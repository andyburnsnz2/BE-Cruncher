using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Runs the full build pipeline for a BuildConfig: establishes a baseline (standard) firmware size
/// for comparison, generates the trimmed workspace, compiles it, and — on failure — makes a bounded
/// number of deterministic recovery attempts (restoring a wrongly-excluded file from the untouched
/// Original Source, traced from the compiler/linker's own error output). No AI is involved anywhere
/// in this path; the compiler is the only authority, and every recovery attempt is verified by
/// recompiling. Writes the final BuildReport to Output/.
/// </summary>
public sealed class BuildOrchestrator
{
    // Excluding many optional modules at once can need a long chain of one-file-at-a-time restores
    // (e.g. a shared header pulls back its own sibling headers one discovery per attempt — see project
    // memory) — verified needing up to 10 attempts for the worst case (every optional module excluded
    // simultaneously), so this leaves real headroom rather than sitting right at the observed ceiling.
    public const int MaxTotalAttempts = 20;

    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    private readonly WorkspaceService _workspaceService;
    private readonly BuildGenerator _buildGenerator;
    private readonly PlatformIoBuildService _platformIoBuildService;
    private readonly BaselineSizeService _baselineSizeService;

    public BuildOrchestrator(
        WorkspaceService workspaceService,
        BuildGenerator buildGenerator,
        PlatformIoBuildService platformIoBuildService,
        BaselineSizeService baselineSizeService)
    {
        _workspaceService = workspaceService;
        _buildGenerator = buildGenerator;
        _platformIoBuildService = platformIoBuildService;
        _baselineSizeService = baselineSizeService;
    }

    public async Task<BuildReport> RunAsync(
        string originalSourceDir,
        AnalysisResult analysis,
        BuildConfig config,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var board = analysis.Boards.FirstOrDefault(b => b.Id == config.BoardId)
            ?? throw new InvalidOperationException($"Board '{config.BoardId}' not found in analysis.");

        var workspace = _workspaceService.CreateWorkspace(originalSourceDir);

        BaselineSize? baseline = null;
        try
        {
            baseline = await _baselineSizeService.GetOrBuildBaselineAsync(
                config.Version, originalSourceDir, board.PlatformIoEnv, board.BuildFlag, workspace.OutputDir, progress, ct);
        }
        catch (Exception ex)
        {
            progress?.Report($"Baseline build skipped: {ex.Message}");
        }

        progress?.Report("Generating trimmed build...");
        var manifest = _buildGenerator.Generate(workspace, analysis, config, progress);

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var repairNotes = new List<string>();
        var remainingExcludedFiles = manifest.ExcludedBatteryFiles
            .Concat(manifest.ExcludedInverterFiles)
            .Concat(manifest.ExcludedOptionalModuleFiles)
            .ToList();
        BuildResult result;

        while (true)
        {
            attempts++;
            progress?.Report($"Compiling ({manifest.PlatformIoEnvironment}), attempt {attempts}...");
            result = await _platformIoBuildService.BuildAsync(workspace, manifest.PlatformIoEnvironment, progress, ct);

            if (result.Success || attempts >= MaxTotalAttempts)
                break;

            var buildLog = File.ReadAllText(result.LogFilePath);

            // Missing-header and undefined-symbol failures almost always mean the file-exclusion
            // step deleted a file that's transitively needed (a shared feature misfiled under one
            // driver's folder, or a .cpp/.h pair split across the exclusion boundary). Restore the
            // exact excluded file(s) straight from the untouched Original Source and recompile — a
            // wrongly-excluded file can require several rounds of transitive restoration (A needs B
            // needs C) before the dependency graph settles.
            var restoredFiles = TryRestoreFilesForMissingHeaders(buildLog, originalSourceDir, workspace, remainingExcludedFiles);
            if (restoredFiles.Count == 0)
                restoredFiles = TryRestoreFilesForUndefinedSymbols(buildLog, originalSourceDir, workspace, remainingExcludedFiles);

            if (restoredFiles.Count > 0)
            {
                repairNotes.Add(
                    $"Attempt {attempts}: build failure traced to previously-excluded file(s); restored from " +
                    $"Original Source: {string.Join(", ", restoredFiles)}.");
                continue;
            }

            repairNotes.Add(
                $"Attempt {attempts}: build failed and no previously-excluded file could be traced to the error " +
                $"(not a missing-header or undefined-symbol failure this tool can auto-recover). See the log at " +
                $"{result.LogFilePath} for the actual compiler/linker output.");
            break;
        }

        stopwatch.Stop();

        var report = new BuildReport
        {
            Config = config,
            PlatformIoEnvironment = manifest.PlatformIoEnvironment,
            Success = result.Success,
            Attempts = attempts,
            WarningCount = result.WarningCount,
            ErrorCount = result.ErrorCount,
            TotalDuration = stopwatch.Elapsed,
            RamUsedBytes = result.RamUsedBytes,
            RamTotalBytes = result.RamTotalBytes,
            FlashUsedBytes = result.FlashUsedBytes,
            FlashTotalBytes = result.FlashTotalBytes,
            Baseline = baseline,
            Warnings = manifest.Warnings,
            RepairNotes = repairNotes,
            FirmwareBinPath = result.FirmwareBinPath,
            OutputDir = workspace.OutputDir,
            LogsDir = workspace.LogsDir,
        };

        Directory.CreateDirectory(workspace.OutputDir);
        File.WriteAllText(
            Path.Combine(workspace.OutputDir, "build-report.json"),
            JsonSerializer.Serialize(report, FileJsonOptions));

        return report;
    }

    /// <summary>
    /// Finds which previously-excluded file(s) are named after a header the compiler reported as
    /// missing ("fatal error: X.h: No such file or directory") — a transitively-needed dependency
    /// from a different, still-excluded driver, not necessarily the file that failed to compile.
    /// </summary>
    private static List<string> TryRestoreFilesForMissingHeaders(
        string buildLog, string originalSourceDir, Workspace workspace, List<string> remainingExcludedFiles)
    {
        var missingHeaders = BuildErrorParser.ParseMissingHeaders(buildLog.Split('\n'));
        if (missingHeaders.Count == 0)
            return [];

        return RestoreMatchingFiles(
            originalSourceDir, workspace, remainingExcludedFiles,
            relativeFile => missingHeaders.Any(h => h.Equals(Path.GetFileName(relativeFile), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Finds which previously-excluded file(s) define any of the symbols a linker error reported as
    /// missing.
    /// </summary>
    private static List<string> TryRestoreFilesForUndefinedSymbols(
        string buildLog, string originalSourceDir, Workspace workspace, List<string> remainingExcludedFiles)
    {
        var symbols = BuildErrorParser.ParseUndefinedSymbols(buildLog.Split('\n'));
        if (symbols.Count == 0)
            return [];

        return RestoreMatchingFiles(
            originalSourceDir, workspace, remainingExcludedFiles,
            relativeFile =>
            {
                var originalPath = Path.Combine(originalSourceDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                string content;
                try { content = File.ReadAllText(originalPath); }
                catch (IOException) { return false; }
                return symbols.Any(s => Regex.IsMatch(content, $@"\b{Regex.Escape(s)}\b"));
            });
    }

    /// <summary>
    /// Restores every remaining excluded file matched by <paramref name="matches"/> from the
    /// untouched Original Source into the workspace, and removes them from the tracked exclusion
    /// list. Deliberately restores only the exact matched file, not its .cpp/.h counterpart — e.g. a
    /// missing *header* only needs the header (declarations), not the paired .cpp implementation,
    /// which can pull in its own further dependencies nothing actually needed. Restoring one side
    /// unnecessarily is worse than a fixable follow-up "missing header"/"undefined reference" on the
    /// next attempt, which these two checks re-run on every failure anyway.
    /// </summary>
    private static List<string> RestoreMatchingFiles(
        string originalSourceDir,
        Workspace workspace,
        List<string> remainingExcludedFiles,
        Func<string, bool> matches)
    {
        var restored = new List<string>();

        foreach (var relativeFile in remainingExcludedFiles.ToList())
        {
            var originalPath = Path.Combine(originalSourceDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(originalPath) || !matches(relativeFile))
                continue;

            var destPath = Path.Combine(workspace.SourceDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(originalPath, destPath, overwrite: true);

            restored.Add(relativeFile);
            remainingExcludedFiles.Remove(relativeFile);
        }

        return restored;
    }
}
