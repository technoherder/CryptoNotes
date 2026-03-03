using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Foundation;
using Security;
using CryptoNotes.Services;
using Xamarin.Forms;

[assembly: Dependency(typeof(CryptoNotes.iOS.Services.SecureKeyStorageService))]

namespace CryptoNotes.iOS.Services
{
    /// <summary>
    /// iOS implementation of secure key storage using Keychain.
    /// Keys are stored with ThisDeviceOnly accessibility for maximum security.
    /// </summary>
    public class SecureKeyStorageService : ISecureKeyStorageService
    {
        private const string ServiceName = "com.cryptonotes.keystore";
        private const int AesKeySize = 32; // 256 bits
        private const int IvSize = 16;

        public bool IsHardwareBackedAvailable
        {
            get
            {
                // iOS Keychain is always hardware-backed on devices with Secure Enclave (iPhone 5s+)
                // For simplicity, we consider iOS 9+ as having adequate security
                return UIKit.UIDevice.CurrentDevice.CheckSystemVersion(9, 0);
            }
        }

        public Task<bool> StoreKeyAsync(string keyId, byte[] keyData)
        {
            return Task.Run(() =>
            {
                try
                {
                    // First, try to delete any existing key
                    DeleteKey(keyId);

                    var record = new SecRecord(SecKind.GenericPassword)
                    {
                        Service = ServiceName,
                        Account = keyId,
                        ValueData = NSData.FromArray(keyData),
                        Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
                    };

                    var result = SecKeyChain.Add(record);
                    return result == SecStatusCode.Success;
                }
                catch
                {
                    return false;
                }
            });
        }

        public Task<byte[]> RetrieveKeyAsync(string keyId)
        {
            return Task.Run(() =>
            {
                try
                {
                    var query = new SecRecord(SecKind.GenericPassword)
                    {
                        Service = ServiceName,
                        Account = keyId,
                    };

                    SecStatusCode status;
                    var match = SecKeyChain.QueryAsRecord(query, out status);

                    if (status == SecStatusCode.Success && match?.ValueData != null)
                    {
                        return match.ValueData.ToArray();
                    }

                    return null;
                }
                catch
                {
                    return null;
                }
            });
        }

        public Task<bool> DeleteKeyAsync(string keyId)
        {
            return Task.Run(() => DeleteKey(keyId));
        }

        private bool DeleteKey(string keyId)
        {
            try
            {
                var record = new SecRecord(SecKind.GenericPassword)
                {
                    Service = ServiceName,
                    Account = keyId,
                };

                var result = SecKeyChain.Remove(record);
                return result == SecStatusCode.Success || result == SecStatusCode.ItemNotFound;
            }
            catch
            {
                return false;
            }
        }

        public Task<bool> KeyExistsAsync(string keyId)
        {
            return Task.Run(() =>
            {
                try
                {
                    var query = new SecRecord(SecKind.GenericPassword)
                    {
                        Service = ServiceName,
                        Account = keyId,
                    };

                    SecStatusCode status;
                    SecKeyChain.QueryAsRecord(query, out status);
                    return status == SecStatusCode.Success;
                }
                catch
                {
                    return false;
                }
            });
        }

        public Task<bool> GenerateDeviceBoundKeyAsync(string keyId)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Generate a random 256-bit key
                    var keyData = new byte[AesKeySize];
                    using (var rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(keyData);
                    }

                    // Store with ThisDeviceOnly accessibility
                    var success = StoreKeyAsync(keyId, keyData).Result;

                    // Clear the key from memory
                    Array.Clear(keyData, 0, keyData.Length);

                    return success;
                }
                catch
                {
                    return false;
                }
            });
        }

        public async Task<byte[]> EncryptWithDeviceBoundKeyAsync(string keyId, byte[] plainData)
        {
            if (plainData == null || plainData.Length == 0)
                return null;

            var key = await RetrieveKeyAsync(keyId);
            if (key == null)
                return null;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = key;
                    aes.GenerateIV();

                    using (var encryptor = aes.CreateEncryptor())
                    {
                        var encrypted = encryptor.TransformFinalBlock(plainData, 0, plainData.Length);

                        // Prepend IV to ciphertext
                        var result = new byte[IvSize + encrypted.Length];
                        Buffer.BlockCopy(aes.IV, 0, result, 0, IvSize);
                        Buffer.BlockCopy(encrypted, 0, result, IvSize, encrypted.Length);
                        return result;
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        public async Task<byte[]> DecryptWithDeviceBoundKeyAsync(string keyId, byte[] encryptedData)
        {
            if (encryptedData == null || encryptedData.Length < IvSize + 1)
                return null;

            var key = await RetrieveKeyAsync(keyId);
            if (key == null)
                return null;

            try
            {
                // Extract IV from beginning of ciphertext
                var iv = new byte[IvSize];
                Buffer.BlockCopy(encryptedData, 0, iv, 0, IvSize);

                var cipherBytes = new byte[encryptedData.Length - IvSize];
                Buffer.BlockCopy(encryptedData, IvSize, cipherBytes, 0, cipherBytes.Length);

                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = key;
                    aes.IV = iv;

                    using (var decryptor = aes.CreateDecryptor())
                    {
                        return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        public async Task WipeAllKeysAsync()
        {
            // Delete all known keys
            var keyIds = new[]
            {
                "cryptonotes_dek",
                "cryptonotes_attempt_key"
            };

            foreach (var keyId in keyIds)
            {
                await DeleteKeyAsync(keyId);
            }
        }
    }
}
