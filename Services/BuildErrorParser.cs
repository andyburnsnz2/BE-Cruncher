using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BE_Cruncher.Services;

public sealed record CompileError(string RelativeFile, int Line, int Column, string Message);

/// <summary>
/// Extracts GCC/Clang-style diagnostics ("path/to/file.cpp:123:45: error: message") from a
/// PlatformIO build log.
/// </summary>
public static class BuildErrorParser
{
    private static readonly Regex ErrorRegex = new(
        @"^(?<file>[\w\-./\\]+\.(?:cpp|h|hpp|cc|c))\s*:\s*(?<line>\d+)\s*:\s*(?<col>\d+)\s*:\s*(?:fatal error|error)\s*:\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex UndefinedReferenceRegex = new(
        @"undefined reference to `([^']+)'",
        RegexOptions.Compiled);

    private static readonly Regex MissingHeaderRegex = new(
        @"fatal error:\s*([\w\-./\\]+\.(?:h|hpp)):\s*No such file or directory",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<CompileError> ParseErrors(IEnumerable<string> logLines) =>
        logLines
            .Select(l => ErrorRegex.Match(l.Trim()))
            .Where(m => m.Success)
            .Select(m => new CompileError(
                m.Groups["file"].Value.Replace('\\', '/'),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["msg"].Value))
            .ToList();

    /// <summary>
    /// Extracts bare symbol names (function/variable identifiers, argument lists stripped) from
    /// linker "undefined reference to `symbol'" diagnostics.
    /// </summary>
    public static List<string> ParseUndefinedSymbols(IEnumerable<string> logLines) =>
        logLines
            .Select(l => UndefinedReferenceRegex.Match(l))
            .Where(m => m.Success)
            .Select(m =>
            {
                var symbol = m.Groups[1].Value;
                var parenIndex = symbol.IndexOf('(');
                return parenIndex >= 0 ? symbol[..parenIndex] : symbol;
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Extracts bare header filenames (e.g. "CHADEMO-BATTERY.h") from "fatal error: X.h: No such
    /// file or directory" diagnostics — a transitively-needed header from a different, still-excluded
    /// driver, not necessarily the file that failed to compile.
    /// </summary>
    public static List<string> ParseMissingHeaders(IEnumerable<string> logLines) =>
        logLines
            .Select(l => MissingHeaderRegex.Match(l))
            .Where(m => m.Success)
            // GCC reports the header exactly as written in the #include (often with a path prefix,
            // e.g. "../battery/BATTERIES.h" or "src/battery/BATTERIES.h") — reduce to the bare
            // filename since that's what every excluded-file match is keyed on.
            .Select(m => Path.GetFileName(m.Groups[1].Value.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolves a compiler-reported (possibly prefix-relative) file path to an actual file under
    /// the workspace source directory.
    /// </summary>
    public static string? ResolveFile(string workspaceSourceDir, string reportedPath)
    {
        var direct = Path.Combine(workspaceSourceDir, reportedPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct))
            return direct;

        var normalizedSuffix = reportedPath.Replace('\\', '/');
        return Directory.EnumerateFiles(workspaceSourceDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetRelativePath(workspaceSourceDir, f)
                .Replace('\\', '/')
                .EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase));
    }
}
