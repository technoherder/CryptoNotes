using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CryptoNotes.Services
{
    /// <summary>
    /// Core security service for CryptoNotes.
    /// Handles app-level password protection, AES-256 encryption of sensitive data,
    /// failed login attempt tracking, and auto-wipe on too many failures.
    ///
    /// Architecture:
    /// - App password is never stored. Only a salted PBKDF2 hash is stored.
    /// - A separate AES-256 data encryption key (DEK) is generated randomly.
    /// - The DEK is encrypted with a key derived from the app password (KEK).
    /// - All sensitive fields are encrypted with the DEK before SQLite storage.
    /// - Security metadata is stored in a separate file (not in the encrypted DB).
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

        private byte[] _dataEncryptionKey;
        private bool _isUnlocked;

        public bool IsUnlocked => _isUnlocked;
        public bool IsSetUp => File.Exists(SecurityFilePath);

        /// <summary>
        /// Set up app password for the first time.
        /// Generates a random data encryption key and protects it with the password.
        /// </summary>
        public void SetupPassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 8)
                throw new ArgumentException("Password must be at least 8 characters");

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

            // Encrypt the DEK with a key derived from the password (KEK)
            var kekSalt = new byte[SALT_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(kekSalt);

            var kek = DeriveKey(password, kekSalt, PBKDF2_ITERATIONS, KEY_SIZE);
            var encryptedDek = AesEncryptRaw(_dataEncryptionKey, kek);

            // Store security metadata
            var securityData = new SecurityData
            {
                PasswordHash = Convert.ToBase64String(passwordHash),
                PasswordSalt = Convert.ToBase64String(passwordSalt),
                EncryptedDek = Convert.ToBase64String(encryptedDek),
                KekSalt = Convert.ToBase64String(kekSalt),
                FailedAttempts = 0,
                MaxFailedAttempts = MAX_FAILED_ATTEMPTS_DEFAULT,
                Iterations = PBKDF2_ITERATIONS
            };

            SaveSecurityData(securityData);
            _isUnlocked = true;

            // Clear the KEK from memory
            Array.Clear(kek, 0, kek.Length);
        }

        /// <summary>
        /// Attempt to unlock the app with a password.
        /// Returns true if password is correct, false otherwise.
        /// Triggers auto-wipe if max failed attempts exceeded.
        /// </summary>
        public bool TryUnlock(string password)
        {
            var data = LoadSecurityData();
            if (data == null) return false;

            // Check if already wiped
            if (data.FailedAttempts >= data.MaxFailedAttempts)
            {
                WipeAllData();
                return false;
            }

            var passwordSalt = Convert.FromBase64String(data.PasswordSalt);
            var storedHash = Convert.FromBase64String(data.PasswordHash);
            var computedHash = DeriveKey(password, passwordSalt, data.Iterations, 32);

            // Constant-time comparison to prevent timing attacks
            if (!ConstantTimeEquals(storedHash, computedHash))
            {
                data.FailedAttempts++;
                SaveSecurityData(data);

                if (data.FailedAttempts >= data.MaxFailedAttempts)
                {
                    WipeAllData();
                }

                Array.Clear(computedHash, 0, computedHash.Length);
                return false;
            }

            // Password correct - decrypt the DEK
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

            // Reset failed attempts on success
            data.FailedAttempts = 0;
            SaveSecurityData(data);

            _isUnlocked = true;

            Array.Clear(kek, 0, kek.Length);
            Array.Clear(computedHash, 0, computedHash.Length);
            return true;
        }

        /// <summary>
        /// Change the app password. Requires the current password.
        /// Re-encrypts the DEK with the new password; data stays intact.
        /// </summary>
        public bool ChangePassword(string currentPassword, string newPassword)
        {
            if (!_isUnlocked || _dataEncryptionKey == null) return false;
            if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8) return false;

            var data = LoadSecurityData();
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

            // Re-encrypt the DEK with new password-derived KEK
            var newKekSalt = new byte[SALT_SIZE];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(newKekSalt);

            var newKek = DeriveKey(newPassword, newKekSalt, PBKDF2_ITERATIONS, KEY_SIZE);
            var newEncryptedDek = AesEncryptRaw(_dataEncryptionKey, newKek);

            data.PasswordHash = Convert.ToBase64String(newHash);
            data.PasswordSalt = Convert.ToBase64String(newSalt);
            data.EncryptedDek = Convert.ToBase64String(newEncryptedDek);
            data.KekSalt = Convert.ToBase64String(newKekSalt);
            data.FailedAttempts = 0;
            SaveSecurityData(data);

            Array.Clear(computedHash, 0, computedHash.Length);
            Array.Clear(newHash, 0, newHash.Length);
            Array.Clear(newKek, 0, newKek.Length);
            return true;
        }

        /// <summary>
        /// Get the number of remaining unlock attempts before auto-wipe.
        /// </summary>
        public int GetRemainingAttempts()
        {
            var data = LoadSecurityData();
            if (data == null) return 0;
            return Math.Max(0, data.MaxFailedAttempts - data.FailedAttempts);
        }

        /// <summary>
        /// Get the maximum number of failed attempts allowed.
        /// </summary>
        public int GetMaxAttempts()
        {
            var data = LoadSecurityData();
            return data?.MaxFailedAttempts ?? MAX_FAILED_ATTEMPTS_DEFAULT;
        }

        /// <summary>
        /// Set the maximum number of failed attempts before auto-wipe.
        /// </summary>
        public void SetMaxAttempts(int maxAttempts)
        {
            if (maxAttempts < 3 || maxAttempts > 20) return;
            var data = LoadSecurityData();
            if (data == null) return;
            data.MaxFailedAttempts = maxAttempts;
            SaveSecurityData(data);
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
        public void WipeAllData()
        {
            // Clear encryption key from memory
            if (_dataEncryptionKey != null)
            {
                Array.Clear(_dataEncryptionKey, 0, _dataEncryptionKey.Length);
                _dataEncryptionKey = null;
            }

            _isUnlocked = false;

            // Delete the security file
            SecureDeleteFile(SecurityFilePath);

            // Delete the database
            var dbPath = Constants.DatabasePath;
            SecureDeleteFile(dbPath);
            SecureDeleteFile(dbPath + "-shm");
            SecureDeleteFile(dbPath + "-wal");
            SecureDeleteFile(dbPath + "-journal");

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

        private void SaveSecurityData(SecurityData data)
        {
            var lines = new[]
            {
                data.PasswordHash,
                data.PasswordSalt,
                data.EncryptedDek,
                data.KekSalt,
                data.FailedAttempts.ToString(),
                data.MaxFailedAttempts.ToString(),
                data.Iterations.ToString()
            };
            File.WriteAllLines(SecurityFilePath, lines);
        }

        private SecurityData LoadSecurityData()
        {
            if (!File.Exists(SecurityFilePath)) return null;
            try
            {
                var lines = File.ReadAllLines(SecurityFilePath);
                if (lines.Length < 7) return null;
                return new SecurityData
                {
                    PasswordHash = lines[0],
                    PasswordSalt = lines[1],
                    EncryptedDek = lines[2],
                    KekSalt = lines[3],
                    FailedAttempts = int.Parse(lines[4]),
                    MaxFailedAttempts = int.Parse(lines[5]),
                    Iterations = int.Parse(lines[6])
                };
            }
            catch
            {
                return null;
            }
        }

        private class SecurityData
        {
            public string PasswordHash { get; set; }
            public string PasswordSalt { get; set; }
            public string EncryptedDek { get; set; }
            public string KekSalt { get; set; }
            public int FailedAttempts { get; set; }
            public int MaxFailedAttempts { get; set; }
            public int Iterations { get; set; }
        }

        #endregion
    }
}
