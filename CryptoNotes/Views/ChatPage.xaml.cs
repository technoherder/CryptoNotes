using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CryptoNotes.Models;
using Xamarin.Forms;

namespace CryptoNotes.Views
{
  public partial class ChatPage : ContentPage
  {
    private readonly string _otherUsername;
    private string _myUsername;
    private Item _myKeyPair;
    private string _recipientPublicKey;
    private ObservableCollection<ChatMessageDisplay> _messages = new ObservableCollection<ChatMessageDisplay>();
    private bool _isActive = false;

    public ChatPage(string otherUsername)
    {
      InitializeComponent();
      _otherUsername = otherUsername;
      Title = $"#{otherUsername}";
      MessagesView.ItemsSource = _messages;
    }

    protected override async void OnAppearing()
    {
      base.OnAppearing();
      _isActive = true;
      await InitializeChat();
      await LoadMessages();

      // Auto-refresh: poll for new messages
      Device.StartTimer(TimeSpan.FromSeconds(5), () =>
      {
        // Stop timer when page is no longer active
        if (!_isActive)
          return false;

        Device.BeginInvokeOnMainThread(async () =>
        {
          if (_isActive)
            await FetchAndLoadNewMessages();
        });
        return true;
      });
    }

    protected override void OnDisappearing()
    {
      base.OnDisappearing();
      // Stop the polling timer and clear sensitive data from memory
      _isActive = false;
      ClearSensitiveData();
    }

    private void ClearSensitiveData()
    {
      // Clear plaintext messages from the in-memory collection
      foreach (var msg in _messages)
      {
        msg.PlainText = null;
      }
      _messages.Clear();

      // Clear the recipient's public key from memory
      _recipientPublicKey = null;
    }

    private async Task InitializeChat()
    {
      var account = await App.Database.GetUserAccountAsync();
      if (account == null)
      {
        await DisplayAlert("Error", "Not logged in", "OK");
        await Navigation.PopAsync();
        return;
      }

      _myUsername = account.Username;

      if (!App.MessagingApi.IsConfigured)
        App.MessagingApi.Configure(account.ServerUrl, account.AuthToken);

      _myKeyPair = await App.Database.GetItemAsync(account.KeyPairId);
      if (_myKeyPair == null)
      {
        await DisplayAlert("Error", "Key pair not found. Re-register.", "OK");
        return;
      }

      // Fetch recipient's public key
      var userResult = await App.MessagingApi.GetUserPublicKeyAsync(_otherUsername);
      if (userResult.IsSuccess)
      {
        _recipientPublicKey = userResult.Data.PublicKey;
      }
      else
      {
        await DisplayAlert("Error", $"Could not fetch {_otherUsername}'s public key: {userResult.Error}", "OK");
      }
    }

    private async Task LoadMessages()
    {
      var messages = await App.Database.GetChatMessagesAsync(_otherUsername, _myUsername);
      _messages.Clear();

      foreach (var msg in messages)
      {
        _messages.Add(ToDisplay(msg));
      }

      ScrollToBottom();
    }

    private async Task FetchAndLoadNewMessages()
    {
      if (!App.MessagingApi.IsConfigured || _myKeyPair == null) return;

      var result = await App.MessagingApi.ReceiveMessagesAsync();
      if (!result.IsSuccess || result.Data.Count == 0) return;

      bool hasNewForThisChat = false;

      foreach (var msg in result.Data)
      {
        string plainText = null;
        try
        {
          plainText = await App.Encryption.DecryptMessageAsync(
            msg.EncryptedContent, _myKeyPair.PrivateKey, _myKeyPair.PasswordKey);

          var chatMessage = new ChatMessage
          {
            ServerId = msg.Id,
            SenderUsername = msg.SenderUsername,
            RecipientUsername = _myUsername,
            PlainText = plainText,
            SentAtDateTime = DateTime.Parse(msg.SentAt),
            IsOutgoing = false
          };

          await App.Database.SaveChatMessageAsync(chatMessage, _myUsername);

          if (msg.SenderUsername == _otherUsername)
          {
            _messages.Add(ToDisplay(chatMessage));
            hasNewForThisChat = true;
          }
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"Decrypt failed: {ex.Message}");
        }
        finally
        {
          // Clear the decrypted plaintext variable
          plainText = null;
        }
      }

      if (hasNewForThisChat)
        ScrollToBottom();
    }

    private async void SendMessageClicked(object sender, EventArgs e)
    {
      var text = MessageInput.Text?.Trim();
      if (string.IsNullOrEmpty(text)) return;
      if (text.Length > 10000)
      {
        await DisplayAlert("Error", "Message too long (max 10,000 characters)", "OK");
        return;
      }
      if (string.IsNullOrEmpty(_recipientPublicKey))
      {
        await DisplayAlert("Error", "Recipient's public key not available", "OK");
        return;
      }

      SendBtn.IsEnabled = false;
      MessageInput.Text = "";

      try
      {
        // Encrypt the message with recipient's public key and sign with our private key
        var encrypted = await App.Encryption.EncryptMessageAsync(
          text,
          _recipientPublicKey,
          _myKeyPair.PrivateKey,
          _myKeyPair.PasswordKey);

        // Send to server
        var result = await App.MessagingApi.SendMessageAsync(_otherUsername, encrypted);

        if (result.IsSuccess)
        {
          // Store plaintext locally
          var chatMessage = new ChatMessage
          {
            ServerId = result.Data.Id,
            SenderUsername = _myUsername,
            RecipientUsername = _otherUsername,
            PlainText = text,
            SentAtDateTime = DateTime.UtcNow,
            IsOutgoing = true
          };

          await App.Database.SaveChatMessageAsync(chatMessage, _myUsername);
          _messages.Add(ToDisplay(chatMessage));
          ScrollToBottom();
        }
        else
        {
          await DisplayAlert("Send Failed", result.Error, "OK");
          MessageInput.Text = text; // Restore the message
        }
      }
      catch (Exception ex)
      {
        await DisplayAlert("Error", ex.Message, "OK");
        MessageInput.Text = text;
      }

      SendBtn.IsEnabled = true;
    }

    private void ScrollToBottom()
    {
      if (_messages.Count > 0)
      {
        MessagesView.ScrollTo(_messages.Count - 1, position: ScrollToPosition.End, animate: true);
      }
    }

    private ChatMessageDisplay ToDisplay(ChatMessage msg)
    {
      bool isOutgoing = msg.IsOutgoing;
      var nick = isOutgoing ? _myUsername ?? "me" : msg.SenderUsername;
      return new ChatMessageDisplay
      {
        PlainText = msg.PlainText,
        SenderLabel = $"<{nick}>",
        SenderColor = isOutgoing ? Color.FromHex("#33ff33") : Color.FromHex("#00e5ff"),
        TimeStamp = msg.SentAtDateTime.ToLocalTime().ToString("[HH:mm]")
      };
    }

    private async void RefreshClicked(object sender, EventArgs e)
    {
      await FetchAndLoadNewMessages();
    }
  }

  /// <summary>
  /// Display model for chat messages with visual properties.
  /// </summary>
  public class ChatMessageDisplay
  {
    public string PlainText { get; set; }
    public string SenderLabel { get; set; }
    public Color SenderColor { get; set; }
    public string TimeStamp { get; set; }
  }
}
