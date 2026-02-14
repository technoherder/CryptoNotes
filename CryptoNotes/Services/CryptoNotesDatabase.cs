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

    public Task<List<Item>> GetItemsAsync()
    {
      return Database.Table<Item>().ToListAsync();
    }

    public Task<List<Item>> GetPrivateItemsNotSignedAsync()
    {
      // SQL queries are also possible
      return Database.QueryAsync<Item>("SELECT * FROM [Item] WHERE [PasswordKey] IS NULL OR [EmailKey] IS NULL;");
    }

    public Task<Item> GetItemAsync(int id)
    {
      return Database.Table<Item>().Where(i => i.Id == id).FirstOrDefaultAsync();
    }

    public Task<List<Item>> GetPrivateItemAsync()
    {
      return Database.Table<Item>().Where(x => x.PrivateKey != null).ToListAsync();
    }

    public Task<int> SaveItemAsync(Item item)
    {
      int dumbCheck =  Database.Table<Item>().Where(x => x.Text == item.Text).ToListAsync().Result.Count;
      if (item.Id != 0 && dumbCheck == 1)
      {
        return Database.UpdateAsync(item);
      }
      else if (dumbCheck == 0)
      {
        return Database.InsertAsync(item);
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
      // SQL queries are also possible
      return Database.QueryAsync<Item>("DELETE FROM [Item];");
    }

    // --- UserAccount methods ---

    public Task<UserAccount> GetUserAccountAsync()
    {
      return Database.Table<UserAccount>().FirstOrDefaultAsync();
    }

    public async Task<int> SaveUserAccountAsync(UserAccount account)
    {
      var existing = await Database.Table<UserAccount>().FirstOrDefaultAsync();
      if (existing != null)
      {
        account.Id = existing.Id;
        return await Database.UpdateAsync(account);
      }
      return await Database.InsertAsync(account);
    }

    public Task DeleteUserAccountAsync()
    {
      return Database.DeleteAllAsync<UserAccount>();
    }

    // --- ChatMessage methods ---

    public Task<int> SaveChatMessageAsync(ChatMessage message)
    {
      if (message.Id != 0)
        return Database.UpdateAsync(message);
      return Database.InsertAsync(message);
    }

    public Task<List<ChatMessage>> GetChatMessagesAsync(string otherUsername, string myUsername)
    {
      return Database.Table<ChatMessage>()
        .Where(m =>
          (m.SenderUsername == myUsername && m.RecipientUsername == otherUsername) ||
          (m.SenderUsername == otherUsername && m.RecipientUsername == myUsername))
        .OrderBy(m => m.SentAt)
        .ToListAsync();
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
          result.Add(msg);
      }
      return result;
    }

    public Task DeleteAllChatMessagesAsync()
    {
      return Database.DeleteAllAsync<ChatMessage>();
    }
  }
}
