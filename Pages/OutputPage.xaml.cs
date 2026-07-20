using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BE_Cruncher.Models;
using BE_Cruncher.Services;

namespace BE_Cruncher.Pages
{
    public partial class OutputPage : Page
    {
        private readonly AppServices _services;
        private readonly BuildReport _report;

        public OutputPage(AppServices services, BuildReport report)
        {
            InitializeComponent();
            _services = services;
            _report = report;

            HeaderText.Text = report.Success ? "Build succeeded" : "Build failed";
            HeaderText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
                report.Success ? "SuccessBrush" : "ErrorBrush"];

            SizeText.Text = BuildSizeSummary(report);
            DetailsText.Text =
                $"Board/battery/inverter config: {report.Config.BoardId} / {report.Config.BatteryId} / {report.Config.InverterId}\n" +
                $"PlatformIO environment: {report.PlatformIoEnvironment}\n" +
                $"Warnings: {report.WarningCount}    Errors: {report.ErrorCount}\n" +
                $"Compile attempts: {report.Attempts}    Total duration: {report.TotalDuration:mm\\:ss}\n" +
                $"Firmware: {report.FirmwareBinPath ?? "(not produced)"}\n" +
                $"Standard firmware (for comparison): {report.Baseline?.ReferenceBinPath ?? "(not downloaded — see size summary for why)"}";

            RepairText.Text = report.RepairNotes.Count > 0
                ? string.Join("\n", report.RepairNotes)
                : "(none needed)";

            WarningsText.Text = report.Warnings.Count > 0
                ? string.Join("\n", report.Warnings)
                : "(none)";

            OpenOutputButton.IsEnabled = Directory.Exists(report.OutputDir);
            OpenLogsButton.IsEnabled = Directory.Exists(report.LogsDir);
        }

        private static string BuildSizeSummary(BuildReport report)
        {
            if (report.FlashUsedBytes is not { } used || report.FlashTotalBytes is not { } total)
                return "Flash size unavailable.";

            var summary = $"Flash used: {used:N0} / {total:N0} bytes ({(double)used / total:P1})";

            if (report.Baseline is { } baseline && report.FlashBytesSaved is { } saved && report.FlashPercentReduction is { } pct)
            {
                // The app partition size (the "/ total" denominator) is fixed by the board's partition
                // table, not by which firmware is in it — reuse this build's own total rather than the
                // baseline's, since a web-installer-sourced baseline doesn't carry that figure at all.
                summary +=
                    $"\nStandard build used: {baseline.FlashUsedBytes:N0} / {total:N0} bytes ({baseline.Source})" +
                    $"\nReduction: {saved:N0} bytes smaller ({pct:F1}% less flash than the standard build)";
            }
            else
            {
                summary += "\n(No baseline size available for comparison.)";
            }

            return summary;
        }

        private void OpenOutputButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_report.OutputDir);

        private void OpenLogsButton_Click(object sender, RoutedEventArgs e) => OpenFolder(_report.LogsDir);

        private static void OpenFolder(string path)
        {
            if (!Directory.Exists(path))
                return;
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        private void BackToVersionsButton_Click(object sender, RoutedEventArgs e) =>
            NavigationService?.Navigate(new VersionsPage(_services));
    }
}
