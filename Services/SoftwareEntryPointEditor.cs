using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BE_Cruncher.Services;

/// <summary>
/// Strips the actual call/reference sites for excluded optional modules out of every file that
/// unconditionally needs them — deleting a module's own files isn't enough on its own. Two distinct
/// classes of coupling exist in this codebase (found by reading the real source, see project memory):
/// (1) the entry point (setup()/loop()) calls every module's init/loop functions unconditionally,
/// gated only by a runtime flag, never a compile-time macro; (2) several core, always-compiled files
/// (the settings loader, and — for SD card specifically — the CAN and logging hot paths) directly read/
/// write a module's global state or call its functions as part of normal operation, independent of
/// whether that module is even enabled. Every match is wrapped in "#if 0 / #endif" rather than edited,
/// so nothing here can ever produce a malformed comment or an unbalanced brace — the preprocessor
/// discards the wrapped text (braces and all) before the compiler ever sees it.
/// </summary>
public static class SoftwareEntryPointEditor
{
    private enum MatchKind { Call, Assignment, Either }

    private sealed record SymbolRule(string Symbol, MatchKind Kind);
    private sealed record ModuleCallSiteRule(string ModuleId, SymbolRule[] Symbols, string? BlockGuardSymbol);
    private sealed record TargetFile(string FindMarkerRegex, ModuleCallSiteRule[] Rules, bool SearchWholeTree);

    private static SymbolRule Call(string s) => new(s, MatchKind.Call);
    private static SymbolRule Assign(string s) => new(s, MatchKind.Assignment);

    // The entry point (setup()/loop()) — found among top-level .cpp files by content, not filename.
    // "BlockGuardSymbol" is a runtime flag *declared in that same module's own header* which gates a
    // whole if-block — since the flag itself disappears along with the module's header, the entire
    // guarded block (not just its body) has to go, found via brace matching. Modules whose calls are
    // gated by a flag declared in a *different*, still-present module's header (e.g. espnow's calls,
    // gated by wifi.h's espnow_enabled) don't need a block guard — the flag survives, so an empty
    // if-body is all that's left, which is valid C++ on its own.
    private static readonly TargetFile EntryPoint = new(
        @"\bvoid\s+setup\s*\([\s\S]*\bvoid\s+loop\s*\(",
        [
            new("wifi", [Call("init_WiFi"), Call("wifi_monitor")], "wifi_enabled"),
            new("webserver", [Call("init_webserver"), Call("ota_monitor")], null),
            new("display", [Call("init_display"), Call("update_display")], null),
            new("espnow", [Call("init_espnow"), Call("update_espnow")], null),
            new("mqtt", [Call("mqtt_client_loop")], "mqtt_enabled"),
            new("sdcard", [Call("init_sdcard"), Call("write_log_to_sdcard"), Call("write_can_frame_to_sdcard"),
                            Call("init_logging_buffers"), Call("deinit_logging_buffers")], null),
        ],
        SearchWholeTree: false);

    // The settings loader (init_stored_settings()) reads every persisted setting into memory in one
    // unconditional block at boot, including each module's own globals — found by content since its
    // conventional path (communication/nvm/comm_nvm.cpp) isn't assumed stable across releases.
    private static readonly TargetFile SettingsLoader = new(
        @"\bvoid\s+init_stored_settings\s*\(",
        [
            new("mqtt", [Assign("mqtt_enabled"), Assign("mqtt_timeout_ms"), Assign("mqtt_publish_interval_ms"),
                          Assign("ha_autodiscovery_enabled"), Assign("mqtt_transmit_all_cellvoltages"),
                          Assign("mqtt_server"), Assign("mqtt_port"), Assign("mqtt_user"), Assign("mqtt_password")], null),
            new("webserver", [Assign("http_username"), Assign("http_password"), Assign("webserver_auth")], null),
            new("wifi", [Assign("ssid"), Assign("password"), Assign("wifiap_enabled"), Assign("wifi_channel"),
                          Assign("passwordAP"), Assign("espnow_enabled"), Assign("custom_hostname"),
                          Assign("static_IP_enabled"), Assign("static_local_IP"), Assign("static_gateway"),
                          Assign("static_subnet"), Assign("static_dns")], null),
        ],
        SearchWholeTree: false);

    // SD card logging is called directly from the core CAN receive/transmit path and the core logging
    // path, not just an optional module's own code — both unconditional, gated only by a datalayer flag
    // that's untouched here (the guarded block's *condition* isn't an sdcard.h symbol, only its body is).
    private static readonly TargetFile CanHotPath = new(
        @"\badd_can_frame_to_buffer\s*\(",
        [new("sdcard", [Call("add_can_frame_to_buffer")], null)],
        SearchWholeTree: false);

    private static readonly TargetFile LoggingHotPath = new(
        @"\badd_log_to_buffer\s*\(",
        [new("sdcard", [Call("add_log_to_buffer")], null)],
        SearchWholeTree: false);

    // The webserver's own settings page also calls straight into sdcard.cpp for a handful of admin
    // actions (pause/resume/delete logs). Only relevant when sdcard is excluded but webserver is kept —
    // if webserver itself is excluded this file is already gone, so the search below simply finds nothing.
    private static readonly TargetFile WebserverSdcardActions = new(
        @"\bpause_can_writing\s*\(",
        [new("sdcard", [Call("pause_can_writing"), Call("resume_can_writing"), Call("delete_can_log"),
                         Call("delete_log"), Call("pause_log_writing"), Call("resume_log_writing")], null)],
        SearchWholeTree: false);

    private static readonly TargetFile[] AllTargets = [EntryPoint, SettingsLoader, CanHotPath, LoggingHotPath, WebserverSdcardActions];

    public static IReadOnlySet<string> KnownModuleIds { get; } =
        AllTargets.SelectMany(t => t.Rules).Select(r => r.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applies every applicable target-file edit for the given excluded module set within the workspace's
    /// source tree. Silently skips a target whose file can't be found (e.g. because the whole file was
    /// already deleted by an unrelated module exclusion) — nothing to strip from a file that's already gone.
    /// </summary>
    public static void ApplyAll(string sourceRoot, IReadOnlySet<string> excludedModuleIds, List<string> warnings)
    {
        foreach (var target in AllTargets)
        {
            if (!target.Rules.Any(r => excludedModuleIds.Contains(r.ModuleId)))
                continue;

            var file = FindFileByMarker(sourceRoot, target.FindMarkerRegex);
            if (file is null)
                continue; // already gone, or this release's structure doesn't match — nothing to strip

            try
            {
                var content = File.ReadAllText(file);
                File.WriteAllText(file, Trim(content, target.Rules, excludedModuleIds));
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not strip excluded-module call sites from {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private static string? FindFileByMarker(string sourceRoot, string markerRegex)
    {
        var pattern = new Regex(markerRegex, RegexOptions.Singleline);
        return Directory.EnumerateFiles(sourceRoot, "*.cpp", SearchOption.AllDirectories)
            .FirstOrDefault(f => pattern.IsMatch(SafeReadAllText(f)));
    }

    private static string Trim(string content, ModuleCallSiteRule[] rules, IReadOnlySet<string> excludedModuleIds)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        var wraps = new List<(int Start, int EndInclusive)>();

        foreach (var rule in rules)
        {
            if (!excludedModuleIds.Contains(rule.ModuleId))
                continue;

            foreach (var symbol in rule.Symbols)
                wraps.AddRange(FindMatchingLines(lines, symbol));

            if (rule.BlockGuardSymbol is not null)
                wraps.AddRange(FindGuardedBlocks(lines, rule.BlockGuardSymbol));
        }

        // Apply from the end backwards so earlier line indices stay valid as we insert.
        foreach (var (start, endInclusive) in wraps.OrderByDescending(w => w.Start))
        {
            lines.Insert(endInclusive + 1, "#endif  // BE Cruncher: excluded optional module");
            lines.Insert(start, "#if 0  // BE Cruncher: excluded optional module");
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<(int Start, int EndInclusive)> FindMatchingLines(List<string> lines, SymbolRule symbolRule)
    {
        var escaped = Regex.Escape(symbolRule.Symbol);
        var pattern = symbolRule.Kind switch
        {
            MatchKind.Call => new Regex($@"\b{escaped}\s*\("),
            MatchKind.Assignment => new Regex($@"\b{escaped}\s*=[^=]"),
            _ => new Regex($@"\b{escaped}\b"),
        };
        for (var i = 0; i < lines.Count; i++)
            if (pattern.IsMatch(lines[i]))
                yield return (i, i);
    }

    private static IEnumerable<(int Start, int EndInclusive)> FindGuardedBlocks(List<string> lines, string guardSymbol)
    {
        var ifPattern = new Regex($@"^\s*if\s*\(\s*{Regex.Escape(guardSymbol)}\s*\)\s*\{{");
        for (var i = 0; i < lines.Count; i++)
        {
            if (!ifPattern.IsMatch(lines[i]))
                continue;

            var depth = CountChar(lines[i], '{') - CountChar(lines[i], '}');
            var end = i;
            while (depth > 0 && ++end < lines.Count)
                depth += CountChar(lines[end], '{') - CountChar(lines[end], '}');
            if (depth != 0)
                continue; // unbalanced — leave untouched rather than risk a broken file

            yield return (i, end);
        }
    }

    private static int CountChar(string s, char c) => s.Count(ch => ch == c);

    private static string SafeReadAllText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return ""; }
    }
}
