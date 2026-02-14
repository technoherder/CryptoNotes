using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CryptoNotes.Models;
using CryptoNotes.ViewModels;
using PgpCore;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace CryptoNotes.Views
{
  public partial class EncryptMessagePage : ContentPage
  {
    public EncryptMessagePage()
    {
      InitializeComponent();

      privatePicker.SetBinding(Picker.ItemsSourceProperty, "Item");
      privatePicker.ItemDisplayBinding = new Binding("Text");
      privatePicker.ItemsSource = App.Database.GetPrivateItemAsync().Result.Where(x => x.PasswordKey != null && x.EmailKey != null).ToList();

      publicPicker.SetBinding(Picker.ItemsSourceProperty, "Item");
      publicPicker.ItemDisplayBinding = new Binding("Text");
      publicPicker.ItemsSource = App.Database.GetItemsAsync().Result;
    }

    void OnToggled(object sender, ToggledEventArgs e)
    {
      if (e.Value)
      {
        privatePicker.IsVisible = true;
        PwdLbl.IsVisible = true;
        PwdTxt.IsVisible = true;
      }
      else
      {
        privatePicker.IsVisible = false;
        PwdLbl.IsVisible = false;
        PwdTxt.IsVisible = false;
      }
    }

    async void EncryptMessageClicked(System.Object sender, System.EventArgs e)
    {
      encryptBtn.Opacity = 0;
      Item publicKey = this.publicPicker.SelectedItem as Item;

      if (publicKey == null)
      {
        await DisplayAlert("Error", "Please select a public key", "OK");
        encryptBtn.Opacity = 1;
        return;
      }

      // Use unique file names to prevent collisions and aid cleanup
      var id = Guid.NewGuid().ToString("N");
      string publicFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"enc_pub_{id}.asc");
      string encryptedMessage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"enc_out_{id}.pgp");
      string messageContent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"enc_msg_{id}.txt");
      string privateFile = null;

      try
      {
        File.WriteAllText(publicFile, publicKey.PublicKey);
        File.WriteAllText(messageContent, MessageTxt.Text);

        using (PGP pgp = new PGP())
        {
          if (SignedF.IsToggled)
          {
            Item privateKey = privatePicker.SelectedItem as Item;
            if (privateKey == null)
            {
              await DisplayAlert("Error", "Please select a private key for signing", "OK");
              return;
            }

            privateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"enc_priv_{id}.asc");
            File.WriteAllText(privateFile, privateKey.PrivateKey);
            await pgp.EncryptFileAndSignAsync(messageContent, encryptedMessage, publicFile, privateFile, PwdTxt.Text, true, true);
          }
          else
          {
            await pgp.EncryptFileAsync(messageContent, encryptedMessage, publicFile, true, true);
          }
        }

        await Share.RequestAsync(new ShareTextRequest
        {
          Text = File.ReadAllText(encryptedMessage),
          Title = "PGP Message",
          Subject = "PGP Message"
        });
      }
      catch (Exception ex)
      {
        await DisplayAlert("Encryption Error", "Failed to encrypt message. Check your keys and password.", "OK");
      }
      finally
      {
        // Securely delete all temp files
        SecureDeleteFile(publicFile);
        SecureDeleteFile(encryptedMessage);
        SecureDeleteFile(messageContent);
        if (privateFile != null) SecureDeleteFile(privateFile);
      }

      MessageTxt.Text = string.Empty;
      PwdTxt.Text = string.Empty;
      privatePicker.SelectedIndex = -1;
      publicPicker.SelectedIndex = -1;
      encryptBtn.Opacity = 1;
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
