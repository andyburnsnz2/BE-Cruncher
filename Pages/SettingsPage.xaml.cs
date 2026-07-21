using System.Windows;
using System.Windows.Controls;
using BE_Cruncher.Services;
using Microsoft.Win32;

namespace BE_Cruncher.Pages
{
    public partial class SettingsPage : Page
    {
        private readonly AppServices _services;
        private readonly AppPaths _defaults = new(); // no config overrides — the true defaults

        public SettingsPage(AppServices services)
        {
            InitializeComponent();
            _services = services;

            RepositoriesBox.Text = services.Paths.RepositoriesDir;
            WorkspacesBox.Text = services.Paths.WorkspacesDir;
            CacheBox.Text = services.Paths.CacheDir;

            RefreshPlatformIoStatus();
        }

        private void RefreshPlatformIoStatus()
        {
            var installed = _services.PlatformIoInstaller.IsInstalled();
            PlatformIoStatusText.Text = installed
                ? $"Installed and working ({_services.PlatformIoInstaller.ResolvePioExecutable()})."
                : "Not installed (or installed but not working — see below). Nothing else on this page depends on it; you can also install it the first time you start a build.";
            PlatformIoUninstallButton.IsEnabled = System.IO.Directory.Exists(PlatformIoInstaller.RootInstallDir);
        }

        private async void PlatformIoInstallButton_Click(object sender, RoutedEventArgs e)
        {
            PlatformIoInstallButton.IsEnabled = false;
            PlatformIoUninstallButton.IsEnabled = false;
            PlatformIoProgressText.Text = "";

            var progress = new Progress<string>(line => PlatformIoProgressText.Text = line);
            var ok = await _services.PlatformIoInstaller.InstallAsync(progress);
            PlatformIoProgressText.Text = ok ? "Done." : PlatformIoProgressText.Text;

            RefreshPlatformIoStatus();
            PlatformIoInstallButton.IsEnabled = true;
        }

        private void PlatformIoUninstallButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = MessageBox.Show(
                $"Delete PlatformIO's entire install folder ({PlatformIoInstaller.RootInstallDir})?\n\n" +
                "This is a clean, complete removal — nothing else on your system is affected. " +
                "You'll need to reinstall it before building firmware again.",
                "Uninstall PlatformIO Core", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
                return;

            try
            {
                _services.PlatformIoInstaller.Uninstall();
                PlatformIoProgressText.Text = "Uninstalled.";
            }
            catch (Exception ex)
            {
                PlatformIoProgressText.Text = $"Could not fully uninstall: {ex.Message}";
            }

            RefreshPlatformIoStatus();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string boxName } || FindName(boxName) is not TextBox box)
                return;

            var dialog = new OpenFolderDialog
            {
                Title = "Choose a folder",
                InitialDirectory = System.IO.Directory.Exists(box.Text) ? box.Text : _services.Paths.RootDir,
            };
            if (dialog.ShowDialog() == true)
                box.Text = dialog.FolderName;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string boxName } || FindName(boxName) is not TextBox box)
                return;

            box.Text = boxName switch
            {
                "RepositoriesBox" => _defaults.RepositoriesDir,
                "WorkspacesBox" => _defaults.WorkspacesDir,
                "CacheBox" => _defaults.CacheDir,
                _ => box.Text,
            };
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            _services.Config.RepositoriesDir = OverrideOrNull(RepositoriesBox.Text, _defaults.RepositoriesDir);
            _services.Config.WorkspacesDir = OverrideOrNull(WorkspacesBox.Text, _defaults.WorkspacesDir);
            _services.Config.CacheDir = OverrideOrNull(CacheBox.Text, _defaults.CacheDir);

            new ConfigService(_services.Paths).Save(_services.Config);
            StatusText.Text = "Saved. Restart BE Cruncher for the new locations to take effect.";
        }

        private static string? OverrideOrNull(string? entered, string defaultValue) =>
            string.IsNullOrWhiteSpace(entered) || string.Equals(entered.TrimEnd('\\', '/'), defaultValue.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? null
                : entered;
    }
}
