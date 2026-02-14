using System;
using System.ComponentModel.DataAnnotations;

namespace CryptoNotes.Server.Models
{
    public class Message
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public string SenderUsername { get; set; }

        [Required]
        public string RecipientUsername { get; set; }

        /// <summary>
        /// The PGP-encrypted message content. The server never sees plaintext.
        /// </summary>
        [Required]
        public string EncryptedContent { get; set; }

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether the recipient has fetched this message.
        /// </summary>
        public bool Delivered { get; set; } = false;
    }
}
