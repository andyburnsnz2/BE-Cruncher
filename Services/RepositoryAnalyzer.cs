using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Deterministically discovers boards, batteries, inverters, optional modules, and protected paths
/// from a Battery Emulator source tree — no AI, no network calls. Boards come from platformio.ini's
/// [env:*] sections; batteries/inverters come from parsing the registration file's enum + factory
/// switch + name-lookup switch (see CppRegistrationParser); optional modules and protected paths come
/// from structural folder/macro scanning (see RepositoryStructureHelper).
/// </summary>
public sealed class RepositoryAnalyzer
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    private readonly AppPaths _paths;

    public RepositoryAnalyzer(AppPaths paths) => _paths = paths;

    public bool HasCachedAnalysis(string tagName) => File.Exists(_paths.AnalysisFile(tagName));

    public AnalysisResult LoadCachedAnalysis(string tagName) =>
        JsonSerializer.Deserialize<AnalysisResult>(File.ReadAllText(_paths.AnalysisFile(tagName)), FileJsonOptions)
        ?? throw new InvalidOperationException("Cached analysis.json could not be parsed.");

    public AnalysisResult Analyze(string tagName, string sourceRoot, bool forceRefresh = false)
    {
        if (!forceRefresh && HasCachedAnalysis(tagName))
            return LoadCachedAnalysis(tagName);

        var boards = ParseBoards(sourceRoot);

        var batteryFolder = RepositoryStructureHelper.FindComponentFolder(sourceRoot, @"^batter(y|ies)$");
        var inverterFolder = RepositoryStructureHelper.FindComponentFolder(sourceRoot, @"^inverters?$");

        var batteries = ParseComponents(sourceRoot, batteryFolder);
        var inverters = ParseComponents(sourceRoot, inverterFolder);

        var optionalModules = RepositoryStructureHelper.FindOptionalModules(sourceRoot);
        var protectedPaths = RepositoryStructureHelper.FindProtectedPaths(sourceRoot);

        var result = new AnalysisResult
        {
            Boards = boards,
            Batteries = batteries,
            Inverters = inverters,
            OptionalModules = optionalModules,
            ProtectedPaths = protectedPaths,
            PlatformIo = new PlatformIoInfo
            {
                SourceDir = ParseSourceDir(sourceRoot),
                ConfigFile = "platformio.ini",
                Environments = boards.Select(b => b.PlatformIoEnv).ToList(),
            },
            Notes =
                "Deterministically parsed from source — no AI involved. Boards come from platformio.ini " +
                "[env:*] sections; batteries/inverters come from parsing the registration file's enum, " +
                "factory switch, and name-lookup switch; a component's files are only ever considered " +
                "for exclusion if no other component's local #include closure also references them.",
            Version = tagName,
            AnalyzedAt = DateTimeOffset.UtcNow,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.AnalysisFile(tagName))!);
        File.WriteAllText(_paths.AnalysisFile(tagName), JsonSerializer.Serialize(result, FileJsonOptions));

        return result;
    }

    private static List<BoardInfo> ParseBoards(string sourceRoot)
    {
        var iniPath = Path.Combine(sourceRoot, "platformio.ini");
        if (!File.Exists(iniPath))
            return [];

        var content = File.ReadAllText(iniPath);
        var sections = Regex.Matches(content, @"\[env:([\w-]+)\]([\s\S]*?)(?=\r?\n\[|\z)");

        var boards = new List<BoardInfo>();
        foreach (Match section in sections)
        {
            var envName = section.Groups[1].Value;
            var body = section.Groups[2].Value;

            var flagMatch = Regex.Match(body, @"-D\s*(HW_\w+)");
            var buildFlag = flagMatch.Success ? flagMatch.Groups[1].Value : "";

            boards.Add(new BoardInfo
            {
                Id = envName,
                DisplayName = buildFlag.Length > 0 ? $"{envName} ({buildFlag})" : envName,
                BuildFlag = buildFlag,
                PlatformIoEnv = envName,
            });
        }

        return boards;
    }

    private static string ParseSourceDir(string sourceRoot)
    {
        var iniPath = Path.Combine(sourceRoot, "platformio.ini");
        if (!File.Exists(iniPath))
            return "./Software";

        var match = Regex.Match(File.ReadAllText(iniPath), @"src_dir\s*=\s*(\S+)");
        return match.Success ? match.Groups[1].Value : "./Software";
    }

    private static List<ComponentInfo> ParseComponents(string sourceRoot, string? folder)
    {
        if (folder is null)
            return [];

        var (cppPath, hPath) = RepositoryStructureHelper.FindRegistrationFilePair(folder);
        if (cppPath is null)
            return [];

        var content = File.ReadAllText(cppPath);
        var parsed = CppRegistrationParser.Parse(folder, content, cppPath, hPath);
        if (parsed is null)
            return [];

        return parsed.Components.Select(c => new ComponentInfo
        {
            Id = ToId(c.EnumValue),
            DisplayName = c.DisplayName,
            EnumValue = $"{parsed.EnumTypeName}::{c.EnumValue}",
            SourceFiles = c.ExclusiveFiles.Select(f => ToRelativePath(sourceRoot, f)).ToList(),
            RegistrationNotes = $"Class {c.ClassName}; registered via factory switch in {Path.GetFileName(cppPath)}.",
        }).ToList();
    }

    private static string ToId(string enumValue) =>
        Regex.Replace(enumValue, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();

    private static string ToRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
