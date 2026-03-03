using System.Threading.Tasks;

namespace CryptoNotes.Services
{
    /// <summary>
    /// Platform-specific secure key storage abstraction.
    /// Uses iOS Keychain on iOS, Android Keystore on Android.
    /// Provides hardware-backed key storage when available.
    /// </summary>
    public interface ISecureKeyStorageService
    {
        /// <summary>
        /// Check if hardware-backed key storage is available on this device.
        /// </summary>
        bool IsHardwareBackedAvailable { get; }

        /// <summary>
        /// Store a key securely in platform keystore.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <param name="keyData">The key bytes to store</param>
        /// <returns>True if successfully stored</returns>
        Task<bool> StoreKeyAsync(string keyId, byte[] keyData);

        /// <summary>
        /// Retrieve a key from secure storage.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <returns>The key bytes, or null if not found</returns>
        Task<byte[]> RetrieveKeyAsync(string keyId);

        /// <summary>
        /// Delete a key from secure storage.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <returns>True if successfully deleted</returns>
        Task<bool> DeleteKeyAsync(string keyId);

        /// <summary>
        /// Check if a key exists in secure storage.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        Task<bool> KeyExistsAsync(string keyId);

        /// <summary>
        /// Generate a device-bound key that cannot be extracted.
        /// Used for encrypting local-only data like failed attempt counter.
        /// The key is generated and stored in hardware keystore.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <returns>True if successfully generated</returns>
        Task<bool> GenerateDeviceBoundKeyAsync(string keyId);

        /// <summary>
        /// Encrypt data using a device-bound key.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <param name="plainData">Data to encrypt</param>
        /// <returns>Encrypted data with IV prepended, or null on failure</returns>
        Task<byte[]> EncryptWithDeviceBoundKeyAsync(string keyId, byte[] plainData);

        /// <summary>
        /// Decrypt data using a device-bound key.
        /// </summary>
        /// <param name="keyId">Unique identifier for the key</param>
        /// <param name="encryptedData">Encrypted data with IV prepended</param>
        /// <returns>Decrypted data, or null on failure</returns>
        Task<byte[]> DecryptWithDeviceBoundKeyAsync(string keyId, byte[] encryptedData);

        /// <summary>
        /// Securely delete all keys managed by this service.
        /// Called during data wipe.
        /// </summary>
        Task WipeAllKeysAsync();
    }
}
