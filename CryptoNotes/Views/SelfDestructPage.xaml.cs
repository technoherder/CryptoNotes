using System;
using System.Collections.Generic;

using Xamarin.Forms;

namespace CryptoNotes.Views
{
  public partial class SelfDestructPage : ContentPage
  {
    public SelfDestructPage()
    {
      InitializeComponent();
    }

    async void SelfDestructClicked(System.Object sender, System.EventArgs e)
    {
      bool confirm = await DisplayAlert("SELF DESTRUCT",
        "This will permanently destroy ALL keys, messages, and account data. This cannot be undone.",
        "DESTROY EVERYTHING", "Cancel");

      if (!confirm) return;

      DeleteBtn.Opacity = 0;

      // Delete all data from database
      await App.Database.DeleteAllItemsAsync();
      await App.Database.DeleteAllChatMessagesAsync();
      await App.Database.DeleteUserAccountAsync();

      // Wipe security data and encryption keys
      App.Security.WipeAllData();

      // Return to lock screen
      Application.Current.MainPage = new AppLockPage();
    }
  }
}
