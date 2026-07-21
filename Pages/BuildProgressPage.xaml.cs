using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BE_Cruncher.Models;
using BE_Cruncher.Services;

namespace BE_Cruncher.Pages
{
    public partial class BuildProgressPage : Page
    {
        private static readonly Regex AttemptRegex = new(@"attempt (\d+)\.\.\.", RegexOptions.Compiled);
        private static readonly Regex ToolInstallRegex = new(@"Tool Manager: Installing\s+(\S+)", RegexOptions.Compiled);
        private static readonly Regex ToolInstalledRegex = new(@"has been installed!", RegexOptions.Compiled);
        private static readonly Regex ScanningRegex = new(@"^(LDF:|Scanning dependencies|Found \d+ compatible librar)", RegexOptions.Compiled);
        private static readonly Regex CompilingFileRegex = new(@"^Compiling\s+\S", RegexOptions.Compiled);
        private static readonly Regex LinkingRegex = new(@"^Linking\s+\S", RegexOptions.Compiled);

        // PlatformIO goes through several phases that can each produce zero console output for minutes
        // at a time (a package download between progress updates, dependency scanning before the first
        // "Compiling" line appears, linking at the end) — without tracking which phase we're actually
        // in, the UI just shows whatever the last raw line happened to be, sometimes minutes stale, which
        // is indistinguishable from having hung. Every phase gets its own plain-English status, and every
        // phase also shows time-since-last-output so a genuinely novel silent gap still reads as "quiet
        // but alive, ticking" rather than "frozen".
        private enum BuildPhase { Starting, InstallingPackage, PreparingToCompile, Compiling, Linking, Finished }

        private readonly AppServices _services;
        private readonly GitHubRelease _release;
        private readonly AnalysisResult _analysis;
        private readonly BuildConfig _config;

        private readonly DispatcherTimer _timer;
        private readonly DateTime _buildStartedAt = DateTime.Now;
        private DateTime _attemptStartedAt = DateTime.Now;
        private DateTime _lastOutputAt = DateTime.Now;
        private int _attempt = 1;
        private bool _finished;
        private BuildPhase _phase = BuildPhase.Starting;
        private string? _installingPackage;

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
                if (!_services.PlatformIoInstaller.IsInstalled() && !await EnsurePlatformIoInstalledAsync(progress))
                {
                    _finished = true;
                    UpdateSummary();
                    return;
                }

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

        /// <summary>
        /// PlatformIO Core is a hard dependency this app doesn't bundle. If it's missing, ask before
        /// doing anything — installing software on someone's machine isn't something to do silently,
        /// even from an official source — then run the actual install invisibly (no console window)
        /// with progress streamed into the same log this page already shows for the build itself.
        /// </summary>
        private async Task<bool> EnsurePlatformIoInstalledAsync(IProgress<string> progress)
        {
            var confirmed = MessageBox.Show(
                "PlatformIO Core is required to compile firmware, but wasn't found on this machine.\n\n" +
                "Install it now? This downloads and runs PlatformIO's own official installer script " +
                "(requires Python to already be installed).",
                "PlatformIO Core required", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            if (!confirmed)
            {
                StatusText.Text = "Build cancelled — PlatformIO Core is required.";
                return false;
            }

            AppendLine("PlatformIO Core not found — installing...");
            var installed = await _services.PlatformIoInstaller.InstallAsync(progress);
            if (!installed)
            {
                StatusText.Text = "PlatformIO Core installation failed — see the log above.";
                return false;
            }

            return true;
        }

        private void AppendLine(string line)
        {
            _lastOutputAt = DateTime.Now;

            var attemptMatch = AttemptRegex.Match(line);
            if (attemptMatch.Success && int.TryParse(attemptMatch.Groups[1].Value, out var attempt) && attempt != _attempt)
            {
                _attempt = attempt;
                _attemptStartedAt = DateTime.Now;
                _phase = BuildPhase.Starting;
            }

            var installMatch = ToolInstallRegex.Match(line);
            if (installMatch.Success)
            {
                // The line is a URL to the package archive — show just the filename, not the whole URL.
                var url = installMatch.Groups[1].Value;
                _installingPackage = url[(url.LastIndexOf('/') + 1)..];
                _phase = BuildPhase.InstallingPackage;
            }
            else if (ToolInstalledRegex.IsMatch(line))
            {
                _installingPackage = null;
                _phase = BuildPhase.PreparingToCompile;
            }
            else if (ScanningRegex.IsMatch(line))
            {
                _phase = BuildPhase.PreparingToCompile;
            }
            else if (CompilingFileRegex.IsMatch(line))
            {
                _phase = BuildPhase.Compiling;
            }
            else if (LinkingRegex.IsMatch(line))
            {
                _phase = BuildPhase.Linking;
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
            var sinceLastOutput = FormatDuration(DateTime.Now - _lastOutputAt);

            // The page's own top-level "Step 3" header must not claim your firmware is building while
            // it's actually still doing one-time prerequisite setup — that's a separate task the app is
            // doing on your behalf, not step 3 of reducing your firmware's size.
            HeaderText.Text = _phase is BuildPhase.InstallingPackage or BuildPhase.PreparingToCompile
                ? "Step 3a: Installing prerequisites (one-time, not your firmware build)"
                : "Step 3: Building";

            if (_finished)
            {
                HeaderText.Text = "Step 3: Building";
                SummaryText.Text = $"Finished — attempt {_attempt} of {BuildOrchestrator.MaxTotalAttempts} — total {FormatDuration(totalElapsed)}";
                return;
            }

            SummaryText.Text = _phase switch
            {
                BuildPhase.Starting =>
                    $"Starting... ({FormatDuration(attemptElapsed)})",

                BuildPhase.InstallingPackage =>
                    $"PREREQUISITE SETUP (not your firmware build): downloading ESP32 toolchain component " +
                    $"\"{_installingPackage}\" — a one-time, per-board download/install that only ever happens " +
                    $"once on this machine ({FormatDuration(attemptElapsed)} so far, last output {sinceLastOutput} ago)",

                BuildPhase.PreparingToCompile =>
                    $"PREREQUISITE SETUP (not your firmware build): toolchain installed, now scanning project " +
                    $"dependencies before compiling starts — this can take a few minutes with no visible progress, " +
                    $"that's normal ({FormatDuration(attemptElapsed)} so far, last output {sinceLastOutput} ago)",

                BuildPhase.Linking =>
                    $"Linking your trimmed firmware — attempt {_attempt} of {BuildOrchestrator.MaxTotalAttempts} " +
                    $"({FormatDuration(attemptElapsed)} on this attempt, last output {sinceLastOutput} ago)",

                _ =>
                    $"Compiling your trimmed firmware — attempt {_attempt} of {BuildOrchestrator.MaxTotalAttempts} " +
                    $"— {FormatDuration(attemptElapsed)} on this attempt (total {FormatDuration(totalElapsed)}, " +
                    $"last output {sinceLastOutput} ago)",
            };
        }

        private static string FormatDuration(TimeSpan span) =>
            span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }
}
