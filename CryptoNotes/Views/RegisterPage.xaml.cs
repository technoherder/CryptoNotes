using System;
using System.IO;
using System.Linq;
using CryptoNotes.Models;
using PgpCore;
using Xamarin.Forms;

namespace CryptoNotes.Views
{
  public partial class RegisterPage : ContentPage
  {
    public RegisterPage()
    {
      InitializeComponent();
      LoadState();
    }

    private async void LoadState()
    {
      // Load existing key pairs for the picker
      var keys = await App.Database.GetPrivateItemAsync();
      var validKeys = keys.Where(x => x.PasswordKey != null && x.EmailKey != null).ToList();
      KeyPicker.ItemsSource = validKeys;
      KeyPicker.ItemDisplayBinding = new Binding("Text");

      // Check if already logged in
      var account = await App.Database.GetUserAccountAsync();
      if (account != null)
      {
        ShowLoggedInState(account);
      }
    }

    private void ShowLoggedInState(UserAccount account)
    {
      AuthForm.IsVisible = false;
      LoggedInView.IsVisible = true;
      UsernameDisplay.Text = account.Username;
      ServerDisplay.Text = account.ServerUrl;

      // Configure the API service
      App.MessagingApi.Configure(account.ServerUrl, account.AuthToken);
    }

    private void ShowStatus(string message, bool isError = false)
    {
      StatusFrame.IsVisible = true;
      StatusLabel.Text = message;
      StatusLabel.TextColor = isError ? Color.FromHex("#ff4444") : Color.FromHex("#20C20E");
    }

    private async void RegisterClicked(object sender, EventArgs e)
    {
      var serverUrl = ServerUrlTxt.Text?.Trim();
      var username = UsernameTxt.Text?.Trim();
      var password = PasswordTxt.Text;
      var selectedKey = KeyPicker.SelectedItem as Item;

      if (string.IsNullOrEmpty(serverUrl))
      {
        ShowStatus("Server URL is required", true);
        return;
      }
      if (string.IsNullOrEmpty(username) || username.Length < 3)
      {
        ShowStatus("Username must be at least 3 characters", true);
        return;
      }
      if (string.IsNullOrEmpty(password) || password.Length < 6)
      {
        ShowStatus("Password must be at least 6 characters", true);
        return;
      }
      if (selectedKey == null)
      {
        ShowStatus("Please select a PGP key pair. Generate one first in 'Your Keys'.", true);
        return;
      }

      RegisterBtn.IsEnabled = false;
      ShowStatus("Registering...");

      var result = await App.MessagingApi.RegisterAsync(
        serverUrl, username, password, selectedKey.PublicKey);

      if (result.IsSuccess)
      {
        var account = new UserAccount
        {
          Username = result.Data.Username,
          AuthToken = result.Data.Token,
          ServerUrl = serverUrl,
          KeyPairId = selectedKey.Id
        };

        await App.Database.SaveUserAccountAsync(account);
        App.MessagingApi.Configure(serverUrl, result.Data.Token);

        ShowStatus("Registration successful!");
        ShowLoggedInState(account);
      }
      else
      {
        ShowStatus(result.Error, true);
      }

      RegisterBtn.IsEnabled = true;
    }

    private async void LoginClicked(object sender, EventArgs e)
    {
      var serverUrl = ServerUrlTxt.Text?.Trim();
      var username = UsernameTxt.Text?.Trim();
      var password = PasswordTxt.Text;
      var selectedKey = KeyPicker.SelectedItem as Item;

      if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
      {
        ShowStatus("All fields are required for login", true);
        return;
      }
      if (selectedKey == null)
      {
        ShowStatus("Please select the PGP key pair you registered with", true);
        return;
      }

      LoginBtn.IsEnabled = false;
      ShowStatus("Logging in...");

      var result = await App.MessagingApi.LoginAsync(serverUrl, username, password);

      if (result.IsSuccess)
      {
        var account = new UserAccount
        {
          Username = result.Data.Username,
          AuthToken = result.Data.Token,
          ServerUrl = serverUrl,
          KeyPairId = selectedKey.Id
        };

        await App.Database.SaveUserAccountAsync(account);
        App.MessagingApi.Configure(serverUrl, result.Data.Token);

        ShowStatus("Login successful!");
        ShowLoggedInState(account);
      }
      else
      {
        ShowStatus(result.Error, true);
      }

      LoginBtn.IsEnabled = true;
    }

    private async void LogoutClicked(object sender, EventArgs e)
    {
      await App.Database.DeleteUserAccountAsync();
      AuthForm.IsVisible = true;
      LoggedInView.IsVisible = false;
      StatusFrame.IsVisible = false;
    }
  }
}
