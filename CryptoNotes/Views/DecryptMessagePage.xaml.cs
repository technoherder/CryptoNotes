using System;
using System.IO;
using System.Security.Cryptography;
using CryptoNotes.ViewModels;
using PgpCore;
using Xamarin.Forms;
using CryptoNotes.Models;

namespace CryptoNotes.Views
{
  public partial class DecryptMessagePage : ContentPage
  {
    public DecryptMessagePage()
    {
      InitializeComponent();

      privatePicker.SetBinding(Picker.ItemsSourceProperty, "Item");
      privatePicker.ItemDisplayBinding = new Binding("Text");
      privatePicker.ItemsSource = App.Database.GetPrivateItemAsync().Result;
    }

    async void DecryptMessageClicked(System.Object sender, System.EventArgs e)
    {
      decryptBtn.Opacity = 0;
      Item privateKey = this.privatePicker.SelectedItem as Item;

      if (privateKey == null)
      {
        await DisplayAlert("Error", "Please select a private key", "OK");
        decryptBtn.Opacity = 1;
        return;
      }

      // Use unique file names to prevent collisions
      var id = Guid.NewGuid().ToString("N");
      string privateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"dec_priv_{id}.asc");
      string encryptedMessage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"dec_enc_{id}.pgp");
      string decryptedMessage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"dec_out_{id}.txt");

      string safeMessage = null;

      try
      {
        File.WriteAllText(privateFile, privateKey.PrivateKey);
        File.WriteAllText(encryptedMessage, MessageTxt.Text);

        using (PGP pgp = new PGP())
        {
          await pgp.DecryptFileAsync(encryptedMessage, decryptedMessage, privateFile, PwdTxt.Text);
        }

        safeMessage = File.ReadAllText(decryptedMessage);
      }
      catch (Exception ex)
      {
        await DisplayAlert("Decryption Error", "Failed to decrypt. Check your key and password.", "OK");
      }
      finally
      {
        // Securely delete all temp files
        SecureDeleteFile(privateFile);
        SecureDeleteFile(encryptedMessage);
        SecureDeleteFile(decryptedMessage);
      }

      PwdTxt.Text = string.Empty;
      decryptBtn.Opacity = 1;

      if (safeMessage != null)
        await DisplayAlert("Decrypted Message", safeMessage, "OK");
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
  }
}
