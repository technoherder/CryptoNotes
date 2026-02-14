using System;
using CryptoNotes.Services;
using Xamarin.Forms;

namespace CryptoNotes.Views
{
    public partial class AppLockPage : ContentPage
    {
        public AppLockPage()
        {
            InitializeComponent();
            DetermineMode();
        }

        private void DetermineMode()
        {
            if (App.Security.IsSetUp)
            {
                int remaining = App.Security.GetRemainingAttempts();
                if (remaining <= 0)
                {
                    // Already wiped
                    ShowWipedView();
                }
                else
                {
                    ShowUnlockView();
                }
            }
            else
            {
                ShowSetupView();
            }
        }

        private void ShowSetupView()
        {
            SetupView.IsVisible = true;
            UnlockView.IsVisible = false;
            WipedView.IsVisible = false;
        }

        private void ShowUnlockView()
        {
            SetupView.IsVisible = false;
            UnlockView.IsVisible = true;
            WipedView.IsVisible = false;
            UpdateAttemptsLabel();
        }

        private void ShowWipedView()
        {
            SetupView.IsVisible = false;
            UnlockView.IsVisible = false;
            WipedView.IsVisible = true;
        }

        private void UpdateAttemptsLabel()
        {
            int remaining = App.Security.GetRemainingAttempts();
            int max = App.Security.GetMaxAttempts();
            if (remaining < max)
            {
                AttemptsLabel.IsVisible = true;
                AttemptsLabel.Text = $"WARNING: {remaining} attempt(s) remaining before auto-wipe";
            }
            else
            {
                AttemptsLabel.IsVisible = false;
            }
        }

        private void SetupClicked(object sender, EventArgs e)
        {
            var password = SetupPasswordTxt.Text;
            var confirm = SetupConfirmTxt.Text;

            if (string.IsNullOrEmpty(password) || password.Length < 8)
            {
                ShowStatus("Password must be at least 8 characters");
                return;
            }
            if (password != confirm)
            {
                ShowStatus("Passwords do not match");
                return;
            }

            try
            {
                App.Security.SetupPassword(password);
                SetupPasswordTxt.Text = "";
                SetupConfirmTxt.Text = "";
                NavigateToApp();
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message);
            }
        }

        private void UnlockClicked(object sender, EventArgs e)
        {
            AttemptUnlock();
        }

        private void UnlockCompleted(object sender, EventArgs e)
        {
            AttemptUnlock();
        }

        private void AttemptUnlock()
        {
            var password = UnlockPasswordTxt.Text;
            if (string.IsNullOrEmpty(password))
            {
                ShowStatus("Enter your password");
                return;
            }

            UnlockBtn.IsEnabled = false;

            bool success = App.Security.TryUnlock(password);
            UnlockPasswordTxt.Text = "";

            if (success)
            {
                NavigateToApp();
            }
            else
            {
                int remaining = App.Security.GetRemainingAttempts();
                if (remaining <= 0)
                {
                    ShowWipedView();
                }
                else
                {
                    ShowStatus($"Incorrect password. {remaining} attempt(s) remaining.");
                    UpdateAttemptsLabel();
                }
            }

            UnlockBtn.IsEnabled = true;
        }

        private void StartFreshClicked(object sender, EventArgs e)
        {
            App.Security.WipeAllData();
            ShowSetupView();
            StatusFrame.IsVisible = false;
        }

        private void NavigateToApp()
        {
            Application.Current.MainPage = new MainPage();
        }

        private void ShowStatus(string message)
        {
            StatusFrame.IsVisible = true;
            StatusLabel.Text = message;
        }

        // Prevent back button from bypassing the lock screen
        protected override bool OnBackButtonPressed()
        {
            return true;
        }
    }
}
