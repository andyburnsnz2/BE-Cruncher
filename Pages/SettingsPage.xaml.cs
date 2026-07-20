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
