using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Threading;
using BE_Cruncher.Models;
using BE_Cruncher.Services;

namespace BE_Cruncher.Pages
{
    public partial class BuildProgressPage : Page
    {
        private static readonly Regex AttemptRegex = new(@"attempt (\d+)\.\.\.", RegexOptions.Compiled);

        private readonly AppServices _services;
        private readonly GitHubRelease _release;
        private readonly AnalysisResult _analysis;
        private readonly BuildConfig _config;

        private readonly DispatcherTimer _timer;
        private readonly DateTime _buildStartedAt = DateTime.Now;
        private DateTime _attemptStartedAt = DateTime.Now;
        private int _attempt = 1;
        private bool _finished;

        public BuildProgressPage(AppServices services, GitHubRelease release, AnalysisResult analysis, BuildConfig config)
        {
            InitializeComponent();
            _services = services;
            _release = release;
            _analysis = analysis;
            _config = config;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => UpdateSummary();
            _timer.Start();
            UpdateSummary();

            Loaded += async (_, _) => await RunBuildAsync();
            Unloaded += (_, _) => _timer.Stop();
        }

        private async Task RunBuildAsync()
        {
            var progress = new Progress<string>(AppendLine);
            try
            {
                var originalSourceDir = _services.Paths.OriginalSourceDir(_release.TagName);
                var report = await _services.BuildOrchestrator.RunAsync(originalSourceDir, _analysis, _config, progress);

                _finished = true;
                UpdateSummary();
                StatusText.Text = report.Success ? "Build succeeded." : "Build failed.";
                NavigationService?.Navigate(new OutputPage(_services, report));
            }
            catch (Exception ex)
            {
                _finished = true;
                UpdateSummary();
                StatusText.Text = "Build could not start.";
                AppendLine($"ERROR: {ex.Message}");
            }
        }

        private void AppendLine(string line)
        {
            var match = AttemptRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var attempt) && attempt != _attempt)
            {
                _attempt = attempt;
                _attemptStartedAt = DateTime.Now;
            }

            StatusText.Text = line;
            UpdateSummary();
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void UpdateSummary()
        {
            var attemptElapsed = DateTime.Now - _attemptStartedAt;
            var totalElapsed = DateTime.Now - _buildStartedAt;
            var state = _finished ? "Finished" : "Compiling";
            SummaryText.Text =
                $"{state} — attempt {_attempt} of {BuildOrchestrator.MaxTotalAttempts} " +
                $"— {FormatDuration(attemptElapsed)} on this attempt (total {FormatDuration(totalElapsed)})";
        }

        private static string FormatDuration(TimeSpan span) =>
            span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }
}
