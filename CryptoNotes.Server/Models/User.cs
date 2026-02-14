using System;
using System.ComponentModel.DataAnnotations;

namespace CryptoNotes.Server.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string Username { get; set; }

        [Required]
        public string PasswordHash { get; set; }

        [Required]
        public string PublicKey { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }
}
