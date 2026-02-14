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

    public ChatPage(string otherUsername)
    {
      InitializeComponent();
      _otherUsername = otherUsername;
      Title = $"{otherUsername}";
      MessagesView.ItemsSource = _messages;
    }

    protected override async void OnAppearing()
    {
      base.OnAppearing();
      await InitializeChat();
      await LoadMessages();

      // Auto-refresh: poll for new messages
      Device.StartTimer(TimeSpan.FromSeconds(5), () =>
      {
        if (this.IsVisible)
        {
          Device.BeginInvokeOnMainThread(async () =>
          {
            await FetchAndLoadNewMessages();
          });
          return true;
        }
        return false;
      });
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
        try
        {
          var plainText = await App.Encryption.DecryptMessageAsync(
            msg.EncryptedContent, _myKeyPair.PrivateKey, _myKeyPair.PasswordKey);

          var chatMessage = new ChatMessage
          {
            ServerId = msg.Id,
            SenderUsername = msg.SenderUsername,
            RecipientUsername = _myUsername,
            PlainText = plainText,
            SentAt = DateTime.Parse(msg.SentAt),
            IsOutgoing = false
          };

          await App.Database.SaveChatMessageAsync(chatMessage);

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
            SentAt = DateTime.UtcNow,
            IsOutgoing = true
          };

          await App.Database.SaveChatMessageAsync(chatMessage);
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
      return new ChatMessageDisplay
      {
        PlainText = msg.PlainText,
        SenderLabel = isOutgoing ? "You" : msg.SenderUsername,
        SenderColor = isOutgoing ? Color.FromHex("#20C20E") : Color.FromHex("#9f00ff"),
        BubbleColor = isOutgoing ? Color.FromHex("#00497a") : Color.FromHex("#2d1b4e"),
        Alignment = isOutgoing ? LayoutOptions.End : LayoutOptions.Start,
        BubbleMargin = isOutgoing ? new Thickness(50, 0, 0, 0) : new Thickness(0, 0, 50, 0),
        TimeStamp = msg.SentAt.ToLocalTime().ToString("HH:mm")
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
    public Color BubbleColor { get; set; }
    public LayoutOptions Alignment { get; set; }
    public Thickness BubbleMargin { get; set; }
    public string TimeStamp { get; set; }
  }
}
