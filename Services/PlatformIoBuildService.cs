using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Invokes the PlatformIO CLI against a workspace's Source copy and captures the results.
/// </summary>
public sealed class PlatformIoBuildService
{
    public async Task<BuildResult> BuildAsync(
        Workspace workspace,
        string environment,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var logPath = Path.Combine(workspace.LogsDir, "build.log");
        var stopwatch = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = PlatformIoInstaller.ResolvePioCommand(),
            ArgumentList = { "run", "-e", environment },
            WorkingDirectory = workspace.SourceDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var allLines = new List<string>();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data, allLines, progress);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, allLines, progress);

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "PlatformIO Core ('pio') was not found. BE Cruncher compiles firmware through it, but does not " +
                "bundle it — install it from https://platformio.org/install/cli, then restart BE Cruncher (a new " +
                "terminal/app session is needed after install for the PATH change to take effect).");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        stopwatch.Stop();

        File.WriteAllLines(logPath, allLines);

        // Matches the standard GCC/Clang diagnostic shape ("file:line:col: warning: ...") rather than
        // any line containing the word "warning" — PlatformIO's own banner text ("*** WARNING: ...",
        // "Warning! Ignore ...") would otherwise be miscounted as compiler diagnostics.
        var warningCount = allLines.Count(l => Regex.IsMatch(l, @":\s*warning:", RegexOptions.IgnoreCase));
        // "fatal error:" (e.g. a missing header) has a space, not a colon, before "error:" — match both forms.
        var errorCount = allLines.Count(l => Regex.IsMatch(l, @":\s*(?:fatal\s+)?error:", RegexOptions.IgnoreCase));

        var (ramUsed, ramTotal) = ParseMemoryLine(allLines, "RAM:");
        var (flashUsed, flashTotal) = ParseMemoryLine(allLines, "Flash:");

        var firmwarePath = CopyFirmwareToOutput(workspace, environment);

        return new BuildResult
        {
            Success = process.ExitCode == 0 && firmwarePath is not null,
            ExitCode = process.ExitCode,
            WarningCount = warningCount,
            ErrorCount = errorCount,
            RamUsedBytes = ramUsed,
            RamTotalBytes = ramTotal,
            FlashUsedBytes = flashUsed,
            FlashTotalBytes = flashTotal,
            FirmwareBinPath = firmwarePath,
            LogFilePath = logPath,
            Duration = stopwatch.Elapsed,
        };
    }

    private static void OnLine(string? line, List<string> allLines, IProgress<string>? progress)
    {
        if (line is null)
            return;
        allLines.Add(line);
        progress?.Report(line);
    }

    private static string? CopyFirmwareToOutput(Workspace workspace, string environment)
    {
        var candidate = Path.Combine(workspace.SourceDir, ".pio", "build", environment, "firmware.bin");
        if (!File.Exists(candidate))
            return null;

        Directory.CreateDirectory(workspace.OutputDir);
        var dest = Path.Combine(workspace.OutputDir, "firmware.bin");
        File.Copy(candidate, dest, overwrite: true);

        var mergedCandidate = Path.Combine(workspace.SourceDir, ".pio", "build", environment, "firmware.factory.bin");
        if (File.Exists(mergedCandidate))
            File.Copy(mergedCandidate, Path.Combine(workspace.OutputDir, "firmware.merged.bin"), overwrite: true);

        return dest;
    }

    private static (long? Used, long? Total) ParseMemoryLine(List<string> lines, string prefix)
    {
        var line = lines.FirstOrDefault(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
            return (null, null);

        var match = Regex.Match(line, @"used\s+([\d,]+)\s+bytes\s+from\s+([\d,]+)\s+bytes");
        if (!match.Success)
            return (null, null);

        return (
            long.Parse(match.Groups[1].Value.Replace(",", "")),
            long.Parse(match.Groups[2].Value.Replace(",", "")));
    }
}
