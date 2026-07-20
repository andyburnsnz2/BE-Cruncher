using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BE_Cruncher.Services;

/// <summary>
/// Optionally removes a module's own settings controls from the device's own web configuration page
/// (settings_html.cpp) when that module is excluded from the build — otherwise the "Enable MQTT"-style
/// checkbox still renders and still saves a setting that the firmware silently ignores at runtime.
/// This is opt-in (see BuildConfig.StripWebUiSections) and deliberately scoped to only the modules whose
/// controls are cleanly self-contained: MQTT and ESP-NOW (one clearly-labelled section each) and SD card
/// (two independent checkboxes). WiFi's settings are woven into core network config (SSID, static IP,
/// AP password) that the device genuinely needs to be reachable at all, so it's not offered here — and
/// webserver needs no entry, since excluding it deletes this whole file anyway.
///
/// Unlike SoftwareEntryPointEditor, this edits inside a C++ raw string literal (R"rawliteral(...)"), so
/// "#if 0" is not an option — the preprocessor never looks inside a raw string literal for directives; a
/// "#if 0" placed there would just print those five characters on the actual web page. Matched lines are
/// deleted outright instead. The compiler can still catch a mistake that breaks C++ syntax (e.g. an
/// unbalanced raw-string delimiter), but it cannot verify the resulting HTML renders correctly — every
/// removal here is bounded by content markers found via HTML tag balance-counting (mirroring the brace
/// counting already used elsewhere in this codebase), and any boundary it can't confidently resolve is
/// skipped entirely rather than guessed at.
/// </summary>
public static class WebUiSectionEditor
{
    public static IReadOnlySet<string> KnownModuleIds { get; } = new HashSet<string>(
        ["mqtt", "espnow", "sdcard"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the settings page's .cpp by content (the file implementing settings_processor()) and
    /// removes the controls for any of the given excluded module IDs it has a rule for. Silently does
    /// nothing if the file can't be found (e.g. webserver itself was already excluded, taking this file
    /// with it) or if a module's expected markers aren't present (upstream HTML changed) — never guesses.
    /// </summary>
    public static void Apply(string sourceRoot, IReadOnlySet<string> excludedModuleIds, List<string> warnings)
    {
        var relevant = excludedModuleIds.Where(KnownModuleIds.Contains).ToList();
        if (relevant.Count == 0)
            return;

        var file = Directory.EnumerateFiles(sourceRoot, "*.cpp", SearchOption.AllDirectories)
            .FirstOrDefault(f => Regex.IsMatch(SafeReadAllText(f), @"\bsettings_processor\s*\("));
        if (file is null)
            return; // webserver (and this file with it) was already excluded — nothing to strip

        try
        {
            var lines = SafeReadAllText(file).Replace("\r\n", "\n").Split('\n').ToList();
            var removals = new List<(int Start, int End)>();

            if (relevant.Contains("mqtt", StringComparer.OrdinalIgnoreCase))
            {
                var range = FindMqttSection(lines);
                if (range is not null) removals.Add(range.Value);
                else warnings.Add("Could not confidently locate the MQTT section on the device's settings page; its controls were left in place (they'll do nothing, but were not removed).");
            }
            if (relevant.Contains("espnow", StringComparer.OrdinalIgnoreCase))
            {
                var range = FindLabelledCheckbox(lines, "ESPNOWENABLED");
                if (range is not null) removals.Add(range.Value);
                else warnings.Add("Could not confidently locate the ESP-NOW control on the device's settings page; it was left in place.");
            }
            if (relevant.Contains("sdcard", StringComparer.OrdinalIgnoreCase))
            {
                foreach (var name in new[] { "SDLOGENABLED", "CANLOGSD" })
                {
                    var range = FindLabelledCheckbox(lines, name);
                    if (range is not null) removals.Add(range.Value);
                    else warnings.Add($"Could not confidently locate the SD card '{name}' control on the device's settings page; it was left in place.");
                }
            }

            if (removals.Count == 0)
                return;

            foreach (var (start, end) in removals.OrderByDescending(r => r.Start))
                lines.RemoveRange(start, end - start + 1);

            File.WriteAllText(file, string.Join('\n', lines));
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not edit the device settings page ({ex.Message}); excluded modules' controls remain visible there but do nothing.");
        }
    }

    /// <summary>
    /// "Enable MQTT" checkbox plus the whole "if-mqtt" div of dependent sub-fields (server/port/user/
    /// password/timeout/publish-interval/cell-voltages/remote-reset/HA-discovery) that follows it.
    /// </summary>
    private static (int Start, int End)? FindMqttSection(List<string> lines)
    {
        var toggle = FindLabelledCheckbox(lines, "MQTTENABLED");
        if (toggle is null)
            return null;

        for (var i = toggle.Value.End + 1; i < lines.Count && i < toggle.Value.End + 5; i++)
        {
            if (!Regex.IsMatch(lines[i], @"<div\s+class=['""]if-mqtt['""]"))
                continue;

            var depth = CountTag(lines[i], "<div") - CountTag(lines[i], "</div>");
            var end = i;
            while (depth > 0 && ++end < lines.Count)
                depth += CountTag(lines[end], "<div") - CountTag(lines[end], "</div>");
            if (depth != 0)
                return null; // unbalanced — don't guess

            return (toggle.Value.Start, end);
        }

        return toggle; // no dependent div found right after — just the toggle itself
    }

    /// <summary>
    /// A single `&lt;label&gt;...&lt;/label&gt;` immediately followed by a self-closed
    /// `&lt;input ... name='NAME' .../&gt;` — the input's closing `/&gt;` may wrap onto a following line
    /// (attributes like title="..." on their own line), but bails out with no match — rather than risk
    /// cutting the wrong thing — if another `&lt;input` or `&lt;label&gt;` appears before that close is found.
    /// </summary>
    private static (int Start, int End)? FindLabelledCheckbox(List<string> lines, string inputNameAttr)
    {
        var inputPattern = new Regex($@"name=['""]{Regex.Escape(inputNameAttr)}['""]");
        var inputStart = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (inputPattern.IsMatch(lines[i]))
            {
                inputStart = i;
                break;
            }
        }
        if (inputStart < 0)
            return null;

        var inputEnd = -1;
        for (var i = inputStart; i < lines.Count && i < inputStart + 5; i++)
        {
            if (i > inputStart && (lines[i].Contains("<input") || lines[i].Contains("<label>")))
                return null; // another field started before this one closed — ambiguous, don't guess

            if (lines[i].TrimEnd().EndsWith("/>"))
            {
                inputEnd = i;
                break;
            }
        }
        if (inputEnd < 0)
            return null;

        for (var i = inputStart - 1; i >= 0 && i >= inputStart - 5; i--)
        {
            if (lines[i].Contains("<label>"))
                return (i, inputEnd);
        }

        return null;
    }

    private static int CountTag(string line, string tag) =>
        Regex.Matches(line, Regex.Escape(tag)).Count;

    private static string SafeReadAllText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }
}
