using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CryptoNotes.Models;
using SQLite;

namespace CryptoNotes.Services
{
  public class CryptoNotesDatabase
  {
    static readonly Lazy<SQLiteAsyncConnection> lazyInitializer = new Lazy<SQLiteAsyncConnection>(() =>
    {
      return new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);
    });

    static SQLiteAsyncConnection Database => lazyInitializer.Value;
    static bool initialized = false;

    public CryptoNotesDatabase()
    {
      InitializeAsync().SafeFireAndForget(false);
    }

    async Task InitializeAsync()
    {
      if (!initialized)
      {
        if (!Database.TableMappings.Any(m => m.MappedType.Name == typeof(Item).Name))
        {
          await Database.CreateTablesAsync(CreateFlags.None, typeof(Item)).ConfigureAwait(false);
        }
        if (!Database.TableMappings.Any(m => m.MappedType.Name == typeof(ChatMessage).Name))
        {
          await Database.CreateTablesAsync(CreateFlags.None, typeof(ChatMessage)).ConfigureAwait(false);
        }
        if (!Database.TableMappings.Any(m => m.MappedType.Name == typeof(UserAccount).Name))
        {
          await Database.CreateTablesAsync(CreateFlags.None, typeof(UserAccount)).ConfigureAwait(false);
        }
        initialized = true;
      }
    }

    // --- Encryption helpers ---
    // All sensitive fields are encrypted with AES-256 before storage.
    // The encryption key is derived from the user's app password via PBKDF2.

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

    private static Item EncryptItem(Item item)
    {
      return new Item
      {
        Id = item.Id,
        Text = item.Text, // Keep unencrypted for lookups/display
        Description = item.Description,
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
        Text = item.Text,
        Description = item.Description,
        PublicKey = Decrypt(item.PublicKey),
        PrivateKey = Decrypt(item.PrivateKey),
        SafeMessage = Decrypt(item.SafeMessage),
        EmailKey = Decrypt(item.EmailKey),
        PasswordKey = Decrypt(item.PasswordKey)
      };
    }

    private static ChatMessage EncryptMessage(ChatMessage msg)
    {
      return new ChatMessage
      {
        Id = msg.Id,
        ServerId = msg.ServerId,
        SenderUsername = msg.SenderUsername,
        RecipientUsername = msg.RecipientUsername,
        PlainText = Encrypt(msg.PlainText),
        SentAt = msg.SentAt,
        IsOutgoing = msg.IsOutgoing
      };
    }

    private static ChatMessage DecryptMessage(ChatMessage msg)
    {
      if (msg == null) return null;
      return new ChatMessage
      {
        Id = msg.Id,
        ServerId = msg.ServerId,
        SenderUsername = msg.SenderUsername,
        RecipientUsername = msg.RecipientUsername,
        PlainText = Decrypt(msg.PlainText),
        SentAt = msg.SentAt,
        IsOutgoing = msg.IsOutgoing
      };
    }

    private static UserAccount EncryptAccount(UserAccount acct)
    {
      return new UserAccount
      {
        Id = acct.Id,
        Username = acct.Username,
        AuthToken = Encrypt(acct.AuthToken),
        ServerUrl = acct.ServerUrl,
        KeyPairId = acct.KeyPairId
      };
    }

    private static UserAccount DecryptAccount(UserAccount acct)
    {
      if (acct == null) return null;
      return new UserAccount
      {
        Id = acct.Id,
        Username = acct.Username,
        AuthToken = Decrypt(acct.AuthToken),
        ServerUrl = acct.ServerUrl,
        KeyPairId = acct.KeyPairId
      };
    }

    // --- Item methods (with encryption) ---

    public async Task<List<Item>> GetItemsAsync()
    {
      var items = await Database.Table<Item>().ToListAsync();
      return items.Select(DecryptItem).ToList();
    }

    public Task<List<Item>> GetPrivateItemsNotSignedAsync()
    {
      return Database.QueryAsync<Item>("SELECT * FROM [Item] WHERE [PasswordKey] IS NULL OR [EmailKey] IS NULL;");
    }

    public async Task<Item> GetItemAsync(int id)
    {
      var item = await Database.Table<Item>().Where(i => i.Id == id).FirstOrDefaultAsync();
      return DecryptItem(item);
    }

    public async Task<List<Item>> GetPrivateItemAsync()
    {
      var items = await Database.Table<Item>().Where(x => x.PrivateKey != null).ToListAsync();
      return items.Select(DecryptItem).ToList();
    }

    public Task<int> SaveItemAsync(Item item)
    {
      var encrypted = EncryptItem(item);
      int dumbCheck = Database.Table<Item>().Where(x => x.Text == item.Text).ToListAsync().Result.Count;
      if (item.Id != 0 && dumbCheck == 1)
      {
        encrypted.Id = item.Id;
        return Database.UpdateAsync(encrypted);
      }
      else if (dumbCheck == 0)
      {
        return Database.InsertAsync(encrypted);
      }
      else
        return null;
    }

    public Task<List<Item>> DeleteItemAsync(Item item)
    {
      // Use parameterized query to prevent SQL injection
      return Database.QueryAsync<Item>("DELETE FROM [Item] WHERE [Text] = ?;", item.Text);
    }

    public Task<List<Item>> DeleteAllItemsAsync()
    {
      return Database.QueryAsync<Item>("DELETE FROM [Item];");
    }

    // --- UserAccount methods (with encryption) ---

    public async Task<UserAccount> GetUserAccountAsync()
    {
      var acct = await Database.Table<UserAccount>().FirstOrDefaultAsync();
      return DecryptAccount(acct);
    }

    public async Task<int> SaveUserAccountAsync(UserAccount account)
    {
      var encrypted = EncryptAccount(account);
      var existing = await Database.Table<UserAccount>().FirstOrDefaultAsync();
      if (existing != null)
      {
        encrypted.Id = existing.Id;
        return await Database.UpdateAsync(encrypted);
      }
      return await Database.InsertAsync(encrypted);
    }

    public Task DeleteUserAccountAsync()
    {
      return Database.DeleteAllAsync<UserAccount>();
    }

    // --- ChatMessage methods (with encryption) ---

    public Task<int> SaveChatMessageAsync(ChatMessage message)
    {
      var encrypted = EncryptMessage(message);
      if (message.Id != 0)
      {
        encrypted.Id = message.Id;
        return Database.UpdateAsync(encrypted);
      }
      return Database.InsertAsync(encrypted);
    }

    public async Task<List<ChatMessage>> GetChatMessagesAsync(string otherUsername, string myUsername)
    {
      var messages = await Database.Table<ChatMessage>()
        .Where(m =>
          (m.SenderUsername == myUsername && m.RecipientUsername == otherUsername) ||
          (m.SenderUsername == otherUsername && m.RecipientUsername == myUsername))
        .OrderBy(m => m.SentAt)
        .ToListAsync();
      return messages.Select(DecryptMessage).ToList();
    }

    public async Task<List<ChatMessage>> GetLatestMessagesPerConversationAsync(string myUsername)
    {
      var allMessages = await Database.Table<ChatMessage>()
        .Where(m => m.SenderUsername == myUsername || m.RecipientUsername == myUsername)
        .OrderByDescending(m => m.SentAt)
        .ToListAsync();

      var seen = new System.Collections.Generic.HashSet<string>();
      var result = new List<ChatMessage>();
      foreach (var msg in allMessages)
      {
        var other = msg.SenderUsername == myUsername ? msg.RecipientUsername : msg.SenderUsername;
        if (seen.Add(other))
          result.Add(DecryptMessage(msg));
      }
      return result;
    }

    public Task DeleteAllChatMessagesAsync()
    {
      return Database.DeleteAllAsync<ChatMessage>();
    }
  }
}
