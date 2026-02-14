using CryptoNotes.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace CryptoNotes.Views
{
  public partial class MenuPage : ContentPage
  {
    MainPage RootPage { get => Application.Current.MainPage as MainPage; }
    List<HomeMenuItem> menuItems;
    public MenuPage()
    {
      InitializeComponent();

      menuItems = new List<HomeMenuItem>
            {
                new HomeMenuItem {Id = MenuItemType.Messages, Title="Messages", Icon = "\uf0e0" },
                new HomeMenuItem {Id = MenuItemType.Register, Title="Account", Icon = "\uf234" },
                new HomeMenuItem {Id = MenuItemType.About, Title="About", Icon = "\uf05a" },
                new HomeMenuItem {Id = MenuItemType.Browse, Title="Your Keys", Icon = "\uf6f3" },
                new HomeMenuItem {Id = MenuItemType.PublicKeys, Title="Shared Keys", Icon = "\uf084" },
                new HomeMenuItem {Id = MenuItemType.Invite, Title="Invite", Icon = "\uf234" },
                new HomeMenuItem {Id = MenuItemType.EncryptMessage, Title="Encrypt", Icon = "\uf502" },
                new HomeMenuItem {Id = MenuItemType.DecryptMessage, Title="Decrpyt", Icon = "\ue058" },
                new HomeMenuItem {Id = MenuItemType.SelfDestruct, Title="Self Destruct", Icon = "\uf7ba" }
            };

      ListViewMenu.ItemsSource = menuItems;

      ListViewMenu.SelectedItem = menuItems[0];
      ListViewMenu.ItemSelected += async (sender, e) =>
      {
        if (e.SelectedItem == null)
          return;

        var id = (int)((HomeMenuItem)e.SelectedItem).Id;
        await RootPage.NavigateFromMenu(id);
      };
    }
  }
}