using SQLite;

namespace CryptoNotes.Models
{
    /// <summary>
    /// Database model for tracking conversations between users.
    /// Stores encrypted usernames with an unencrypted ID for efficient queries.
    /// This enables SQL queries on ConversationId while keeping usernames encrypted.
    /// </summary>
    [Table("ConversationIndex")]
    public class ConversationIndex
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        /// <summary>
        /// First username (encrypted)
        /// </summary>
        public string EncryptedUser1 { get; set; }

        /// <summary>
        /// Second username (encrypted)
        /// </summary>
        public string EncryptedUser2 { get; set; }
    }
}
