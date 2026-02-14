using Xamarin.Forms;
using Xamarin.Forms.Xaml;
using CryptoNotes.Services;
using CryptoNotes.Views;

namespace CryptoNotes
{
  public partial class App : Application
  {
    static CryptoNotesDatabase database;
    static MessagingApiService messagingApi;
    static E2EEncryptionService encryptionService;

    public App()
    {
      InitializeComponent();

      MainPage = new MainPage();
    }

    protected override void OnStart()
    {
    }

    protected override void OnSleep()
    {
    }

    protected override void OnResume()
    {
    }

    public static CryptoNotesDatabase Database
    {
      get
      {
        if (database == null)
        {
          database = new CryptoNotesDatabase();
        }
        return database;
      }
    }

    public static MessagingApiService MessagingApi
    {
      get
      {
        if (messagingApi == null)
        {
          messagingApi = new MessagingApiService();
        }
        return messagingApi;
      }
    }

    public static E2EEncryptionService Encryption
    {
      get
      {
        if (encryptionService == null)
        {
          encryptionService = new E2EEncryptionService();
        }
        return encryptionService;
      }
    }
  }
}
