using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BE_Cruncher.Services;

/// <summary>
/// Deterministically discovers selectable components (batteries, inverters) from a Battery Emulator
/// source folder by parsing its C++ directly — no AI. Relies on a consistent, observed upstream
/// pattern: an "enum class TypeName { Value = N, ... }" plus a factory switch
/// ("case TypeName::Value: return new ClassName(...);") and a name-lookup switch
/// ("case TypeName::Value: return ClassName::Member;" or a string literal) in the registration file.
/// </summary>
public static class CppRegistrationParser
{
    private static readonly Regex CaseLabelRegex = new(@"^\s*case\s+(\w+)::(\w+)\s*:\s*$", RegexOptions.Compiled);
    private static readonly Regex SwitchStartRegex = new(@"switch\s*\([^)]*\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex NewClassRegex = new(@"new\s+(\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ReturnLiteralRegex = new(@"return\s+""([^""]*)""\s*;", RegexOptions.Compiled);
    private static readonly Regex ReturnScopedRegex = new(@"return\s+(\w+)::(\w+)\s*;", RegexOptions.Compiled);
    private static readonly Regex ClassDeclRegex = new(@"^\s*class\s+(\w+)\s*[:{ ]", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex LocalIncludeRegex = new("^\\s*#include\\s+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly string[] SentinelEnumValues = ["None", "Highest", "Unknown", "Invalid"];

    public sealed record CaseGroup(
        List<string> Labels,
        List<int> LabelLineIndices,
        int BodyStartLine,
        int BodyEndLineExclusive,
        string Body);

    public sealed record SwitchInfo(string EnumTypeName, List<CaseGroup> Groups);

    /// <summary>
    /// Finds every switch statement whose case labels are "EnumType::Value", grouped by switch.
    /// </summary>
    public static List<SwitchInfo> FindEnumSwitches(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var switches = new List<SwitchInfo>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!SwitchStartRegex.IsMatch(lines[i]))
                continue;

            var depth = CountChar(lines[i], '{') - CountChar(lines[i], '}');
            var end = i;
            while (depth > 0 && ++end < lines.Length)
                depth += CountChar(lines[end], '{') - CountChar(lines[end], '}');
            if (depth != 0)
                continue; // unbalanced — skip rather than mis-parse

            var groups = ParseCaseGroups(lines, i + 1, end);
            if (groups.Count == 0)
                continue;

            var enumTypeName = groups[0].Labels[0].Split("::")[0];
            switches.Add(new SwitchInfo(enumTypeName, groups));

            i = end;
        }

        return switches;
    }

    private static List<CaseGroup> ParseCaseGroups(string[] lines, int start, int endExclusive)
    {
        var groups = new List<CaseGroup>();
        var pendingLabels = new List<string>();
        var pendingLabelLines = new List<int>();
        var bodyStart = -1;

        void CloseGroup(int bodyEnd)
        {
            if (pendingLabels.Count == 0)
                return;
            var body = string.Join('\n', lines[bodyStart..bodyEnd]);
            groups.Add(new CaseGroup([.. pendingLabels], [.. pendingLabelLines], bodyStart, bodyEnd, body));
            pendingLabels = [];
            pendingLabelLines = [];
        }

        for (var i = start; i < endExclusive; i++)
        {
            var caseMatch = CaseLabelRegex.Match(lines[i]);
            if (caseMatch.Success)
            {
                if (bodyStart >= 0 && bodyStart < i)
                    CloseGroup(i);
                pendingLabels.Add($"{caseMatch.Groups[1].Value}::{caseMatch.Groups[2].Value}");
                pendingLabelLines.Add(i);
                bodyStart = i + 1;
                continue;
            }

            if (Regex.IsMatch(lines[i], @"^\s*default\s*:\s*$"))
            {
                CloseGroup(i);
                bodyStart = -1;
            }
        }

        CloseGroup(endExclusive);
        return groups;
    }

    private static int CountChar(string s, char c) => s.Count(ch => ch == c);

    /// <summary>
    /// Finds and parses "enum class TypeName { Value = N, ... };" from any .h file in the folder.
    /// </summary>
    public static List<string>? FindAndParseEnum(string folderDir, string enumTypeName)
    {
        foreach (var file in Directory.EnumerateFiles(folderDir, "*.h", SearchOption.TopDirectoryOnly))
        {
            var content = SafeReadAllText(file);
            var match = Regex.Match(content, $@"enum(?:\s+class)?\s+{Regex.Escape(enumTypeName)}\s*\{{([^}}]*)\}}");
            if (!match.Success)
                continue;

            return match.Groups[1].Value
                .Split(',')
                .Select(v => v.Split('=')[0].Trim())
                .Where(v => v.Length > 0 && !v.StartsWith("//"))
                .ToList();
        }

        return null;
    }

    public static string? ExtractClassNameFromNew(string body)
    {
        var match = NewClassRegex.Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolves a name-lookup case body ("return "Literal";" or "return ClassName::Member;") to the
    /// actual display string, looking up the class's static string member if needed.
    /// </summary>
    public static string? ResolveDisplayName(string body, string folderDir)
    {
        var literal = ReturnLiteralRegex.Match(body);
        if (literal.Success)
            return literal.Groups[1].Value;

        var scoped = ReturnScopedRegex.Match(body);
        if (!scoped.Success)
            return null;

        var className = scoped.Groups[1].Value;
        var memberName = scoped.Groups[2].Value;

        var headerFile = FindClassHeader(folderDir, className);
        if (headerFile is null)
            return null;

        var content = SafeReadAllText(headerFile);
        var memberMatch = Regex.Match(
            content,
            $@"static\s+constexpr\s+const\s+char\s*\*\s*{Regex.Escape(memberName)}\s*=\s*""([^""]*)""\s*;");
        return memberMatch.Success ? memberMatch.Groups[1].Value : null;
    }

    public static string? FindClassHeader(string folderDir, string className)
    {
        foreach (var file in Directory.EnumerateFiles(folderDir, "*.h", SearchOption.TopDirectoryOnly))
        {
            var content = SafeReadAllText(file);
            if (ClassDeclRegex.Matches(content).Any(m => m.Groups[1].Value == className))
                return file;
        }
        return null;
    }

    /// <summary>
    /// Starting from a class's own .h/.cpp, follows local #include chains within the same folder to
    /// collect every file that pair transitively depends on (e.g. a "-HTML.h" companion file).
    /// </summary>
    public static List<string> ResolveClassFileClosure(string folderDir, string className)
    {
        var header = FindClassHeader(folderDir, className);
        if (header is null)
            return [];

        var basename = Path.GetFileNameWithoutExtension(header);
        var cpp = Path.Combine(folderDir, basename + ".cpp");

        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { header };
        if (File.Exists(cpp))
            closure.Add(cpp);

        var queue = new Queue<string>(closure);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            var content = SafeReadAllText(file);
            foreach (Match m in LocalIncludeRegex.Matches(content))
            {
                var includedName = m.Groups[1].Value;
                if (includedName.Contains('/'))
                    continue; // not a same-folder include

                var includedPath = Path.Combine(folderDir, includedName);
                if (!File.Exists(includedPath) || closure.Contains(includedPath))
                    continue;

                closure.Add(includedPath);
                queue.Enqueue(includedPath);

                // PlatformIO compiles every .cpp in the tree regardless of whether anything
                // #includes it, so a discovered header's sibling .cpp must travel with it —
                // not just the original seed class's pair.
                var siblingCpp = Path.Combine(folderDir, Path.GetFileNameWithoutExtension(includedPath) + ".cpp");
                if (File.Exists(siblingCpp) && closure.Add(siblingCpp))
                    queue.Enqueue(siblingCpp);
            }
        }

        return [.. closure];
    }

    public static bool IsSentinelValue(string enumValueName) =>
        SentinelEnumValues.Contains(enumValueName, StringComparer.OrdinalIgnoreCase);

    public sealed record ParsedComponent(
        string EnumValue,
        string ClassName,
        string DisplayName,
        string PrimaryHeaderFileName,
        List<string> ExclusiveFiles);

    public sealed record ParseResult(string EnumTypeName, List<ParsedComponent> Components);

    /// <summary>
    /// Full pipeline: finds the registration file's primary enum switch, maps each enum value to its
    /// implementing class and display name, and computes each class's exclusive file set (files not
    /// shared with any other component — e.g. a base class or a misfiled shared-feature file stays
    /// unclaimed by everyone, and is therefore never a deletion candidate).
    /// </summary>
    public static ParseResult? Parse(
        string folderDir,
        string registrationCppContent,
        string? registrationCppPath = null,
        string? registrationHPath = null)
    {
        var switches = FindEnumSwitches(registrationCppContent);
        if (switches.Count == 0)
            return null;

        var byEnum = switches
            .GroupBy(s => s.EnumTypeName)
            .OrderByDescending(g => g.Max(s => s.Groups.Sum(gr => gr.Labels.Count)))
            .FirstOrDefault();
        if (byEnum is null)
            return null;

        var enumTypeName = byEnum.Key;
        var enumValues = FindAndParseEnum(folderDir, enumTypeName);
        if (enumValues is null || enumValues.Count == 0)
            return null;

        var factorySwitch = byEnum.OrderByDescending(s => s.Groups.Count(g => ExtractClassNameFromNew(g.Body) is not null)).First();
        var nameSwitch = byEnum.OrderByDescending(s => s.Groups.Count(g => ResolveDisplayName(g.Body, folderDir) is not null)).First();

        var classByEnumValue = new Dictionary<string, string>();
        foreach (var group in factorySwitch.Groups)
        {
            var className = ExtractClassNameFromNew(group.Body);
            if (className is null)
                continue;
            foreach (var label in group.Labels)
                classByEnumValue[label.Split("::")[1]] = className;
        }

        var displayByEnumValue = new Dictionary<string, string>();
        foreach (var group in nameSwitch.Groups)
        {
            var display = ResolveDisplayName(group.Body, folderDir);
            if (display is null)
                continue;
            foreach (var label in group.Labels)
                displayByEnumValue[label.Split("::")[1]] = display;
        }

        var classNames = classByEnumValue.Values.Distinct().ToList();
        var closureByClass = classNames.ToDictionary(c => c, c => ResolveClassFileClosure(folderDir, c));
        var headerByClass = classNames.ToDictionary(c => c, c => FindClassHeader(folderDir, c));

        var fileOwnerCounts = closureByClass.Values
            .SelectMany(files => files.Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var components = new List<ParsedComponent>();
        foreach (var enumValue in enumValues)
        {
            if (IsSentinelValue(enumValue))
                continue;
            if (!classByEnumValue.TryGetValue(enumValue, out var className))
                continue;

            var header = headerByClass[className];
            var exclusiveFiles = closureByClass[className]
                .Where(f => fileOwnerCounts.TryGetValue(f, out var count) && count == 1)
                // The registration file itself (the enum/factory-switch .cpp/.h) is shared
                // infrastructure for the whole family, never a single driver's exclusive file — even
                // if only one driver's #include chain happens to reach it (e.g. for the enum type).
                .Where(f => !PathEquals(f, registrationCppPath) && !PathEquals(f, registrationHPath))
                .ToList();

            var displayName = displayByEnumValue.GetValueOrDefault(enumValue) ?? Humanize(enumValue);

            components.Add(new ParsedComponent(
                enumValue,
                className,
                displayName,
                header is not null ? Path.GetFileName(header) : "",
                exclusiveFiles));
        }

        return new ParseResult(enumTypeName, components);
    }

    private static bool PathEquals(string a, string? b) =>
        b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string Humanize(string identifier) =>
        Regex.Replace(identifier, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])(?![a-zA-Z]*$)", " ");

    private static string SafeReadAllText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }
}
