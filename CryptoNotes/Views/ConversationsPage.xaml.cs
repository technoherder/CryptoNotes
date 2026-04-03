using System;
using System.Collections.ObjectModel;
using System.Linq;
using CryptoNotes.Models;
using CryptoNotes.Services;
using Xamarin.Forms;

namespace CryptoNotes.Views
{
  public partial class ConversationsPage : ContentPage
  {
    private ObservableCollection<Conversation> _conversations = new ObservableCollection<Conversation>();
    private string _myUsername;

    public ConversationsPage()
    {
      InitializeComponent();
      ConversationsList.ItemsSource = _conversations;
    }

    protected override async void OnAppearing()
    {
      base.OnAppearing();

      var account = await App.Database.GetUserAccountAsync();
      if (account == null)
      {
        NotLoggedInFrame.IsVisible = true;
        return;
      }

      NotLoggedInFrame.IsVisible = false;
      _myUsername = account.Username;

      if (!App.MessagingApi.IsConfigured)
        App.MessagingApi.Configure(account.ServerUrl, account.AuthToken);

      await LoadConversations();
      await FetchNewMessages();
    }

    private async System.Threading.Tasks.Task LoadConversations()
    {
      _conversations.Clear();

      var messages = await App.Database.GetLatestMessagesPerConversationAsync(_myUsername);
      foreach (var msg in messages)
      {
        var otherUser = msg.SenderUsername == _myUsername ? msg.RecipientUsername : msg.SenderUsername;
        var preview = msg.IsOutgoing ? $"You: {msg.PlainText}" : msg.PlainText;
        if (preview.Length > 50) preview = preview.Substring(0, 50) + "...";

        _conversations.Add(new Conversation
        {
          Username = otherUser,
          LastMessagePreview = preview,
          LastMessageAt = msg.SentAtDateTime.ToString("g")
        });
      }
    }

    private async System.Threading.Tasks.Task FetchNewMessages()
    {
      if (!App.MessagingApi.IsConfigured) return;

      var result = await App.MessagingApi.ReceiveMessagesAsync();
      if (!result.IsSuccess) return;

      var account = await App.Database.GetUserAccountAsync();
      if (account == null) return;

      var myKey = await App.Database.GetItemAsync(account.KeyPairId);
      if (myKey == null) return;

      foreach (var msg in result.Data)
      {
        try
        {
          var plainText = await App.Encryption.DecryptMessageAsync(
            msg.EncryptedContent, myKey.PrivateKey, myKey.PasswordKey);

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
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.WriteLine($"Failed to decrypt message: {ex.Message}");
        }
      }

      if (result.Data.Count > 0)
        await LoadConversations();
    }

    private void ConversationSelected(object sender, SelectionChangedEventArgs e)
    {
      if (e.CurrentSelection.FirstOrDefault() is Conversation conv)
      {
        ConversationsList.SelectedItem = null;
        Navigation.PushAsync(new ChatPage(conv.Username));
      }
    }

    private void NewConversationClicked(object sender, EventArgs e)
    {
      SearchFrame.IsVisible = !SearchFrame.IsVisible;
    }

    private async void SearchUsersClicked(object sender, EventArgs e)
    {
      var query = SearchEntry.Text?.Trim();
      if (string.IsNullOrEmpty(query) || query.Length < 2) return;

      var result = await App.MessagingApi.SearchUsersAsync(query);
      if (result.IsSuccess)
      {
        SearchResults.ItemsSource = result.Data;
      }
      else
      {
        await DisplayAlert("Error", result.Error, "OK");
      }
    }

    private void SearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
      if (e.CurrentSelection.FirstOrDefault() is UserInfo user)
      {
        SearchResults.SelectedItem = null;
        SearchFrame.IsVisible = false;
        Navigation.PushAsync(new ChatPage(user.Username));
      }
    }

    private async void RefreshClicked(object sender, EventArgs e)
    {
      await FetchNewMessages();
      await LoadConversations();
    }
  }
}
