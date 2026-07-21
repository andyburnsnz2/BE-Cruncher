using System.IO;
using System.Linq;
using System.Text.Json;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Applies a BuildConfig to a workspace's Source copy: trims registration/factory files down to the
/// selected battery and inverter (deterministic text editing — see RegistrationEditor) and deletes
/// the now-unreferenced driver files, so PlatformIO only compiles what was actually selected.
/// Degrades gracefully (keeps the full driver set for a component) if trimming can't be performed
/// for some reason, so the build stays compilable.
/// </summary>
public sealed class BuildGenerator
{
    private static readonly JsonSerializerOptions FileJsonOptions = new() { WriteIndented = true };

    public BuildManifest Generate(
        Workspace workspace,
        AnalysisResult analysis,
        BuildConfig config,
        IProgress<string>? progress = null)
    {
        var board = analysis.Boards.FirstOrDefault(b => b.Id == config.BoardId)
            ?? throw new InvalidOperationException($"Board '{config.BoardId}' not found in analysis.");
        var battery = analysis.Batteries.FirstOrDefault(b => b.Id == config.BatteryId)
            ?? throw new InvalidOperationException($"Battery '{config.BatteryId}' not found in analysis.");
        var inverter = analysis.Inverters.FirstOrDefault(i => i.Id == config.InverterId)
            ?? throw new InvalidOperationException($"Inverter '{config.InverterId}' not found in analysis.");

        var warnings = new List<string>();

        var batteryResult = TrimComponentFamily(workspace, "battery", @"^batter(y|ies)$", battery, analysis.Batteries, warnings, progress);
        var inverterResult = TrimComponentFamily(workspace, "inverter", @"^inverters?$", inverter, analysis.Inverters, warnings, progress);
        var excludedOptionalModuleFiles = TrimOptionalModules(workspace, analysis, config, warnings, progress);

        var manifest = new BuildManifest
        {
            Config = config,
            PlatformIoEnvironment = board.PlatformIoEnv,
            RegistrationTrimmed = batteryResult.Trimmed && inverterResult.Trimmed,
            ExcludedBatteryFiles = batteryResult.DeletedFiles,
            ExcludedInverterFiles = inverterResult.DeletedFiles,
            ExcludedOptionalModuleFiles = excludedOptionalModuleFiles,
            Warnings = warnings,
        };

        File.WriteAllText(
            Path.Combine(workspace.GeneratedDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, FileJsonOptions));

        return manifest;
    }

    /// <summary>
    /// Deletes the source files of any optional module the user did not keep selected, and strips the
    /// corresponding call sites out of the entry-point file (setup()/loop()) — deleting a module's files
    /// alone isn't enough, since that file calls every module's functions unconditionally regardless of
    /// its own runtime enable flag (see SoftwareEntryPointEditor). Config.OptionalModuleIds is the set of
    /// modules to keep; anything else in analysis.OptionalModules is excluded. Never deletes a file also
    /// listed in analysis.ProtectedPaths, regardless of which module(s) claim it — e.g. the "safety"
    /// module's files always overlap ProtectedPaths and are therefore never actually removable here,
    /// deliberately, no matter what the user selects.
    /// </summary>
    private static List<string> TrimOptionalModules(
        Workspace workspace, AnalysisResult analysis, BuildConfig config, List<string> warnings, IProgress<string>? progress)
    {
        var keptIds = new HashSet<string>(config.OptionalModuleIds, StringComparer.OrdinalIgnoreCase);
        var protectedFiles = new HashSet<string>(analysis.ProtectedPaths, StringComparer.OrdinalIgnoreCase);
        var excludedIds = analysis.OptionalModules.Select(m => m.Id).Where(id => !keptIds.Contains(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filesToDelete = analysis.OptionalModules
            .Where(m => excludedIds.Contains(m.Id))
            .SelectMany(m => m.SourceFiles)
            .Where(f => !protectedFiles.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deletedFiles = new List<string>();
        if (filesToDelete.Count > 0)
        {
            progress?.Report("Excluding deselected optional modules...");
            foreach (var relativeFile in filesToDelete)
            {
                var fullPath = Path.Combine(workspace.SourceDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                    continue;
                File.Delete(fullPath);
                deletedFiles.Add(relativeFile);
            }
        }

        var callSiteExcludedIds = excludedIds.Where(SoftwareEntryPointEditor.KnownModuleIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (callSiteExcludedIds.Count > 0)
        {
            progress?.Report("Stripping call sites for excluded optional modules from the core files that reference them...");
            SoftwareEntryPointEditor.ApplyAll(workspace.SourceDir, callSiteExcludedIds, warnings);
        }

        if (config.StripWebUiSections)
        {
            progress?.Report("Removing excluded modules' controls from the device's web configuration page...");
            WebUiSectionEditor.Apply(workspace.SourceDir, excludedIds, warnings);
        }

        return deletedFiles;
    }

    private static (bool Trimmed, List<string> DeletedFiles) TrimComponentFamily(
        Workspace workspace,
        string kind,
        string folderNamePattern,
        ComponentInfo selected,
        IReadOnlyList<ComponentInfo> allComponents,
        List<string> warnings,
        IProgress<string>? progress)
    {
        var excluded = allComponents.Where(c => c.Id != selected.Id).ToList();
        var deletedFiles = new List<string>();

        var folder = RepositoryStructureHelper.FindComponentFolder(workspace.SourceDir, folderNamePattern);
        if (folder is null)
        {
            warnings.Add($"Could not locate the {kind} folder in the workspace copy; no {kind} drivers were excluded.");
            return (false, deletedFiles);
        }

        var (cppPath, hPath) = RepositoryStructureHelper.FindRegistrationFilePair(folder);
        if (cppPath is null)
        {
            warnings.Add($"Could not identify the {kind} registration file; no {kind} drivers were excluded.");
            return (false, deletedFiles);
        }

        try
        {
            progress?.Report($"Trimming {kind} registration...");
            var cppContent = File.ReadAllText(cppPath);
            var parsed = CppRegistrationParser.Parse(folder, cppContent, cppPath, hPath);
            if (parsed is null)
            {
                warnings.Add($"Could not parse the {kind} registration switch; no {kind} drivers were excluded.");
                return (false, deletedFiles);
            }

            // A component's registration-file #include footprint isn't limited to its "primary" class
            // header — the registration .cpp often #includes companion helper headers directly too
            // (e.g. a driver's own CT-clamp or shunt helper file), each as its own top-level #include
            // line. Stripping only the primary header's #include line while deleting every exclusive
            // file leaves those companion #includes dangling, pointing at a file that no longer exists.
            // Every .h file this component actually owns exclusively (per CppRegistrationParser) must
            // have its #include line stripped, not just the primary one.
            List<string> HeadersFor(ComponentInfo c)
            {
                var shortEnumValue = c.EnumValue.Split("::")[^1];
                var component = parsed.Components.FirstOrDefault(p => p.EnumValue == shortEnumValue);
                if (component is null)
                    return [];
                return component.ExclusiveFiles
                    .Where(f => f.EndsWith(".h", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Cast<string>()
                    .ToList();
            }

            // Sibling components sometimes share underlying headers (e.g. Tesla Model 3/Y and Tesla
            // Model S/X both declared by TESLA-BATTERY.h) — never strip an #include the selected
            // component's own driver still needs.
            var selectedHeaders = new HashSet<string>(HeadersFor(selected), StringComparer.OrdinalIgnoreCase);
            var excludedHeaders = excluded
                .SelectMany(HeadersFor)
                .Where(h => !selectedHeaders.Contains(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var excludedEnumValues = excluded.Select(c => c.EnumValue).ToList();

            var trimmedCpp = RegistrationEditor.Trim(cppContent, excludedHeaders, excludedEnumValues);
            File.WriteAllText(cppPath, trimmedCpp);

            if (hPath is not null)
            {
                var hContent = File.ReadAllText(hPath);
                var trimmedH = RegistrationEditor.Trim(hContent, excludedHeaders, excludedEnumValues);
                File.WriteAllText(hPath, trimmedH);
            }

            // Sibling components sometimes share the same underlying driver files entirely (not just
            // the header) — never delete a file the selected component also depends on, no matter
            // which excluded component(s) also list it.
            var selectedFiles = new HashSet<string>(selected.SourceFiles, StringComparer.OrdinalIgnoreCase);
            var filesToDelete = excluded
                .SelectMany(c => c.SourceFiles)
                .Where(f => !selectedFiles.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var relativeFile in filesToDelete)
            {
                var fullPath = Path.Combine(workspace.SourceDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                    continue;
                File.Delete(fullPath);
                deletedFiles.Add(relativeFile);
            }

            return (true, deletedFiles);
        }
        catch (Exception ex)
        {
            warnings.Add($"{kind} registration was not trimmed ({ex.Message}); the full {kind} driver set remains compiled in.");
            return (false, deletedFiles);
        }
    }
}
