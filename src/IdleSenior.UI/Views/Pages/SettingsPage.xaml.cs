using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using IdleSenior.Core.Services;
using IdleSenior.UI.ViewModels;

namespace IdleSenior.UI.Views.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly SettingsViewModel _vm;
        private readonly SteamLibraryService _libraryService;
        private readonly SteamLocalDiscoveryService _discoveryService;
        private bool _initializing;

        public SettingsPage(
            SettingsViewModel viewModel,
            SteamLibraryService libraryService,
            SteamLocalDiscoveryService discoveryService)
        {
            InitializeComponent();
            _vm = viewModel;
            _libraryService = libraryService;
            _discoveryService = discoveryService;
            DataContext = viewModel;

            _initializing = true;
            SessionIdBox.Password   = viewModel.SessionId;
            SessionIdTextBox.Text   = viewModel.SessionId;
            LoginSecureBox.Password = viewModel.SteamLoginSecure;
            LoginSecureTextBox.Text = viewModel.SteamLoginSecure;
            _initializing = false;

            _vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SettingsViewModel.SessionId) && SessionIdBox.Password != _vm.SessionId)
                {
                    _initializing = true;
                    SessionIdBox.Password = _vm.SessionId;
                    SessionIdTextBox.Text = _vm.SessionId;
                    _initializing = false;
                }
                else if (args.PropertyName == nameof(SettingsViewModel.SteamLoginSecure) && LoginSecureBox.Password != _vm.SteamLoginSecure)
                {
                    _initializing = true;
                    LoginSecureBox.Password = _vm.SteamLoginSecure;
                    LoginSecureTextBox.Text = _vm.SteamLoginSecure;
                    _initializing = false;
                }
            };

            Loaded += SettingsPage_Loaded;
            Unloaded += SettingsPage_Unloaded;
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Block any visual tree teardown / template destruction from wiping credentials
            _initializing = true;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _initializing = true;
            try
            {
                // Always restore authoritative saved credentials directly from AppSettingsService into ViewModel and UI controls
                var cur = AppSettingsService.Instance.Current;
                _vm.SessionId = cur.SessionId;
                _vm.SteamLoginSecure = cur.SteamLoginSecure;
                SessionIdBox.Password = cur.SessionId;
                SessionIdTextBox.Text = cur.SessionId;
                LoginSecureBox.Password = cur.SteamLoginSecure;
                LoginSecureTextBox.Text = cur.SteamLoginSecure;

                // Preview detected local Steam accounts in the helper banner
                var accounts = _discoveryService.DiscoverLocalAccounts();
                if (accounts.Count > 0)
                {
                    var primary = accounts[0];
                    DetectedAccountLabel.Text = $"Conta Steam Detectada no PC: {primary.PersonaName}";
                    DetectedAccountSubtext.Text = $"SteamID64: {primary.SteamId64} • Clique no botão ao lado para aplicar e salvar.";

                    if (accounts.Count > 1)
                    {
                        LocalAccountsCombo.ItemsSource = accounts;
                        LocalAccountsCombo.SelectedItem = accounts.FirstOrDefault(a => a.SteamId64 == _vm.SteamId64) ?? accounts[0];
                        LocalAccountsCombo.Visibility = Visibility.Visible;
                    }
                }
            }
            finally
            {
                _initializing = false;
            }
        }

        private void AutoDetectSteam_Click(object sender, RoutedEventArgs e)
        {
            var accounts = _discoveryService.DiscoverLocalAccounts();
            if (accounts.Count == 0)
            {
                _vm.SaveStatus = "⚠️ Nenhuma conta Steam conectada foi encontrada neste PC.";
                return;
            }

            var selected = (LocalAccountsCombo.SelectedItem as DiscoveredSteamAccount) ?? accounts[0];
            ApplyDiscoveredAccount(selected);
        }

        private void LocalAccountsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LocalAccountsCombo.SelectedItem is DiscoveredSteamAccount selected)
            {
                ApplyDiscoveredAccount(selected);
            }
        }

        private void ApplyDiscoveredAccount(DiscoveredSteamAccount account)
        {
            _vm.SteamId64 = account.SteamId64;
            _vm.ProfileUrl = account.ProfileUrl;
            _vm.SaveStatus = $"✅ Conta '{account.PersonaName}' ({account.SteamId64}) aplicada e salva automaticamente!";
        }

        private async void ResolveProfileUrl_Click(object sender, RoutedEventArgs e)
        {
            string input = !string.IsNullOrWhiteSpace(_vm.ProfileUrl) ? _vm.ProfileUrl : _vm.SteamId64;
            if (string.IsNullOrWhiteSpace(input))
            {
                _vm.SaveStatus = "⚠️ Digite seu link de perfil ou nick no campo URL do Perfil primeiro.";
                return;
            }

            ResolveUrlBtn.IsEnabled = false;
            _vm.SaveStatus = "🔍 Buscando SteamID64 pelo perfil...";
            try
            {
                var resolved = await _discoveryService.ResolveProfileUrlOrVanityAsync(input);
                if (resolved != null)
                {
                    _vm.SteamId64 = resolved.SteamId64;
                    _vm.ProfileUrl = resolved.ProfileUrl;
                    if (_vm.SaveCommand.CanExecute(null))
                        _vm.SaveCommand.Execute(null);

                    _vm.SaveStatus = $"✅ Perfil '{resolved.PersonaName}' resolvido: SteamID64 {resolved.SteamId64}!";
                }
                else
                {
                    _vm.SaveStatus = "❌ Não foi possível encontrar esse perfil na Steam. Verifique o link ou nick.";
                }
            }
            finally
            {
                ResolveUrlBtn.IsEnabled = true;
            }
        }

        private void OpenApiKeyPage_Click(object sender, RoutedEventArgs e)
        {
            OpenUrlInBrowser("https://steamcommunity.com/dev/apikey");
        }

        private void OpenSteamBadges_Click(object sender, RoutedEventArgs e)
        {
            HelpPanel.Visibility = Visibility.Visible;
            OpenUrlInBrowser("https://steamcommunity.com/my/badges");
        }

        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // PasswordBox / TextBox sync with show/hide toggle
        private void SessionIdBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            if (!SessionIdBox.IsKeyboardFocusWithin && !SessionIdTextBox.IsKeyboardFocusWithin) return;

            _initializing = true;
            try
            {
                SessionIdTextBox.Text = SessionIdBox.Password;
                _vm.SessionId = SessionIdBox.Password;
            }
            finally
            {
                _initializing = false;
            }
        }

        private void SessionIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            if (!SessionIdBox.IsKeyboardFocusWithin && !SessionIdTextBox.IsKeyboardFocusWithin) return;

            _initializing = true;
            try
            {
                SessionIdBox.Password = SessionIdTextBox.Text;
                _vm.SessionId = SessionIdTextBox.Text;
            }
            finally
            {
                _initializing = false;
            }
        }

        private void ToggleSessionId_Click(object sender, RoutedEventArgs e)
        {
            if (SessionIdTextBox.Visibility == Visibility.Visible)
            {
                SessionIdTextBox.Visibility = Visibility.Collapsed;
                SessionIdBox.Visibility = Visibility.Visible;
                ToggleSessionIdBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Eye24);
            }
            else
            {
                SessionIdBox.Visibility = Visibility.Collapsed;
                SessionIdTextBox.Visibility = Visibility.Visible;
                ToggleSessionIdBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.EyeOff24);
            }
        }

        private void LoginSecureBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            if (!LoginSecureBox.IsKeyboardFocusWithin && !LoginSecureTextBox.IsKeyboardFocusWithin) return;

            _initializing = true;
            try
            {
                LoginSecureTextBox.Text = LoginSecureBox.Password;
                _vm.SteamLoginSecure = LoginSecureBox.Password;
            }
            finally
            {
                _initializing = false;
            }
        }

        private void LoginSecureTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_initializing || !IsLoaded) return;
            if (!LoginSecureBox.IsKeyboardFocusWithin && !LoginSecureTextBox.IsKeyboardFocusWithin) return;

            _initializing = true;
            try
            {
                LoginSecureBox.Password = LoginSecureTextBox.Text;
                _vm.SteamLoginSecure = LoginSecureTextBox.Text;
            }
            finally
            {
                _initializing = false;
            }
        }

        private void ToggleLoginSecure_Click(object sender, RoutedEventArgs e)
        {
            if (LoginSecureTextBox.Visibility == Visibility.Visible)
            {
                LoginSecureTextBox.Visibility = Visibility.Collapsed;
                LoginSecureBox.Visibility = Visibility.Visible;
                ToggleLoginSecureBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Eye24);
            }
            else
            {
                LoginSecureBox.Visibility = Visibility.Collapsed;
                LoginSecureTextBox.Visibility = Visibility.Visible;
                ToggleLoginSecureBtn.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.EyeOff24);
            }
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            HelpPanel.Visibility = HelpPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>
        /// Saves the current form values, then validates the Steam session cookies
        /// against the authenticated badges page and reports the result.
        /// </summary>
        private async void TestConnection_Click(object sender, RoutedEventArgs e)
        {
            TestConnectionBtn.IsEnabled = false;
            _vm.SaveStatus = "Testando conexão com a Steam...";

            try
            {
                if (_vm.SaveCommand.CanExecute(null))
                    _vm.SaveCommand.Execute(null);

                bool ok = await _libraryService.ValidateSessionAsync();
                _vm.SaveStatus = ok
                    ? "✅ Conexão válida — sessão Steam autenticada."
                    : "❌ Falha na conexão — verifique os cookies sessionid / steamLoginSecure.";
            }
            catch
            {
                _vm.SaveStatus = "❌ Erro de rede ao testar a conexão.";
            }
            finally
            {
                TestConnectionBtn.IsEnabled = true;
            }
        }
    }
}
