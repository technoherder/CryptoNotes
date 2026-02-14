using SQLite;

namespace CryptoNotes.Models
{
    /// <summary>
    /// Local user account info for the currently logged-in user.
    /// </summary>
    public class UserAccount
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        public string Username { get; set; }

        public string AuthToken { get; set; }

        public string ServerUrl { get; set; }

        /// <summary>
        /// Reference to the Item.Id of the PGP key pair used for messaging.
        /// </summary>
        public int KeyPairId { get; set; }
    }
}
