using System;
using SQLite;

namespace CryptoNotes.Models
{
    /// <summary>
    /// Stores decrypted messages locally on device.
    /// Messages are only stored in plaintext on the user's device, never on the server.
    /// </summary>
    public class ChatMessage
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public int ServerId { get; set; }

        public string SenderUsername { get; set; }

        public string RecipientUsername { get; set; }

        /// <summary>
        /// The decrypted plaintext message, stored only locally.
        /// </summary>
        public string PlainText { get; set; }

        public DateTime SentAt { get; set; }

        public bool IsOutgoing { get; set; }
    }
}
