using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BE_Cruncher.Services;

/// <summary>
/// Deterministically trims a registration/factory file (e.g. BATTERIES.cpp) down to a single
/// selected driver by removing #include lines and switch-case labels/blocks for excluded
/// components. Purely mechanical text editing — no AI. Only ever touches include directives and
/// case-label lines; every other line (comments, formatting, unrelated logic) is left byte-identical.
/// </summary>
public static class RegistrationEditor
{
    public static string Trim(string content, IReadOnlyList<string> excludedHeaderFileNames, IReadOnlyList<string> excludedEnumValues)
    {
        content = RemoveIncludeLines(content, excludedHeaderFileNames);
        content = RemoveExcludedCases(content, excludedEnumValues);
        return content;
    }

    private static string RemoveIncludeLines(string content, IReadOnlyList<string> excludedHeaderFileNames)
    {
        if (excludedHeaderFileNames.Count == 0)
            return content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where(line =>
        {
            var m = Regex.Match(line, "^\\s*#include\\s+\"([^\"]+)\"");
            if (!m.Success)
                return true;
            var includedFile = Path.GetFileName(m.Groups[1].Value);
            return !excludedHeaderFileNames.Contains(includedFile, StringComparer.OrdinalIgnoreCase);
        });

        return string.Join('\n', kept);
    }

    private static string RemoveExcludedCases(string content, IReadOnlyList<string> excludedEnumValues)
    {
        if (excludedEnumValues.Count == 0)
            return content;

        var switches = CppRegistrationParser.FindEnumSwitches(content);
        var linesToRemove = new SortedSet<int>();

        foreach (var sw in switches)
        {
            foreach (var group in sw.Groups)
            {
                var excludedCount = group.Labels.Count(l => excludedEnumValues.Contains(l, StringComparer.OrdinalIgnoreCase));
                if (excludedCount == 0)
                    continue;

                if (excludedCount == group.Labels.Count)
                {
                    // Every label in this fall-through group is excluded — the whole block (labels +
                    // body) is dead code once they're gone.
                    foreach (var lineIndex in group.LabelLineIndices)
                        linesToRemove.Add(lineIndex);
                    for (var i = group.BodyStartLine; i < group.BodyEndLineExclusive; i++)
                        linesToRemove.Add(i);
                }
                else
                {
                    // Some labels in the fall-through group survive (e.g. keeping TeslaModel3Y while
                    // excluding TeslaModelSX) — drop only the excluded label lines, keep the shared body.
                    for (var idx = 0; idx < group.Labels.Count; idx++)
                    {
                        if (excludedEnumValues.Contains(group.Labels[idx], StringComparer.OrdinalIgnoreCase))
                            linesToRemove.Add(group.LabelLineIndices[idx]);
                    }
                }
            }
        }

        if (linesToRemove.Count == 0)
            return content;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var kept = lines.Where((_, i) => !linesToRemove.Contains(i));
        return string.Join('\n', kept);
    }
}
