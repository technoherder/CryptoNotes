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
    static SecurityService securityService;

    public App()
    {
      InitializeComponent();

      // Always show the lock screen first - require password to access app
      MainPage = new AppLockPage();
    }

    protected override void OnStart()
    {
    }

    protected override void OnSleep()
    {
      // Lock the app when it goes to background to protect data
      if (securityService != null && securityService.IsUnlocked)
      {
        securityService.Lock();

        // Clear cached auth token from the messaging API service
        if (messagingApi != null)
          messagingApi.ClearCredentials();

        MainPage = new AppLockPage();
      }
    }

    protected override void OnResume()
    {
      // If the app was locked on sleep, require re-authentication
      if (securityService != null && !securityService.IsUnlocked)
      {
        MainPage = new AppLockPage();
      }
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

    public static SecurityService Security
    {
      get
      {
        if (securityService == null)
        {
          securityService = new SecurityService();
        }
        return securityService;
      }
    }
  }
}
