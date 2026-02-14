using System.ComponentModel.DataAnnotations;

namespace CryptoNotes.Server.Models
{
    public class RegisterRequest
    {
        [Required]
        [MinLength(3)]
        [MaxLength(50)]
        public string Username { get; set; }

        [Required]
        [MinLength(6)]
        public string Password { get; set; }

        [Required]
        public string PublicKey { get; set; }
    }

    public class LoginRequest
    {
        [Required]
        public string Username { get; set; }

        [Required]
        public string Password { get; set; }
    }

    public class LoginResponse
    {
        public string Token { get; set; }
        public string Username { get; set; }
    }

    public class SendMessageRequest
    {
        [Required]
        public string RecipientUsername { get; set; }

        [Required]
        public string EncryptedContent { get; set; }
    }

    public class UserInfo
    {
        public string Username { get; set; }
        public string PublicKey { get; set; }
    }

    public class MessageResponse
    {
        public int Id { get; set; }
        public string SenderUsername { get; set; }
        public string EncryptedContent { get; set; }
        public string SentAt { get; set; }
    }
}
