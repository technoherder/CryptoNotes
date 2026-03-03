using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace CryptoNotes.Services
{
    /// <summary>
    /// Core security service for CryptoNotes.
    /// Handles app-level password protection, AES-256 encryption of sensitive data,
    /// failed login attempt tracking, and auto-wipe on too many failures.
    ///
    /// Architecture (v2):
    /// - App password is never stored. Only a salted PBKDF2 hash is stored.
    /// - A separate AES-256 data encryption key (DEK) is generated randomly.
    /// - The DEK is stored in hardware keystore (iOS Keychain / Android Keystore) when available.
    /// - Fallback: DEK is encrypted with a key derived from the app password (KEK).
    /// - Failed attempt counters are encrypted with a device-bound key to prevent tampering.
    /// - All sensitive fields are encrypted with the DEK before SQLite storage.
    /// </summary>
    public class SecurityService
    {
        private static readonly string SecurityFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ".cryptonotes_security");

        private const int PBKDF2_ITERATIONS = 100000;
        private const int SALT_SIZE = 32;
        private const int KEY_SIZE = 32; // AES-256
        private const int IV_SIZE = 16;
        private const int MAX_FAILED_ATTEMPTS_DEFAULT = 5;
        private const int SECURITY_FILE_VERSION = 2;

        // Key IDs for hardware keystore
        private const string DEK_KEY_ID = "cryptonotes_dek";
        private const string ATTEMPT_KEY_ID = "cryptonotes_attempt_key";

        private byte[] _dataEncryptionKey;
        private bool _isUnlocked;
        private ISecureKeyStorageService _secureKeyStorage;
        private bool _secureKeyStorageInitialized;

        public bool IsUnlocked => _isUnlocked;
        public bool IsSetUp => File.Exists(SecurityFilePath);

        public SecurityService()
        {
            // Lazy initialization to avoid issues with DependencyService during startup
        }

        private ISecureKeyStorageService SecureKeyStorage
        {
            get
            {
                if (!_secureKeyStorageInitialized)
                {
                    _secureKeyStorage = DependencyService.Get<ISecureKeyStorageService>();
                    _secureKeyStorageInitialized = true;
                }
                return _secureKeyStorage;
            }
        }

        private bool UseHardwareKeystore => SecureKeyStorage?.IsHardwareBackedAvailable ?? false;

        /// <summary>
        /// Initialize the security service asynchronously.
        /// Should be called early in app startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Ensure attempt counter key exists
            if (SecureKeyStorage != null)
            {
                if (!await SecureKeyStorage.KeyExistsAsync(ATTEMPT_KEY_ID))
                {
                    await SecureKeyStorage.GenerateDeviceBoundKeyAsync(ATTEMPT_KEY_ID);
                }
            }

            // Migrate from v1 to v2 if needed
            await MigrateSecurityDataIfNeededAsync();
        }

        /// <summary>
        /// Set up app password for the first time.
        /// Generates a random data encryption key and protects it.
        /// </summary>
        public async Task SetupPasswordAsync(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
                throw new ArgumentException("Password must be at least 8 characters");

            // Initialize first
            await InitializeAsync();

            // Generate random salt for password hashing
            var passwordSalt = new byte[SALT_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(passwordSalt);

            // Hash the password with PBKDF2
            var passwordHash = DeriveKey(password, passwordSalt, PBKDF2_ITERATIONS, 32);

            // Generate the actual data encryption key (DEK) randomly
            _dataEncryptionKey = new byte[KEY_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(_dataEncryptionKey);

            // Store DEK in hardware keystore if available
            string encryptedDek = null;
            string kekSalt = null;
            bool usesHardwareKeystore = false;

            if (UseHardwareKeystore)
            {
                var stored = await SecureKeyStorage.StoreKeyAsync(DEK_KEY_ID, _dataEncryptionKey);
                if (stored)
                {
                    usesHardwareKeystore = true;
                }
            }

            if (!usesHardwareKeystore)
            {
                // Fallback: encrypt DEK with password-derived KEK
                var kekSaltBytes = new byte[SALT_SIZE];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(kekSaltBytes);

                var kek = DeriveKey(password, kekSaltBytes, PBKDF2_ITERATIONS, KEY_SIZE);
                var encryptedDekBytes = AesEncryptRaw(_dataEncryptionKey, kek);

                encryptedDek = Convert.ToBase64String(encryptedDekBytes);
                kekSalt = Convert.ToBase64String(kekSaltBytes);

                Array.Clear(kek, 0, kek.Length);
            }

            // Encrypt failed attempt counters
            var encryptedFailedAttempts = await EncryptCounterAsync(0);
            var encryptedMaxAttempts = await EncryptCounterAsync(MAX_FAILED_ATTEMPTS_DEFAULT);

            // Store security metadata
            var securityData = new SecurityDataV2
            {
                Version = SECURITY_FILE_VERSION,
                PasswordHash = Convert.ToBase64String(passwordHash),
                PasswordSalt = Convert.ToBase64String(passwordSalt),
                EncryptedDek = encryptedDek ?? "",
                KekSalt = kekSalt ?? "",
                EncryptedFailedAttempts = encryptedFailedAttempts,
                EncryptedMaxAttempts = encryptedMaxAttempts,
                Iterations = PBKDF2_ITERATIONS,
                UsesHardwareKeystore = usesHardwareKeystore
            };

            SaveSecurityDataV2(securityData);
            _isUnlocked = true;
        }

        /// <summary>
        /// Synchronous wrapper for SetupPasswordAsync (for backward compatibility).
        /// </summary>
        public void SetupPassword(string password)
        {
            SetupPasswordAsync(password).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Attempt to unlock the app with a password.
        /// Returns true if password is correct, false otherwise.
        /// Triggers auto-wipe if max failed attempts exceeded.
        /// </summary>
        public async Task<bool> TryUnlockAsync(string password)
        {
            // Ensure initialized
            await InitializeAsync();

            var data = LoadSecurityDataV2();
            if (data == null) return false;

            // Get current failed attempts
            var failedAttempts = await DecryptCounterAsync(data.EncryptedFailedAttempts);
            var maxAttempts = await DecryptCounterAsync(data.EncryptedMaxAttempts);

            // Check if already wiped
            if (failedAttempts >= maxAttempts)
            {
                await WipeAllDataAsync();
                return false;
            }

            var passwordSalt = Convert.FromBase64String(data.PasswordSalt);
            var storedHash = Convert.FromBase64String(data.PasswordHash);
            var computedHash = DeriveKey(password, passwordSalt, data.Iterations, 32);

            // Constant-time comparison to prevent timing attacks
            if (!ConstantTimeEquals(storedHash, computedHash))
            {
                // Increment and encrypt failed attempts
                failedAttempts++;
                data.EncryptedFailedAttempts = await EncryptCounterAsync(failedAttempts);
                SaveSecurityDataV2(data);

                if (failedAttempts >= maxAttempts)
                {
                    await WipeAllDataAsync();
                }

                Array.Clear(computedHash, 0, computedHash.Length);
                return false;
            }

            // Password correct - retrieve or decrypt the DEK
            if (data.UsesHardwareKeystore && SecureKeyStorage != null)
            {
                _dataEncryptionKey = await SecureKeyStorage.RetrieveKeyAsync(DEK_KEY_ID);
                if (_dataEncryptionKey == null)
                {
                    // Hardware key lost, cannot decrypt data
                    Array.Clear(computedHash, 0, computedHash.Length);
                    return false;
                }
            }
            else
            {
                // Decrypt DEK using KEK
                var kekSalt = Convert.FromBase64String(data.KekSalt);
                var kek = DeriveKey(password, kekSalt, data.Iterations, KEY_SIZE);
                var encryptedDek = Convert.FromBase64String(data.EncryptedDek);

                try
                {
                    _dataEncryptionKey = AesDecryptRaw(encryptedDek, kek);
                }
                catch
                {
                    Array.Clear(kek, 0, kek.Length);
                    Array.Clear(computedHash, 0, computedHash.Length);
                    return false;
                }
                finally
                {
                    Array.Clear(kek, 0, kek.Length);
                }
            }

            // Reset failed attempts on success
            data.EncryptedFailedAttempts = await EncryptCounterAsync(0);
            SaveSecurityDataV2(data);

            _isUnlocked = true;
            Array.Clear(computedHash, 0, computedHash.Length);
            return true;
        }

        /// <summary>
        /// Synchronous wrapper for TryUnlockAsync (for backward compatibility).
        /// </summary>
        public bool TryUnlock(string password)
        {
            return TryUnlockAsync(password).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Change the app password. Requires the current password.
        /// Re-encrypts the DEK with the new password; data stays intact.
        /// </summary>
        public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
        {
            if (!_isUnlocked || _dataEncryptionKey == null) return false;
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8) return false;

            var data = LoadSecurityDataV2();
            if (data == null) return false;

            // Verify current password
            var passwordSalt = Convert.FromBase64String(data.PasswordSalt);
            var storedHash = Convert.FromBase64String(data.PasswordHash);
            var computedHash = DeriveKey(currentPassword, passwordSalt, data.Iterations, 32);

            if (!ConstantTimeEquals(storedHash, computedHash))
            {
                Array.Clear(computedHash, 0, computedHash.Length);
                return false;
            }

            // Generate new salt and hash for new password
            var newSalt = new byte[SALT_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(newSalt);

            var newHash = DeriveKey(newPassword, newSalt, PBKDF2_ITERATIONS, 32);

            data.PasswordHash = Convert.ToBase64String(newHash);
            data.PasswordSalt = Convert.ToBase64String(newSalt);

            // If not using hardware keystore, re-encrypt DEK with new password
            if (!data.UsesHardwareKeystore)
            {
                var newKekSalt = new byte[SALT_SIZE];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(newKekSalt);

                var newKek = DeriveKey(newPassword, newKekSalt, PBKDF2_ITERATIONS, KEY_SIZE);
                var newEncryptedDek = AesEncryptRaw(_dataEncryptionKey, newKek);

                data.EncryptedDek = Convert.ToBase64String(newEncryptedDek);
                data.KekSalt = Convert.ToBase64String(newKekSalt);

                Array.Clear(newKek, 0, newKek.Length);
            }

            // Reset failed attempts
            data.EncryptedFailedAttempts = await EncryptCounterAsync(0);
            SaveSecurityDataV2(data);

            Array.Clear(computedHash, 0, computedHash.Length);
            Array.Clear(newHash, 0, newHash.Length);
            return true;
        }

        /// <summary>
        /// Synchronous wrapper for ChangePasswordAsync (for backward compatibility).
        /// </summary>
        public bool ChangePassword(string currentPassword, string newPassword)
        {
            return ChangePasswordAsync(currentPassword, newPassword).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get the number of remaining unlock attempts before auto-wipe.
        /// </summary>
        public async Task<int> GetRemainingAttemptsAsync()
        {
            var data = LoadSecurityDataV2();
            if (data == null) return 0;

            var failed = await DecryptCounterAsync(data.EncryptedFailedAttempts);
            var max = await DecryptCounterAsync(data.EncryptedMaxAttempts);

            return Math.Max(0, max - failed);
        }

        /// <summary>
        /// Synchronous wrapper for GetRemainingAttemptsAsync.
        /// </summary>
        public int GetRemainingAttempts()
        {
            return GetRemainingAttemptsAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get the maximum number of failed attempts allowed.
        /// </summary>
        public async Task<int> GetMaxAttemptsAsync()
        {
            var data = LoadSecurityDataV2();
            if (data == null) return MAX_FAILED_ATTEMPTS_DEFAULT;
            return await DecryptCounterAsync(data.EncryptedMaxAttempts);
        }

        /// <summary>
        /// Synchronous wrapper for GetMaxAttemptsAsync.
        /// </summary>
        public int GetMaxAttempts()
        {
            return GetMaxAttemptsAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Set the maximum number of failed attempts before auto-wipe.
        /// </summary>
        public async Task SetMaxAttemptsAsync(int maxAttempts)
        {
            if (maxAttempts < 3 || maxAttempts > 20) return;
            var data = LoadSecurityDataV2();
            if (data == null) return;

            data.EncryptedMaxAttempts = await EncryptCounterAsync(maxAttempts);
            SaveSecurityDataV2(data);
        }

        /// <summary>
        /// Synchronous wrapper for SetMaxAttemptsAsync.
        /// </summary>
        public void SetMaxAttempts(int maxAttempts)
        {
            SetMaxAttemptsAsync(maxAttempts).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Encrypt a string using AES-256-CBC with the app's data encryption key.
        /// Returns Base64-encoded ciphertext (IV prepended).
        /// </summary>
        public string EncryptString(string plainText)
        {
            if (!_isUnlocked || _dataEncryptionKey == null)
                throw new InvalidOperationException("App is locked");
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = AesEncryptRaw(plainBytes, _dataEncryptionKey);
            return Convert.ToBase64String(encrypted);
        }

        /// <summary>
        /// Decrypt a Base64-encoded AES-256-CBC ciphertext using the app's data encryption key.
        /// </summary>
        public string DecryptString(string cipherText)
        {
            if (!_isUnlocked || _dataEncryptionKey == null)
                throw new InvalidOperationException("App is locked");
            if (string.IsNullOrEmpty(cipherText))
                return cipherText;

            try
            {
                var cipherBytes = Convert.FromBase64String(cipherText);
                var decrypted = AesDecryptRaw(cipherBytes, _dataEncryptionKey);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // If decryption fails (data was stored before encryption was enabled),
                // return the original string
                return cipherText;
            }
        }

        /// <summary>
        /// Wipe all application data - called on too many failed attempts
        /// or manually by the user.
        /// </summary>
        public async Task WipeAllDataAsync()
        {
            // Clear encryption key from memory
            if (_dataEncryptionKey != null)
            {
                Array.Clear(_dataEncryptionKey, 0, _dataEncryptionKey.Length);
                _dataEncryptionKey = null;
            }

            _isUnlocked = false;

            // Wipe keys from hardware keystore
            if (SecureKeyStorage != null)
            {
                await SecureKeyStorage.WipeAllKeysAsync();
            }

            // Delete the security file
            SecureDeleteFile(SecurityFilePath);

            // Delete the databases (both old and new)
            SecureDeleteFile(Constants.DatabasePath);
            SecureDeleteFile(Constants.DatabasePath + "-shm");
            SecureDeleteFile(Constants.DatabasePath + "-wal");
            SecureDeleteFile(Constants.DatabasePath + "-journal");

            if (Constants.EncryptedDatabasePath != Constants.DatabasePath)
            {
                SecureDeleteFile(Constants.EncryptedDatabasePath);
                SecureDeleteFile(Constants.EncryptedDatabasePath + "-shm");
                SecureDeleteFile(Constants.EncryptedDatabasePath + "-wal");
                SecureDeleteFile(Constants.EncryptedDatabasePath + "-journal");
            }

            // Delete migration flag
            var migrationFlagPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ".db_migration_complete");
            SecureDeleteFile(migrationFlagPath);

            // Delete DB salt
            var dbSaltPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ".db_salt");
            SecureDeleteFile(dbSaltPath);

            // Clean up any temp files in the app directory
            var appDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                foreach (var file in Directory.GetFiles(appDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".asc" || ext == ".pgp" || ext == ".txt")
                    {
                        SecureDeleteFile(file);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Synchronous wrapper for WipeAllDataAsync.
        /// </summary>
        public void WipeAllData()
        {
            WipeAllDataAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Lock the app - clear the DEK from memory.
        /// </summary>
        public void Lock()
        {
            if (_dataEncryptionKey != null)
            {
                Array.Clear(_dataEncryptionKey, 0, _dataEncryptionKey.Length);
                _dataEncryptionKey = null;
            }
            _isUnlocked = false;
        }

        #region Counter Encryption

        private async Task<string> EncryptCounterAsync(int value)
        {
            var bytes = BitConverter.GetBytes(value);

            if (SecureKeyStorage != null)
            {
                var encrypted = await SecureKeyStorage.EncryptWithDeviceBoundKeyAsync(ATTEMPT_KEY_ID, bytes);
                if (encrypted != null)
                {
                    return Convert.ToBase64String(encrypted);
                }
            }

            // Fallback: just encode (less secure, but still requires file access)
            return "plain:" + value.ToString();
        }

        private async Task<int> DecryptCounterAsync(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted))
                return 0;

            // Check for plaintext fallback
            if (encrypted.StartsWith("plain:"))
            {
                if (int.TryParse(encrypted.Substring(6), out var plainValue))
                    return plainValue;
                return 0;
            }

            if (SecureKeyStorage != null)
            {
                try
                {
                    var encryptedBytes = Convert.FromBase64String(encrypted);
                    var decrypted = await SecureKeyStorage.DecryptWithDeviceBoundKeyAsync(ATTEMPT_KEY_ID, encryptedBytes);
                    if (decrypted != null && decrypted.Length >= 4)
                    {
                        return BitConverter.ToInt32(decrypted, 0);
                    }
                }
                catch { }
            }

            return 0;
        }

        #endregion

        #region Crypto Primitives

        private static byte[] DeriveKey(string password, byte[] salt, int iterations, int keyLength)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256))
            {
                return pbkdf2.GetBytes(keyLength);
            }
        }

        private static byte[] AesEncryptRaw(byte[] plainBytes, byte[] key)
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
                    var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    // Prepend IV to ciphertext
                    var result = new byte[IV_SIZE + encrypted.Length];
                    Buffer.BlockCopy(aes.IV, 0, result, 0, IV_SIZE);
                    Buffer.BlockCopy(encrypted, 0, result, IV_SIZE, encrypted.Length);
                    return result;
                }
            }
        }

        private static byte[] AesDecryptRaw(byte[] cipherBytesWithIv, byte[] key)
        {
            if (cipherBytesWithIv.Length < IV_SIZE + 1)
                throw new CryptographicException("Invalid ciphertext");

            var iv = new byte[IV_SIZE];
            Buffer.BlockCopy(cipherBytesWithIv, 0, iv, 0, IV_SIZE);

            var cipherBytes = new byte[cipherBytesWithIv.Length - IV_SIZE];
            Buffer.BlockCopy(cipherBytesWithIv, IV_SIZE, cipherBytes, 0, cipherBytes.Length);

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

        /// <summary>
        /// Constant-time byte array comparison to prevent timing attacks.
        /// </summary>
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int result = 0;
            for (int i = 0; i < a.Length; i++)
                result |= a[i] ^ b[i];
            return result == 0;
        }

        private static void SecureDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var length = new FileInfo(path).Length;
                if (length > 0)
                {
                    // Three-pass overwrite: zeros, ones, random
                    var buffer = new byte[length];
                    File.WriteAllBytes(path, buffer); // Pass 1: zeros

                    for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xFF;
                    File.WriteAllBytes(path, buffer); // Pass 2: ones

                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(buffer);
                    File.WriteAllBytes(path, buffer); // Pass 3: random

                    Array.Clear(buffer, 0, buffer.Length);
                }
                File.Delete(path);
            }
            catch
            {
                try { File.Delete(path); } catch { }
            }
        }

        #endregion

        #region Security Data Persistence

        private async Task MigrateSecurityDataIfNeededAsync()
        {
            if (!File.Exists(SecurityFilePath)) return;

            try
            {
                var lines = File.ReadAllLines(SecurityFilePath);

                // Check if already v2 (first line is version number)
                if (lines.Length > 0 && lines[0] == SECURITY_FILE_VERSION.ToString())
                    return;

                // V1 format: 7 lines without version
                if (lines.Length == 7)
                {
                    var v1Data = new SecurityDataV1
                    {
                        PasswordHash = lines[0],
                        PasswordSalt = lines[1],
                        EncryptedDek = lines[2],
                        KekSalt = lines[3],
                        FailedAttempts = int.Parse(lines[4]),
                        MaxFailedAttempts = int.Parse(lines[5]),
                        Iterations = int.Parse(lines[6])
                    };

                    // Convert to v2
                    var encryptedFailed = await EncryptCounterAsync(v1Data.FailedAttempts);
                    var encryptedMax = await EncryptCounterAsync(v1Data.MaxFailedAttempts);

                    var v2Data = new SecurityDataV2
                    {
                        Version = SECURITY_FILE_VERSION,
                        PasswordHash = v1Data.PasswordHash,
                        PasswordSalt = v1Data.PasswordSalt,
                        EncryptedDek = v1Data.EncryptedDek,
                        KekSalt = v1Data.KekSalt,
                        EncryptedFailedAttempts = encryptedFailed,
                        EncryptedMaxAttempts = encryptedMax,
                        Iterations = v1Data.Iterations,
                        UsesHardwareKeystore = false
                    };

                    SaveSecurityDataV2(v2Data);
                }
            }
            catch { }
        }

        private void SaveSecurityDataV2(SecurityDataV2 data)
        {
            var lines = new[]
            {
                data.Version.ToString(),
                data.PasswordHash,
                data.PasswordSalt,
                data.EncryptedDek,
                data.KekSalt,
                data.EncryptedFailedAttempts,
                data.EncryptedMaxAttempts,
                data.Iterations.ToString(),
                data.UsesHardwareKeystore.ToString()
            };
            File.WriteAllLines(SecurityFilePath, lines);
        }

        private SecurityDataV2 LoadSecurityDataV2()
        {
            if (!File.Exists(SecurityFilePath)) return null;
            try
            {
                var lines = File.ReadAllLines(SecurityFilePath);

                // Check for v2 format
                if (lines.Length >= 9 && lines[0] == SECURITY_FILE_VERSION.ToString())
                {
                    return new SecurityDataV2
                    {
                        Version = int.Parse(lines[0]),
                        PasswordHash = lines[1],
                        PasswordSalt = lines[2],
                        EncryptedDek = lines[3],
                        KekSalt = lines[4],
                        EncryptedFailedAttempts = lines[5],
                        EncryptedMaxAttempts = lines[6],
                        Iterations = int.Parse(lines[7]),
                        UsesHardwareKeystore = bool.Parse(lines[8])
                    };
                }

                // V1 format fallback (shouldn't happen after migration)
                if (lines.Length >= 7)
                {
                    return new SecurityDataV2
                    {
                        Version = 1,
                        PasswordHash = lines[0],
                        PasswordSalt = lines[1],
                        EncryptedDek = lines[2],
                        KekSalt = lines[3],
                        EncryptedFailedAttempts = "plain:" + lines[4],
                        EncryptedMaxAttempts = "plain:" + lines[5],
                        Iterations = int.Parse(lines[6]),
                        UsesHardwareKeystore = false
                    };
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private class SecurityDataV1
        {
            public string PasswordHash { get; set; }
            public string PasswordSalt { get; set; }
            public string EncryptedDek { get; set; }
            public string KekSalt { get; set; }
            public int FailedAttempts { get; set; }
            public int MaxFailedAttempts { get; set; }
            public int Iterations { get; set; }
        }

        private class SecurityDataV2
        {
            public int Version { get; set; }
            public string PasswordHash { get; set; }
            public string PasswordSalt { get; set; }
            public string EncryptedDek { get; set; }
            public string KekSalt { get; set; }
            public string EncryptedFailedAttempts { get; set; }
            public string EncryptedMaxAttempts { get; set; }
            public int Iterations { get; set; }
            public bool UsesHardwareKeystore { get; set; }
        }

        #endregion
    }
}
