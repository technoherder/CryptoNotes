using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

using CryptoNotes.Models;

namespace CryptoNotes.Views
{
  [DesignTimeVisible(false)]
  public partial class MainPage : MasterDetailPage
  {
    Dictionary<int, NavigationPage> MenuPages = new Dictionary<int, NavigationPage>();
    public MainPage()
    {
      InitializeComponent();

      MasterBehavior = MasterBehavior.Popover;

      MenuPages.Add((int)MenuItemType.About, (NavigationPage)Detail);
    }

    public async Task NavigateFromMenu(int id)
    {
      if (!MenuPages.ContainsKey(id))
      {
        switch (id)
        {
          case (int)MenuItemType.Browse:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new ItemsPage()));
            break;
          case (int)MenuItemType.About:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new AboutPage()));
            break;
          case (int)MenuItemType.PublicKeys:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new PublicKeyChainPage()));
            break;
          case (int)MenuItemType.EncryptMessage:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new EncryptMessagePage()));
            break;
          case (int)MenuItemType.DecryptMessage:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new DecryptMessagePage()));
            break;
          case (int)MenuItemType.Invite:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new InvitePage()));
            break;
          case (int)MenuItemType.SelfDestruct:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new SelfDestructPage()));
            break;
          case (int)MenuItemType.Messages:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new ConversationsPage()));
            break;
          case (int)MenuItemType.Register:
            MenuPages.Clear();
            MenuPages.Add(id, new NavigationPage(new RegisterPage()));
            break;
        }
      }

      NavigationPage newPage = MenuPages[id];

      if (newPage != null && Detail != newPage)
      {
        Detail = newPage;

        if (Device.RuntimePlatform == Device.Android)
          await Task.Delay(100);

        IsPresented = false;
      }
    }
  }
}