using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CryptoNotes.Services;
using Xamarin.Forms;

[assembly: Dependency(typeof(CryptoNotes.Droid.Services.SecureKeyStorageService))]

namespace CryptoNotes.Droid.Services
{
    /// <summary>
    /// Android implementation of secure key storage using Android Keystore.
    /// Uses hardware-backed keystore when available (API 23+).
    /// Falls back to encrypted SharedPreferences for arbitrary key material storage.
    /// </summary>
    public class SecureKeyStorageService : ISecureKeyStorageService
    {
        private const string AndroidKeyStore = "AndroidKeyStore";
        private const string KeyAliasPrefix = "CryptoNotesKey_";
        private const string MasterKeyAlias = "CryptoNotesMasterKey";
        private const string Transformation = "AES/GCM/NoPadding";
        private const string PrefsName = "CryptoNotesSecureKeys";
        private const int GcmTagLength = 128;

        private KeyStore _keyStore;

        public SecureKeyStorageService()
        {
            _keyStore = KeyStore.GetInstance(AndroidKeyStore);
            _keyStore.Load(null);
        }

        public bool IsHardwareBackedAvailable
        {
            get
            {
                // Hardware-backed keystore available on API 23+ (Marshmallow)
                return Build.VERSION.SdkInt >= BuildVersionCodes.M;
            }
        }

        public Task<bool> StoreKeyAsync(string keyId, byte[] keyData)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Ensure master key exists for encrypting arbitrary key material
                    EnsureMasterKeyExists();

                    // Encrypt the key data with the master key
                    var encryptedData = EncryptWithMasterKey(keyData);
                    if (encryptedData == null)
                        return false;

                    // Store encrypted key in SharedPreferences
                    var prefs = Android.App.Application.Context.GetSharedPreferences(
                        PrefsName, FileCreationMode.Private);
                    var editor = prefs.Edit();
                    editor.PutString(keyId, Convert.ToBase64String(encryptedData));
                    return editor.Commit();
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
                    var prefs = Android.App.Application.Context.GetSharedPreferences(
                        PrefsName, FileCreationMode.Private);
                    var encryptedBase64 = prefs.GetString(keyId, null);
                    if (string.IsNullOrEmpty(encryptedBase64))
                        return null;

                    var encryptedData = Convert.FromBase64String(encryptedBase64);
                    return DecryptWithMasterKey(encryptedData);
                }
                catch
                {
                    return null;
                }
            });
        }

        public Task<bool> DeleteKeyAsync(string keyId)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Delete from SharedPreferences
                    var prefs = Android.App.Application.Context.GetSharedPreferences(
                        PrefsName, FileCreationMode.Private);
                    var editor = prefs.Edit();
                    editor.Remove(keyId);
                    var result = editor.Commit();

                    // Also try to delete from keystore if it's a device-bound key
                    var fullAlias = KeyAliasPrefix + keyId;
                    if (_keyStore.ContainsAlias(fullAlias))
                    {
                        _keyStore.DeleteEntry(fullAlias);
                    }

                    return result;
                }
                catch
                {
                    return false;
                }
            });
        }

        public Task<bool> KeyExistsAsync(string keyId)
        {
            return Task.Run(() =>
            {
                try
                {
                    // Check SharedPreferences first
                    var prefs = Android.App.Application.Context.GetSharedPreferences(
                        PrefsName, FileCreationMode.Private);
                    if (prefs.Contains(keyId))
                        return true;

                    // Also check keystore for device-bound keys
                    var fullAlias = KeyAliasPrefix + keyId;
                    return _keyStore.ContainsAlias(fullAlias);
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
                    if (Build.VERSION.SdkInt < BuildVersionCodes.M)
                    {
                        // Fallback for older devices: generate random key and store encrypted
                        var keyData = new byte[32];
                        using (var rng = RandomNumberGenerator.Create())
                        {
                            rng.GetBytes(keyData);
                        }
                        var success = StoreKeyAsync(keyId, keyData).Result;
                        Array.Clear(keyData, 0, keyData.Length);
                        return success;
                    }

                    var fullAlias = KeyAliasPrefix + keyId;

                    // Delete existing key if present
                    if (_keyStore.ContainsAlias(fullAlias))
                    {
                        _keyStore.DeleteEntry(fullAlias);
                    }

                    var keyGenerator = KeyGenerator.GetInstance(
                        KeyProperties.KeyAlgorithmAes, AndroidKeyStore);

                    var builder = new KeyGenParameterSpec.Builder(
                        fullAlias,
                        KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                        .SetBlockModes(KeyProperties.BlockModeGcm)
                        .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                        .SetKeySize(256)
                        .SetRandomizedEncryptionRequired(true);

                    keyGenerator.Init(builder.Build());
                    keyGenerator.GenerateKey();
                    return true;
                }
                catch
                {
                    return false;
                }
            });
        }

        public Task<byte[]> EncryptWithDeviceBoundKeyAsync(string keyId, byte[] plainData)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (plainData == null || plainData.Length == 0)
                        return null;

                    var fullAlias = KeyAliasPrefix + keyId;

                    // Check if this is a hardware-backed key
                    if (_keyStore.ContainsAlias(fullAlias))
                    {
                        var key = _keyStore.GetKey(fullAlias, null);
                        if (key == null)
                            return null;

                        var cipher = Cipher.GetInstance(Transformation);
                        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);

                        var iv = cipher.GetIV();
                        var encrypted = cipher.DoFinal(plainData);

                        // Format: [IV length (1 byte)][IV][ciphertext]
                        var result = new byte[1 + iv.Length + encrypted.Length];
                        result[0] = (byte)iv.Length;
                        Buffer.BlockCopy(iv, 0, result, 1, iv.Length);
                        Buffer.BlockCopy(encrypted, 0, result, 1 + iv.Length, encrypted.Length);

                        return result;
                    }
                    else
                    {
                        // Fallback: use stored key from SharedPreferences
                        var key = RetrieveKeyAsync(keyId).Result;
                        if (key == null)
                            return null;

                        try
                        {
                            return EncryptWithSoftwareKey(plainData, key);
                        }
                        finally
                        {
                            Array.Clear(key, 0, key.Length);
                        }
                    }
                }
                catch
                {
                    return null;
                }
            });
        }

        public Task<byte[]> DecryptWithDeviceBoundKeyAsync(string keyId, byte[] encryptedData)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (encryptedData == null || encryptedData.Length < 2)
                        return null;

                    var fullAlias = KeyAliasPrefix + keyId;

                    // Check if this is a hardware-backed key
                    if (_keyStore.ContainsAlias(fullAlias))
                    {
                        var key = _keyStore.GetKey(fullAlias, null);
                        if (key == null)
                            return null;

                        int ivLength = encryptedData[0];
                        if (encryptedData.Length < 1 + ivLength + 1)
                            return null;

                        var iv = new byte[ivLength];
                        Buffer.BlockCopy(encryptedData, 1, iv, 0, ivLength);

                        var ciphertext = new byte[encryptedData.Length - 1 - ivLength];
                        Buffer.BlockCopy(encryptedData, 1 + ivLength, ciphertext, 0, ciphertext.Length);

                        var cipher = Cipher.GetInstance(Transformation);
                        cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(GcmTagLength, iv));

                        return cipher.DoFinal(ciphertext);
                    }
                    else
                    {
                        // Fallback: use stored key from SharedPreferences
                        var key = RetrieveKeyAsync(keyId).Result;
                        if (key == null)
                            return null;

                        try
                        {
                            return DecryptWithSoftwareKey(encryptedData, key);
                        }
                        finally
                        {
                            Array.Clear(key, 0, key.Length);
                        }
                    }
                }
                catch
                {
                    return null;
                }
            });
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

            // Also delete master key
            try
            {
                if (_keyStore.ContainsAlias(MasterKeyAlias))
                {
                    _keyStore.DeleteEntry(MasterKeyAlias);
                }
            }
            catch { }

            // Clear SharedPreferences
            try
            {
                var prefs = Android.App.Application.Context.GetSharedPreferences(
                    PrefsName, FileCreationMode.Private);
                var editor = prefs.Edit();
                editor.Clear();
                editor.Commit();
            }
            catch { }
        }

        #region Master Key Management

        private void EnsureMasterKeyExists()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.M)
                return; // Can't use Android Keystore on older devices

            if (_keyStore.ContainsAlias(MasterKeyAlias))
                return;

            var keyGenerator = KeyGenerator.GetInstance(
                KeyProperties.KeyAlgorithmAes, AndroidKeyStore);

            var builder = new KeyGenParameterSpec.Builder(
                MasterKeyAlias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256)
                .SetRandomizedEncryptionRequired(true);

            keyGenerator.Init(builder.Build());
            keyGenerator.GenerateKey();
        }

        private byte[] EncryptWithMasterKey(byte[] data)
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.M)
            {
                // Fallback for older devices: just return the data
                // (security relies on app sandbox)
                var result = new byte[1 + data.Length];
                result[0] = 0; // Version marker for unencrypted
                Buffer.BlockCopy(data, 0, result, 1, data.Length);
                return result;
            }

            var key = _keyStore.GetKey(MasterKeyAlias, null);
            if (key == null)
                return null;

            var cipher = Cipher.GetInstance(Transformation);
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);

            var iv = cipher.GetIV();
            var encrypted = cipher.DoFinal(data);

            // Format: [version (1 byte = 1)][IV length (1 byte)][IV][ciphertext]
            var resultData = new byte[2 + iv.Length + encrypted.Length];
            resultData[0] = 1; // Version marker for encrypted
            resultData[1] = (byte)iv.Length;
            Buffer.BlockCopy(iv, 0, resultData, 2, iv.Length);
            Buffer.BlockCopy(encrypted, 0, resultData, 2 + iv.Length, encrypted.Length);

            return resultData;
        }

        private byte[] DecryptWithMasterKey(byte[] encryptedData)
        {
            if (encryptedData == null || encryptedData.Length < 2)
                return null;

            var version = encryptedData[0];

            if (version == 0)
            {
                // Unencrypted (old device fallback)
                var result = new byte[encryptedData.Length - 1];
                Buffer.BlockCopy(encryptedData, 1, result, 0, result.Length);
                return result;
            }

            if (Build.VERSION.SdkInt < BuildVersionCodes.M)
                return null;

            var key = _keyStore.GetKey(MasterKeyAlias, null);
            if (key == null)
                return null;

            int ivLength = encryptedData[1];
            if (encryptedData.Length < 2 + ivLength + 1)
                return null;

            var iv = new byte[ivLength];
            Buffer.BlockCopy(encryptedData, 2, iv, 0, ivLength);

            var ciphertext = new byte[encryptedData.Length - 2 - ivLength];
            Buffer.BlockCopy(encryptedData, 2 + ivLength, ciphertext, 0, ciphertext.Length);

            var cipher = Cipher.GetInstance(Transformation);
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(GcmTagLength, iv));

            return cipher.DoFinal(ciphertext);
        }

        #endregion

        #region Software Encryption Fallback

        private byte[] EncryptWithSoftwareKey(byte[] plainData, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.GenerateIV();

                using (var encryptor = aes.CreateEncryptor())
                {
                    var encrypted = encryptor.TransformFinalBlock(plainData, 0, plainData.Length);

                    // Format: [IV length (1 byte)][IV][ciphertext]
                    var result = new byte[1 + aes.IV.Length + encrypted.Length];
                    result[0] = (byte)aes.IV.Length;
                    Buffer.BlockCopy(aes.IV, 0, result, 1, aes.IV.Length);
                    Buffer.BlockCopy(encrypted, 0, result, 1 + aes.IV.Length, encrypted.Length);
                    return result;
                }
            }
        }

        private byte[] DecryptWithSoftwareKey(byte[] encryptedData, byte[] key)
        {
            int ivLength = encryptedData[0];
            if (encryptedData.Length < 1 + ivLength + 1)
                return null;

            var iv = new byte[ivLength];
            Buffer.BlockCopy(encryptedData, 1, iv, 0, ivLength);

            var cipherBytes = new byte[encryptedData.Length - 1 - ivLength];
            Buffer.BlockCopy(encryptedData, 1 + ivLength, cipherBytes, 0, cipherBytes.Length);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = key;
                aes.IV = iv;

                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                }
            }
        }

        #endregion
    }
}
