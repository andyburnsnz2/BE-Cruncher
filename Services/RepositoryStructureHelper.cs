using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Structural helpers for locating things in a Battery Emulator source tree without hard-coding
/// version-specific names — folder-name patterns, #include density, and #ifdef macro scanning.
/// </summary>
public static class RepositoryStructureHelper
{
    private static readonly string[] SafetyKeywords = ["safety", "watchdog", "precharge", "contactor", "interlock", "fault"];
    private static readonly string[] SkipDirNames = [".git", ".github", "test", "tools", "web_data", "lib", ".vs", "bin", "obj"];
    private static readonly string[] CoreDevboardFolders = ["hal", "utils"];

    public static string? FindComponentFolder(string sourceRoot, string namePattern) => FindDirectory(sourceRoot, namePattern);

    /// <summary>
    /// Finds the folder's registration/factory .cpp file using #include density (the file that
    /// #includes every driver header) plus its matching .h, without relying on a specific file name.
    /// </summary>
    public static (string? CppPath, string? HPath) FindRegistrationFilePair(string dir)
    {
        var registrationCpp = Directory.EnumerateFiles(dir, "*.cpp", SearchOption.TopDirectoryOnly)
            .Select(f => (Path: f, IncludeCount: CountLocalIncludes(f)))
            .OrderByDescending(x => x.IncludeCount)
            .FirstOrDefault();

        if (registrationCpp.Path is null || registrationCpp.IncludeCount <= 3)
            return (null, null);

        var headerPath = Path.ChangeExtension(registrationCpp.Path, ".h");
        return (registrationCpp.Path, File.Exists(headerPath) ? headerPath : null);
    }

    /// <summary>
    /// Files whose path mentions battery-protection-adjacent keywords (safety, watchdog, precharge,
    /// contactor, interlock, fault) — never modified or excluded by any build, regardless of what a
    /// (deterministic or AI) analysis pass concludes about component ownership.
    /// </summary>
    public static List<string> FindProtectedPaths(string sourceRoot)
    {
        return Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(f => !GetRelativeParts(sourceRoot, f).Any(part => SkipDirNames.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Where(f => SafetyKeywords.Any(k => f.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(f => ToRelativePath(sourceRoot, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Every subfolder of the board-support/devboard layer (excluding core infrastructure like hal/
    /// and utils/) as an optional-module candidate, noting whether it already has a compile-time
    /// guard macro.
    /// </summary>
    public static List<OptionalModuleInfo> FindOptionalModules(string sourceRoot)
    {
        var devboardDir = FindDirectory(sourceRoot, @"^dev.?board$");
        if (devboardDir is null)
            return [];

        var modules = new List<OptionalModuleInfo>();

        foreach (var sub in Directory.EnumerateDirectories(devboardDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(sub);
            if (CoreDevboardFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var files = Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).ToList();
            var macros = files
                .SelectMany(f =>
                {
                    var text = SafeReadAllText(f);
                    var ifdefMatches = Regex.Matches(text, @"#\s*if(?:n?def|\s+defined)?\s*\(?\s*(?:defined\s*\(?)?\s*([A-Z_][A-Z0-9_]*)");
                    var definedMatches = Regex.Matches(text, @"defined\s*\(\s*([A-Z_][A-Z0-9_]*)");
                    return ifdefMatches.Concat(definedMatches);
                })
                .Select(m => m.Groups[1].Value)
                .Where(m => !IsLikelyIncludeGuard(m) && !m.StartsWith("HW_", StringComparison.Ordinal))
                .Distinct()
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A file physically sitting in this folder isn't necessarily exclusive to it — e.g.
            // BatteryHtmlRenderer.h lives under devboard/webserver/ but every battery driver's *-HTML.cpp
            // needs it regardless of whether the webserver feature itself is compiled in. Only files
            // nothing outside this folder #includes are safe to ever delete when the module is excluded.
            var externallyReferenced = FindExternallyReferencedBasenames(sourceRoot, sub);
            var exclusiveFiles = files.Where(f => !externallyReferenced.Contains(Path.GetFileName(f))).ToList();

            modules.Add(new OptionalModuleInfo
            {
                Id = name.ToLowerInvariant(),
                DisplayName = Humanize(name),
                HasNativeBuildFlag = macros.Count > 0,
                BuildFlag = macros.FirstOrDefault() ?? "",
                SourceFiles = exclusiveFiles.Select(f => ToRelativePath(sourceRoot, f)).ToList(),
                Notes = (macros.Count > 0
                    ? $"Already gated by: {string.Join(", ", macros)}."
                    : "No compile-time guard macro found in this release; always compiled in.")
                    + (externallyReferenced.Count > 0
                        ? $" {externallyReferenced.Count} file(s) in this folder are also used elsewhere in the codebase and are never removed."
                        : ""),
            });
        }

        return modules;
    }

    private static readonly string[] SourceExtensions = [".cpp", ".h", ".hpp", ".cc"];

    /// <summary>
    /// Basenames of files inside <paramref name="moduleDir"/> that are #included from at least one file
    /// OUTSIDE that folder anywhere in the source tree — i.e. not actually exclusive to this module.
    /// </summary>
    private static HashSet<string> FindExternallyReferencedBasenames(string sourceRoot, string moduleDir)
    {
        var moduleBasenames = new HashSet<string>(
            Directory.EnumerateFiles(moduleDir, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)),
            StringComparer.OrdinalIgnoreCase);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            if (GetRelativeParts(sourceRoot, file).Any(p => SkipDirNames.Contains(p, StringComparer.OrdinalIgnoreCase)))
                continue;
            if (IsUnder(file, moduleDir))
                continue; // an include from within the module's own folder doesn't make a file "external"

            foreach (Match m in Regex.Matches(SafeReadAllText(file), "^\\s*#include\\s+\"([^\"]+)\"", RegexOptions.Multiline))
            {
                var basename = Path.GetFileName(m.Groups[1].Value);
                if (moduleBasenames.Contains(basename))
                    referenced.Add(basename);
            }
        }

        return referenced;
    }

    private static bool IsUnder(string file, string dir) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Humanize(string identifier) =>
        string.Join(" ", identifier.Split(['_', '-']).Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    private static bool IsLikelyIncludeGuard(string macro) =>
        macro.EndsWith("_H", StringComparison.Ordinal) ||
        macro.EndsWith("_H_", StringComparison.Ordinal) ||
        (macro.StartsWith("__", StringComparison.Ordinal) && macro.EndsWith("__", StringComparison.Ordinal));

    private static int CountLocalIncludes(string file) =>
        Regex.Matches(SafeReadAllText(file), "^#include\\s+\"", RegexOptions.Multiline).Count;

    private static string SafeReadAllText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }

    private static string? FindDirectory(string root, string namePattern)
    {
        var regex = new Regex(namePattern, RegexOptions.IgnoreCase);
        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => !GetRelativeParts(root, d).Any(part => SkipDirNames.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .FirstOrDefault(d => regex.IsMatch(Path.GetFileName(d)));
    }

    private static string ToRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string[] GetRelativeParts(string root, string fullPath) =>
        ToRelativePath(root, fullPath).Split('/');
}
