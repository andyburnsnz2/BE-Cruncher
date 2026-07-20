using System.Collections.ObjectModel;
using System.Windows.Controls;
using BE_Cruncher.Models;
using BE_Cruncher.Services;

namespace BE_Cruncher.Pages
{
    public partial class VersionsPage : Page
    {
        private readonly AppServices _services;
        private readonly ObservableCollection<ReleaseListItem> _items = [];

        public VersionsPage(AppServices services)
        {
            InitializeComponent();
            _services = services;

            ReleasesList.ItemsSource = _items;
            Loaded += async (_, _) => await LoadReleasesAsync(forceRefresh: false);
        }

        private async void RefreshButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
            await LoadReleasesAsync(forceRefresh: true);

        private void SettingsButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
            NavigationService?.Navigate(new SettingsPage(_services));

        private async Task LoadReleasesAsync(bool forceRefresh)
        {
            StatusText.Text = "Loading releases...";
            RefreshButton.IsEnabled = false;
            try
            {
                var releases = await _services.ReleaseService.GetReleasesAsync(forceRefresh);
                _items.Clear();
                foreach (var release in releases)
                    _items.Add(new ReleaseListItem(release, DescribeStatus(release)));
                StatusText.Text = $"{_items.Count} versions loaded.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load releases: {ex.Message}";
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private void ReleasesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = ReleasesList.SelectedItem as ReleaseListItem;
            DownloadButton.IsEnabled = selected is not null;
            AnalyzeButton.IsEnabled = selected is not null && _services.RepositoryService.IsDownloaded(selected.Release);
            ConfigureButton.IsEnabled = selected is not null && _services.AnalysisService.HasCachedAnalysis(selected.Release.TagName);
        }

        private async void DownloadButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ReleasesList.SelectedItem is not ReleaseListItem item)
                return;

            DownloadButton.IsEnabled = false;
            var progress = new Progress<string>(msg => item.Status = msg);
            try
            {
                await _services.RepositoryService.EnsureDownloadedAsync(item.Release, progress);
                item.Status = DescribeStatus(item.Release);
                AnalyzeButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                item.Status = $"Error: {ex.Message}";
            }
            finally
            {
                DownloadButton.IsEnabled = true;
            }
        }

        private async void AnalyzeButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ReleasesList.SelectedItem is not ReleaseListItem item)
                return;

            AnalyzeButton.IsEnabled = false;
            item.Status = "Analyzing...";
            try
            {
                var originalSourceDir = _services.Paths.OriginalSourceDir(item.Release.TagName);
                var analysis = await Task.Run(() => _services.AnalysisService.Analyze(item.Release.TagName, originalSourceDir));
                item.Status = DescribeStatus(item.Release);
                ConfigureButton.IsEnabled = true;
                StatusText.Text =
                    $"Analysis complete: {analysis.Boards.Count} board(s), {analysis.Batteries.Count} batter(y/ies), " +
                    $"{analysis.Inverters.Count} inverter(s), {analysis.OptionalModules.Count} optional module(s).";
            }
            catch (Exception ex)
            {
                item.Status = $"Analysis error: {ex.Message}";
            }
            finally
            {
                AnalyzeButton.IsEnabled = true;
            }
        }

        private void ConfigureButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (ReleasesList.SelectedItem is not ReleaseListItem item)
                return;

            var analysis = _services.AnalysisService.LoadCachedAnalysis(item.Release.TagName);
            NavigationService?.Navigate(new ConfigurationPage(_services, item.Release, analysis));
        }

        private string DescribeStatus(GitHubRelease release)
        {
            if (!_services.RepositoryService.IsDownloaded(release))
                return "Not downloaded";
            return _services.AnalysisService.HasCachedAnalysis(release.TagName) ? "Downloaded, analyzed" : "Downloaded";
        }
    }
}
