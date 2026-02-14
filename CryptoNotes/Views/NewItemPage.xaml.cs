using System;
using System.IO;
using System.Security.Cryptography;
using Xamarin.Forms;
using PgpCore;
using CryptoNotes.Models;

namespace CryptoNotes.Views
{
  public partial class NewItemPage : ContentPage
  {
    public Item Item { get; set; }

    public NewItemPage()
    {
      InitializeComponent();

      Item = new Item
      {
        Text = "",
        Description = "",
        PasswordKey = "",
        EmailKey = ""
      };

      BindingContext = this;
    }

    async void Save_Clicked(object sender, EventArgs e)
    {
      MessagingCenter.Send(this, "AddItem", Item);
      await Navigation.PopModalAsync();
    }

    async void Cancel_Clicked(object sender, EventArgs e)
    {
      await Navigation.PopModalAsync();
    }

    async void GeneratePrivateKey(System.Object sender, System.EventArgs e)
    {
      createKeyBtn.FadeTo(0, 4000);

      // Use unique file names to prevent collisions
      var id = Guid.NewGuid().ToString("N");
      string publicFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"keygen_pub_{id}.asc");
      string privateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"keygen_priv_{id}.asc");

      try
      {
        using (PGP pgp = new PGP())
        {
          pgp.GenerateKey(publicFile, privateFile, Item.EmailKey, Item.PasswordKey);

          Item.PrivateKey = File.ReadAllText(privateFile);
          Item.PublicKey = File.ReadAllText(publicFile);
        }

        MessagingCenter.Send(this, "AddItem", Item);
        await Navigation.PopModalAsync();
      }
      catch (Exception ex)
      {
        await DisplayAlert("Error", "Failed to generate key pair", "OK");
      }
      finally
      {
        // Securely delete temp key files
        SecureDeleteFile(publicFile);
        SecureDeleteFile(privateFile);
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
