namespace CryptoNotes.Models
{
    public class Conversation
    {
        public string Username { get; set; }
        public int UnreadCount { get; set; }
        public string LastMessageAt { get; set; }
        public string LastMessagePreview { get; set; }
    }
}
