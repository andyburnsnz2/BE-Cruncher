using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BE_Cruncher.Models;
using BE_Cruncher.Services;

namespace BE_Cruncher.Pages
{
    public partial class ConfigurationPage : Page
    {
        private readonly AppServices _services;
        private readonly GitHubRelease _release;
        private readonly AnalysisResult _analysis;

        public ConfigurationPage(AppServices services, GitHubRelease release, AnalysisResult analysis)
        {
            InitializeComponent();
            _services = services;
            _release = release;
            _analysis = analysis;

            BoardCombo.ItemsSource = analysis.Boards;
            BatteryCombo.ItemsSource = analysis.Batteries;
            InverterCombo.ItemsSource = analysis.Inverters;
            var protectedPaths = new HashSet<string>(analysis.ProtectedPaths, StringComparer.OrdinalIgnoreCase);
            OptionalModulesList.ItemsSource = analysis.OptionalModules
                .Select(m => new OptionalModuleSelection(m, isProtected: m.SourceFiles.Any(protectedPaths.Contains)))
                .ToList();

            BoardCombo.SelectedItem = FindBestBoardMatch(analysis.Boards, "t-can485", "lilygo")
                ?? analysis.Boards.FirstOrDefault();
            BatteryCombo.SelectedItem = FindByKeyword(analysis.Batteries, c => $"{c.Id} {c.DisplayName}", "tesla")
                ?? analysis.Batteries.FirstOrDefault();
            InverterCombo.SelectedItem = FindByKeyword(analysis.Inverters, c => $"{c.Id} {c.DisplayName}", "fronius")
                ?? analysis.Inverters.FirstOrDefault();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();

        private void StartBuildButton_Click(object sender, RoutedEventArgs e)
        {
            if (BoardCombo.SelectedItem is not BoardInfo board)
            {
                StatusText.Text = "Select a board.";
                return;
            }
            if (BatteryCombo.SelectedItem is not ComponentInfo battery)
            {
                StatusText.Text = "Select a battery.";
                return;
            }
            if (InverterCombo.SelectedItem is not ComponentInfo inverter)
            {
                StatusText.Text = "Select an inverter.";
                return;
            }

            var selectedModules = ((List<OptionalModuleSelection>)OptionalModulesList.ItemsSource)
                .Where(m => m.IsSelected)
                .Select(m => m.Module.Id)
                .ToList();

            var config = new BuildConfig
            {
                Version = _release.TagName,
                BoardId = board.Id,
                BatteryId = battery.Id,
                InverterId = inverter.Id,
                OptionalModuleIds = selectedModules,
                StripWebUiSections = StripWebUiCheckBox.IsChecked == true,
            };

            NavigationService?.Navigate(new BuildProgressPage(_services, _release, _analysis, config));
        }

        private static T? FindByKeyword<T>(IEnumerable<T> items, Func<T, string> selector, string keyword) =>
            items.FirstOrDefault(i => selector(i).Contains(keyword, StringComparison.OrdinalIgnoreCase));

        private static readonly string[] NonHardwareBoardKeywords = ["warning_check", "warning check", "_test", " test"];

        // Boards can include CI/dev-tooling environments (e.g. a strict -Werror "compiler warning
        // check" build) that still mention the real board name in their description. Exclude those
        // before matching, then try increasingly specific board-name keywords.
        private static BoardInfo? FindBestBoardMatch(IEnumerable<BoardInfo> boards, params string[] keywordsInPriorityOrder)
        {
            string Text(BoardInfo b) => $"{b.Id} {b.DisplayName} {b.BuildFlag}";

            var candidates = boards.Where(b => !NonHardwareBoardKeywords.Any(k => Text(b).Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (candidates.Count == 0)
                candidates = boards.ToList();

            foreach (var keyword in keywordsInPriorityOrder)
            {
                var match = candidates.FirstOrDefault(b => Text(b).Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }

            return candidates.FirstOrDefault();
        }

        private sealed class OptionalModuleSelection
        {
            // Default to kept/checked — this list is opt-out (uncheck something to exclude it), not
            // opt-in, so a build where the user never touches this list keeps every module present,
            // matching the standard/stock firmware's behavior instead of silently stripping everything.
            public OptionalModuleSelection(OptionalModuleInfo module, bool isProtected)
            {
                Module = module;
                IsProtected = isProtected;
            }

            public OptionalModuleInfo Module { get; }
            public bool IsProtected { get; }

            // A module whose files overlap the analyzer's safety/watchdog/precharge/contactor/interlock
            // keyword protection can never actually be excluded (BuildGenerator refuses to delete those
            // files no matter what), so the checkbox is disabled and forced on to avoid implying a
            // choice the app won't honor.
            public string DisplayName => IsProtected ? $"{Module.DisplayName} (protected — cannot be excluded)" : Module.DisplayName;
            public bool IsEnabled => !IsProtected;
            public bool IsSelected { get; set; } = true;
        }
    }
}
