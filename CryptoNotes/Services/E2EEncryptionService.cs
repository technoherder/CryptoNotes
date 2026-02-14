using System;
using System.IO;
using System.Threading.Tasks;
using PgpCore;

namespace CryptoNotes.Services
{
    /// <summary>
    /// Wraps PGP encryption/decryption for the messaging flow.
    /// All encryption happens on-device before messages are sent to the relay server.
    /// All decryption happens on-device after messages are received from the server.
    /// The server only ever sees PGP-encrypted ciphertext.
    /// </summary>
    public class E2EEncryptionService
    {
        private readonly string _basePath;

        public E2EEncryptionService()
        {
            _basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        /// <summary>
        /// Encrypt a plaintext message with the recipient's PGP public key
        /// and optionally sign it with the sender's private key.
        /// </summary>
        /// <param name="plainText">The message to encrypt</param>
        /// <param name="recipientPublicKey">Recipient's PGP public key</param>
        /// <param name="senderPrivateKey">Sender's PGP private key (for signing)</param>
        /// <param name="senderPassword">Password for the sender's private key</param>
        /// <returns>PGP-encrypted message string</returns>
        public async Task<string> EncryptMessageAsync(
            string plainText,
            string recipientPublicKey,
            string senderPrivateKey = null,
            string senderPassword = null)
        {
            // Use unique file names to avoid race conditions
            var id = Guid.NewGuid().ToString("N");
            var publicFile = Path.Combine(_basePath, $"msg_pub_{id}.asc");
            var messageFile = Path.Combine(_basePath, $"msg_plain_{id}.txt");
            var encryptedFile = Path.Combine(_basePath, $"msg_enc_{id}.pgp");

            try
            {
                File.WriteAllText(publicFile, recipientPublicKey);
                File.WriteAllText(messageFile, plainText);

                using (PGP pgp = new PGP())
                {
                    if (!string.IsNullOrEmpty(senderPrivateKey) && !string.IsNullOrEmpty(senderPassword))
                    {
                        var privateFile = Path.Combine(_basePath, $"msg_priv_{id}.asc");
                        try
                        {
                            File.WriteAllText(privateFile, senderPrivateKey);
                            await pgp.EncryptFileAndSignAsync(
                                messageFile, encryptedFile,
                                publicFile, privateFile,
                                senderPassword, true, true);
                        }
                        finally
                        {
                            SecureDeleteFile(privateFile);
                        }
                    }
                    else
                    {
                        await pgp.EncryptFileAsync(
                            messageFile, encryptedFile,
                            publicFile, true, true);
                    }
                }

                return File.ReadAllText(encryptedFile);
            }
            finally
            {
                SecureDeleteFile(publicFile);
                SecureDeleteFile(messageFile);
                SecureDeleteFile(encryptedFile);
            }
        }

        /// <summary>
        /// Decrypt a PGP-encrypted message with the recipient's private key.
        /// </summary>
        /// <param name="encryptedMessage">The PGP-encrypted message</param>
        /// <param name="privateKey">The recipient's PGP private key</param>
        /// <param name="password">Password for the private key</param>
        /// <returns>Decrypted plaintext</returns>
        public async Task<string> DecryptMessageAsync(
            string encryptedMessage,
            string privateKey,
            string password)
        {
            var id = Guid.NewGuid().ToString("N");
            var privateFile = Path.Combine(_basePath, $"dec_priv_{id}.asc");
            var encryptedFile = Path.Combine(_basePath, $"dec_enc_{id}.pgp");
            var decryptedFile = Path.Combine(_basePath, $"dec_plain_{id}.txt");

            try
            {
                File.WriteAllText(privateFile, privateKey);
                File.WriteAllText(encryptedFile, encryptedMessage);

                using (PGP pgp = new PGP())
                {
                    await pgp.DecryptFileAsync(
                        encryptedFile, decryptedFile,
                        privateFile, password);
                }

                return File.ReadAllText(decryptedFile);
            }
            finally
            {
                SecureDeleteFile(privateFile);
                SecureDeleteFile(encryptedFile);
                SecureDeleteFile(decryptedFile);
            }
        }

        /// <summary>
        /// Overwrite file contents before deleting to prevent recovery.
        /// </summary>
        private static void SecureDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var length = new FileInfo(path).Length;
                    var zeros = new byte[length];
                    File.WriteAllBytes(path, zeros);
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup
                try { File.Delete(path); } catch { }
            }
        }
    }
}
