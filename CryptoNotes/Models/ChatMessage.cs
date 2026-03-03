using System;
using SQLite;

namespace CryptoNotes.Models
{
    /// <summary>
    /// Stores decrypted messages locally on device.
    /// Messages are only stored in plaintext on the user's device, never on the server.
    /// All fields except Id, ConversationId, ServerId, and IsOutgoing are encrypted.
    /// </summary>
    public class ChatMessage
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>
        /// Foreign key to ConversationIndex for efficient queries.
        /// This is unencrypted to allow SQL queries.
        /// </summary>
        public int ConversationId { get; set; }

        public int ServerId { get; set; }

        /// <summary>
        /// Sender username (encrypted)
        /// </summary>
        public string SenderUsername { get; set; }

        /// <summary>
        /// Recipient username (encrypted)
        /// </summary>
        public string RecipientUsername { get; set; }

        /// <summary>
        /// The decrypted plaintext message (encrypted in database)
        /// </summary>
        public string PlainText { get; set; }

        /// <summary>
        /// Timestamp stored as encrypted string (ticks)
        /// </summary>
        public string SentAt { get; set; }

        public bool IsOutgoing { get; set; }

        /// <summary>
        /// In-memory DateTime for use after decryption
        /// </summary>
        [Ignore]
        public DateTime SentAtDateTime { get; set; }
    }
}
