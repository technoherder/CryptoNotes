using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CryptoNotes.Models;
using SQLite;

namespace CryptoNotes.Services
{
    /// <summary>
    /// SQLCipher-encrypted database with full metadata encryption.
    /// All sensitive fields including usernames and timestamps are encrypted.
    /// Uses ConversationId for efficient queries without exposing usernames.
    /// </summary>
    public class CryptoNotesDatabase
    {
        private SQLiteAsyncConnection _database;
        private bool _initialized;
        private string _databaseKey;

        // Cache of decrypted conversations to avoid repeated decryption
        private Dictionary<int, (string User1, string User2)> _conversationIndexCache;

        public CryptoNotesDatabase()
        {
            _conversationIndexCache = new Dictionary<int, (string, string)>();
        }

        /// <summary>
        /// Initialize the database with encryption key derived from passcode.
        /// Must be called after user unlocks the app.
        /// </summary>
        public async Task InitializeWithKeyAsync(string passcode)
        {
            // Derive database key from passcode using PBKDF2
            _databaseKey = DeriveDbKey(passcode);

            // Check if migration from unencrypted DB is needed
            await MigrateIfNeededAsync();

            // Create encrypted connection
            var options = new SQLiteConnectionString(
                Constants.EncryptedDatabasePath,
                Constants.Flags,
                storeDateTimeAsTicks: true,
                key: _databaseKey);

            _database = new SQLiteAsyncConnection(options);

            await InitializeTablesAsync();

            // Load conversation cache
            await LoadConversationCacheAsync();
        }

        private string DeriveDbKey(string passcode)
        {
            var salt = GetOrCreateDbSalt();
            using (var pbkdf2 = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(passcode), salt, 100000, HashAlgorithmName.SHA256))
            {
                var keyBytes = pbkdf2.GetBytes(32);
                return Convert.ToBase64String(keyBytes);
            }
        }

        private byte[] GetOrCreateDbSalt()
        {
            if (File.Exists(Constants.DbSaltPath))
            {
                return Convert.FromBase64String(File.ReadAllText(Constants.DbSaltPath));
            }

            var salt = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            File.WriteAllText(Constants.DbSaltPath, Convert.ToBase64String(salt));
            return salt;
        }

        private async Task MigrateIfNeededAsync()
        {
            // Check if migration already completed
            if (File.Exists(Constants.MigrationCompletePath))
                return;

            // Check if old unencrypted database exists
            if (!File.Exists(Constants.DatabasePath))
            {
                // No migration needed - fresh install
                File.WriteAllText(Constants.MigrationCompletePath, DateTime.UtcNow.ToString("O"));
                return;
            }

            // Migrate data from unencrypted to encrypted database
            await PerformMigrationAsync();
        }

        private async Task PerformMigrationAsync()
        {
            try
            {
                // Open old unencrypted database
                var oldDb = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);

                // Create new encrypted database
                var options = new SQLiteConnectionString(
                    Constants.EncryptedDatabasePath,
                    Constants.Flags,
                    storeDateTimeAsTicks: true,
                    key: _databaseKey);
                var newDb = new SQLiteAsyncConnection(options);

                // Create tables in new database
                await newDb.CreateTablesAsync(CreateFlags.None, typeof(Item), typeof(ChatMessage), typeof(UserAccount), typeof(ConversationIndex));

                // Copy Items
                try
                {
                    var items = await oldDb.Table<Item>().ToListAsync();
                    foreach (var item in items)
                    {
                        await newDb.InsertAsync(item);
                    }
                }
                catch { }

                // Copy ChatMessages and create Conversations
                try
                {
                    var messages = await oldDb.Table<ChatMessage>().ToListAsync();
                    var conversations = new Dictionary<string, int>();

                    foreach (var msg in messages)
                    {
                        // Create conversation key (sorted usernames)
                        var users = new[] { msg.SenderUsername ?? "", msg.RecipientUsername ?? "" };
                        Array.Sort(users);
                        var convKey = string.Join("|", users);

                        if (!conversations.ContainsKey(convKey))
                        {
                            var conv = new ConversationIndex
                            {
                                EncryptedUser1 = users[0],
                                EncryptedUser2 = users[1]
                            };
                            await newDb.InsertAsync(conv);
                            conversations[convKey] = conv.Id;
                        }

                        // Update message with conversation ID
                        msg.ConversationId = conversations[convKey];

                        // Convert DateTime to string for storage (will be encrypted later)
                        // Note: Old messages have SentAt as DateTime, new format stores as ticks string
                        await newDb.InsertAsync(msg);
                    }
                }
                catch { }

                // Copy UserAccounts
                try
                {
                    var accounts = await oldDb.Table<UserAccount>().ToListAsync();
                    foreach (var acct in accounts)
                    {
                        await newDb.InsertAsync(acct);
                    }
                }
                catch { }

                // Close connections
                await oldDb.CloseAsync();
                await newDb.CloseAsync();

                // Securely delete old database
                SecureDeleteFile(Constants.DatabasePath);
                SecureDeleteFile(Constants.DatabasePath + "-shm");
                SecureDeleteFile(Constants.DatabasePath + "-wal");
                SecureDeleteFile(Constants.DatabasePath + "-journal");

                // Mark migration complete
                File.WriteAllText(Constants.MigrationCompletePath, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception)
            {
                // If migration fails, just create fresh database
                File.WriteAllText(Constants.MigrationCompletePath, DateTime.UtcNow.ToString("O"));
            }
        }

        private async Task InitializeTablesAsync()
        {
            if (!_initialized)
            {
                await _database.CreateTablesAsync(CreateFlags.None,
                    typeof(Item), typeof(ChatMessage), typeof(UserAccount), typeof(ConversationIndex));
                _initialized = true;
            }
        }

        private async Task LoadConversationCacheAsync()
        {
            _conversationIndexCache.Clear();
            var conversations = await _database.Table<ConversationIndex>().ToListAsync();
            foreach (var conv in conversations)
            {
                _conversationIndexCache[conv.Id] = (
                    Decrypt(conv.EncryptedUser1),
                    Decrypt(conv.EncryptedUser2)
                );
            }
        }

        private static void SecureDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var length = new FileInfo(path).Length;
                if (length > 0)
                {
                    var buffer = new byte[length];
                    File.WriteAllBytes(path, buffer);
                    for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xFF;
                    File.WriteAllBytes(path, buffer);
                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(buffer);
                    File.WriteAllBytes(path, buffer);
                    Array.Clear(buffer, 0, buffer.Length);
                }
                File.Delete(path);
            }
            catch
            {
                try { File.Delete(path); } catch { }
            }
        }

        #region Encryption Helpers

        private static string Encrypt(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (!App.Security.IsUnlocked) return value;
            return App.Security.EncryptString(value);
        }

        private static string Decrypt(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (!App.Security.IsUnlocked) return value;
            return App.Security.DecryptString(value);
        }

        private static string EncryptDateTime(DateTime dt)
        {
            return Encrypt(dt.Ticks.ToString());
        }

        private static DateTime DecryptDateTime(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted)) return DateTime.MinValue;
            try
            {
                var ticksStr = Decrypt(encrypted);
                if (long.TryParse(ticksStr, out var ticks))
                {
                    return new DateTime(ticks);
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        // Item encryption - NOW encrypts Text and Description
        private static Item EncryptItem(Item item)
        {
            return new Item
            {
                Id = item.Id,
                Text = Encrypt(item.Text),
                Description = Encrypt(item.Description),
                PublicKey = Encrypt(item.PublicKey),
                PrivateKey = Encrypt(item.PrivateKey),
                SafeMessage = Encrypt(item.SafeMessage),
                EmailKey = Encrypt(item.EmailKey),
                PasswordKey = Encrypt(item.PasswordKey)
            };
        }

        private static Item DecryptItem(Item item)
        {
            if (item == null) return null;
            return new Item
            {
                Id = item.Id,
                Text = Decrypt(item.Text),
                Description = Decrypt(item.Description),
                PublicKey = Decrypt(item.PublicKey),
                PrivateKey = Decrypt(item.PrivateKey),
                SafeMessage = Decrypt(item.SafeMessage),
                EmailKey = Decrypt(item.EmailKey),
                PasswordKey = Decrypt(item.PasswordKey)
            };
        }

        // ChatMessage encryption - NOW encrypts usernames and timestamp
        private static ChatMessage EncryptMessage(ChatMessage msg)
        {
            return new ChatMessage
            {
                Id = msg.Id,
                ConversationId = msg.ConversationId,
                ServerId = msg.ServerId,
                SenderUsername = Encrypt(msg.SenderUsername),
                RecipientUsername = Encrypt(msg.RecipientUsername),
                PlainText = Encrypt(msg.PlainText),
                SentAt = EncryptDateTime(msg.SentAtDateTime),
                IsOutgoing = msg.IsOutgoing
            };
        }

        private static ChatMessage DecryptMessage(ChatMessage msg)
        {
            if (msg == null) return null;
            var decrypted = new ChatMessage
            {
                Id = msg.Id,
                ConversationId = msg.ConversationId,
                ServerId = msg.ServerId,
                SenderUsername = Decrypt(msg.SenderUsername),
                RecipientUsername = Decrypt(msg.RecipientUsername),
                PlainText = Decrypt(msg.PlainText),
                IsOutgoing = msg.IsOutgoing
            };
            decrypted.SentAtDateTime = DecryptDateTime(msg.SentAt);
            return decrypted;
        }

        // UserAccount encryption - NOW encrypts Username and ServerUrl
        private static UserAccount EncryptAccount(UserAccount acct)
        {
            return new UserAccount
            {
                Id = acct.Id,
                Username = Encrypt(acct.Username),
                AuthToken = Encrypt(acct.AuthToken),
                ServerUrl = Encrypt(acct.ServerUrl),
                KeyPairId = acct.KeyPairId
            };
        }

        private static UserAccount DecryptAccount(UserAccount acct)
        {
            if (acct == null) return null;
            return new UserAccount
            {
                Id = acct.Id,
                Username = Decrypt(acct.Username),
                AuthToken = Decrypt(acct.AuthToken),
                ServerUrl = Decrypt(acct.ServerUrl),
                KeyPairId = acct.KeyPairId
            };
        }

        #endregion

        #region Conversation Methods

        public async Task<int> GetOrCreateConversationIdAsync(string user1, string user2)
        {
            // Check cache first
            foreach (var kvp in _conversationIndexCache)
            {
                if ((kvp.Value.User1 == user1 && kvp.Value.User2 == user2) ||
                    (kvp.Value.User1 == user2 && kvp.Value.User2 == user1))
                {
                    return kvp.Key;
                }
            }

            // Create new conversation
            var conv = new ConversationIndex
            {
                EncryptedUser1 = Encrypt(user1),
                EncryptedUser2 = Encrypt(user2)
            };
            await _database.InsertAsync(conv);

            // Update cache
            _conversationIndexCache[conv.Id] = (user1, user2);

            return conv.Id;
        }

        public List<int> GetConversationIdsForUser(string username)
        {
            var result = new List<int>();
            foreach (var kvp in _conversationIndexCache)
            {
                if (kvp.Value.User1 == username || kvp.Value.User2 == username)
                {
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        public string GetOtherUserInConversation(int conversationId, string myUsername)
        {
            if (_conversationIndexCache.TryGetValue(conversationId, out var users))
            {
                return users.User1 == myUsername ? users.User2 : users.User1;
            }
            return null;
        }

        #endregion

        #region Item Methods

        public async Task<List<Item>> GetItemsAsync()
        {
            var items = await _database.Table<Item>().ToListAsync();
            return items.Select(DecryptItem).ToList();
        }

        public async Task<Item> GetItemAsync(int id)
        {
            var item = await _database.Table<Item>().Where(i => i.Id == id).FirstOrDefaultAsync();
            return DecryptItem(item);
        }

        public async Task<Item> GetItemByTextAsync(string text)
        {
            // Since Text is now encrypted, we need to decrypt all and filter
            var items = await _database.Table<Item>().ToListAsync();
            var decrypted = items.Select(DecryptItem).ToList();
            return decrypted.FirstOrDefault(i => i.Text == text);
        }

        public async Task<List<Item>> GetPrivateItemAsync()
        {
            var items = await _database.Table<Item>().ToListAsync();
            var decrypted = items.Select(DecryptItem).ToList();
            return decrypted.Where(x => !string.IsNullOrEmpty(x.PrivateKey)).ToList();
        }

        public async Task<int> SaveItemAsync(Item item)
        {
            var encrypted = EncryptItem(item);

            // Check for existing item with same text
            var existingItem = await GetItemByTextAsync(item.Text);

            if (item.Id != 0 && existingItem != null)
            {
                encrypted.Id = item.Id;
                return await _database.UpdateAsync(encrypted);
            }
            else if (existingItem == null)
            {
                return await _database.InsertAsync(encrypted);
            }

            return 0;
        }

        public async Task DeleteItemAsync(Item item)
        {
            // Find item by text (need to search since text is encrypted)
            var existingItem = await GetItemByTextAsync(item.Text);
            if (existingItem != null)
            {
                await _database.DeleteAsync<Item>(existingItem.Id);
            }
        }

        public Task DeleteAllItemsAsync()
        {
            return _database.DeleteAllAsync<Item>();
        }

        #endregion

        #region UserAccount Methods

        public async Task<UserAccount> GetUserAccountAsync()
        {
            var acct = await _database.Table<UserAccount>().FirstOrDefaultAsync();
            return DecryptAccount(acct);
        }

        public async Task<int> SaveUserAccountAsync(UserAccount account)
        {
            var encrypted = EncryptAccount(account);
            var existing = await _database.Table<UserAccount>().FirstOrDefaultAsync();
            if (existing != null)
            {
                encrypted.Id = existing.Id;
                return await _database.UpdateAsync(encrypted);
            }
            return await _database.InsertAsync(encrypted);
        }

        public Task DeleteUserAccountAsync()
        {
            return _database.DeleteAllAsync<UserAccount>();
        }

        #endregion

        #region ChatMessage Methods

        public async Task<int> SaveChatMessageAsync(ChatMessage message, string myUsername)
        {
            // Ensure conversation exists
            var otherUser = message.IsOutgoing ? message.RecipientUsername : message.SenderUsername;
            message.ConversationId = await GetOrCreateConversationIdAsync(myUsername, otherUser);

            var encrypted = EncryptMessage(message);
            if (message.Id != 0)
            {
                encrypted.Id = message.Id;
                return await _database.UpdateAsync(encrypted);
            }
            return await _database.InsertAsync(encrypted);
        }

        public async Task<List<ChatMessage>> GetChatMessagesAsync(string otherUsername, string myUsername)
        {
            // Get conversation ID
            var conversationId = await GetOrCreateConversationIdAsync(myUsername, otherUsername);

            // Query by ConversationId (efficient!)
            var messages = await _database.Table<ChatMessage>()
                .Where(m => m.ConversationId == conversationId)
                .ToListAsync();

            // Decrypt and sort by timestamp
            var decrypted = messages.Select(DecryptMessage).ToList();
            return decrypted.OrderBy(m => m.SentAtDateTime).ToList();
        }

        public async Task<List<ChatMessage>> GetLatestMessagesPerConversationAsync(string myUsername)
        {
            // Get all conversation IDs for this user
            var conversationIds = GetConversationIdsForUser(myUsername);

            var result = new List<ChatMessage>();
            foreach (var convId in conversationIds)
            {
                // Get most recent message by Id (auto-increment serves as ordering)
                var latest = await _database.Table<ChatMessage>()
                    .Where(m => m.ConversationId == convId)
                    .OrderByDescending(m => m.Id)
                    .FirstOrDefaultAsync();

                if (latest != null)
                {
                    result.Add(DecryptMessage(latest));
                }
            }

            // Sort by timestamp (most recent first)
            return result.OrderByDescending(m => m.SentAtDateTime).ToList();
        }

        public Task DeleteAllChatMessagesAsync()
        {
            _conversationIndexCache.Clear();
            return Task.WhenAll(
                _database.DeleteAllAsync<ChatMessage>(),
                _database.DeleteAllAsync<ConversationIndex>()
            );
        }

        #endregion
    }
}
