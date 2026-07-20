using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Establishes the untrimmed "standard" firmware for a given release + PlatformIO environment, so
/// custom builds can be compared against a real reference file instead of an estimate. Cheapest source
/// first: the project's own published web-installer images already contain a prebuilt, untrimmed OTA
/// binary for a handful of common boards — downloaded once per (version, environment) into a persistent
/// cache, then copied into every subsequent build's own Output folder alongside firmware.bin so the two
/// can be compared directly. Only when no matching published image exists does this fall back to
/// actually compiling a local baseline (which works for every board, published or not, but produces a
/// size figure only — no comparable file, since the throwaway compile workspace isn't kept around).
/// </summary>
public sealed class BaselineSizeService
{
    private const string WebInstallerOwner = "dalathegreat";
    private const string WebInstallerRepo = "BE-Web-Installer";
    private const string ReferenceBinFileName = "standard-firmware.bin";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Board hardware flags (from platformio.ini's -D HW_xxx) mapped to a normalized-name predicate
    // for the corresponding published image filename (e.g. "BE_v11.1.0_LilygoT-2CAN.ota.bin"). Ordered
    // most-specific first since some tokens are substrings of others (HW_LILYGO vs HW_LILYGO2CAN).
    private static readonly (string BuildFlag, Func<string, bool> Matches)[] BoardMatchRules =
    [
        ("HW_LILYGO2CAN", n => n.Contains("lilygo") && n.Contains("2can")),
        ("HW_LILYGO", n => n.Contains("lilygo") && !n.Contains("2can")),
        ("HW_STARK", n => n.Contains("stark")),
        ("HW_WAVESHARE", n => n.Contains("waveshare")),
        ("HW_BECOM", n => n.Contains("becom")),
    ];

    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly WorkspaceService _workspaceService;
    private readonly PlatformIoBuildService _buildService;
    private readonly HttpClient _http;

    public BaselineSizeService(
        AppPaths paths, AppConfig config, WorkspaceService workspaceService, PlatformIoBuildService buildService, HttpClient http)
    {
        _paths = paths;
        _config = config;
        _workspaceService = workspaceService;
        _buildService = buildService;
        _http = http;
    }

    public bool HasCachedBaseline(string tagName, string environment) =>
        File.Exists(_paths.BaselineFile(tagName, environment));

    public BaselineSize LoadCachedBaseline(string tagName, string environment) =>
        JsonSerializer.Deserialize<BaselineSize>(File.ReadAllText(_paths.BaselineFile(tagName, environment)), JsonOptions)
        ?? throw new InvalidOperationException("Cached baseline size file could not be parsed.");

    /// <summary>
    /// Gets (fetching/compiling if needed) the baseline size for this board, and — when the baseline
    /// came from the published web installer — copies the actual reference .bin into
    /// <paramref name="outputDirForReferenceBin"/> (the current build's own Output folder) so it sits
    /// right next to firmware.bin for a direct, tangible comparison.
    /// </summary>
    public async Task<BaselineSize> GetOrBuildBaselineAsync(
        string tagName,
        string originalSourceDir,
        string environment,
        string buildFlag,
        string outputDirForReferenceBin,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var baseline = HasCachedBaseline(tagName, environment)
            ? LoadCachedBaseline(tagName, environment)
            : await FetchOrCompileBaselineAsync(tagName, originalSourceDir, environment, buildFlag, progress, ct);

        var cachedBinPath = ReferenceBinCachePath(tagName, environment);

        // A cached JSON summary can predate this .bin-caching behavior (or the .bin could simply have
        // been cleaned up separately from the JSON) — re-fetch rather than silently serving a baseline
        // with no comparable file behind it.
        if (baseline.Source.StartsWith("web-installer:", StringComparison.Ordinal) && !File.Exists(cachedBinPath))
            baseline = await FetchOrCompileBaselineAsync(tagName, originalSourceDir, environment, buildFlag, progress, ct);
        if (baseline.Source.StartsWith("web-installer:", StringComparison.Ordinal) && File.Exists(cachedBinPath))
        {
            Directory.CreateDirectory(outputDirForReferenceBin);
            var copyPath = Path.Combine(outputDirForReferenceBin, ReferenceBinFileName);
            File.Copy(cachedBinPath, copyPath, overwrite: true);
            progress?.Report($"Copied the published standard firmware ({baseline.FlashUsedBytes:N0} bytes) to {copyPath} for direct comparison.");
            return baseline with { ReferenceBinPath = copyPath };
        }

        return baseline;
    }

    private async Task<BaselineSize> FetchOrCompileBaselineAsync(
        string tagName, string originalSourceDir, string environment, string buildFlag, IProgress<string>? progress, CancellationToken ct)
    {
        var published = await TryGetPublishedBaselineAsync(tagName, environment, buildFlag, progress, ct);
        if (published is not null)
            return Save(tagName, environment, published);

        progress?.Report($"No published baseline image found for {environment}; building a local standard (untrimmed) firmware to measure instead...");
        var workspace = _workspaceService.CreateWorkspace(originalSourceDir);
        var result = await _buildService.BuildAsync(workspace, environment, progress, ct);

        if (!result.Success || result.FlashUsedBytes is null || result.FlashTotalBytes is null)
            throw new InvalidOperationException(
                $"Could not establish a baseline: the standard build for '{environment}' failed. See {result.LogFilePath}.");

        var baseline = new BaselineSize
        {
            Environment = environment,
            RamUsedBytes = result.RamUsedBytes ?? 0,
            RamTotalBytes = result.RamTotalBytes ?? 0,
            FlashUsedBytes = result.FlashUsedBytes.Value,
            FlashTotalBytes = result.FlashTotalBytes.Value,
            Source = "local-compile",
        };

        return Save(tagName, environment, baseline);
    }

    /// <summary>
    /// Looks up a prebuilt, untrimmed OTA binary published for this exact release tag on the project's
    /// GitHub Pages web installer, downloads it once into a persistent per-(version,environment) cache,
    /// and reports its real size. Returns null (never throws) if the board isn't one of the handful
    /// published there, the version wasn't published, or the lookup fails for any reason — the caller
    /// falls back to a local compile, which works for every board.
    /// </summary>
    private async Task<BaselineSize?> TryGetPublishedBaselineAsync(
        string tagName, string environment, string buildFlag, IProgress<string>? progress, CancellationToken ct)
    {
        var rule = BoardMatchRules.FirstOrDefault(r => r.BuildFlag.Equals(buildFlag, StringComparison.OrdinalIgnoreCase));
        if (rule.Matches is null)
            return null;

        try
        {
            var url = $"https://api.github.com/repos/{WebInstallerOwner}/{WebInstallerRepo}/contents/images/{Uri.EscapeDataString(tagName)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BE-Cruncher", "1.0"));
            if (!string.IsNullOrWhiteSpace(_config.GitHubToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.GitHubToken);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var entries = await response.Content.ReadFromJsonAsync<List<GitHubContentEntryDto>>(cancellationToken: ct);
            if (entries is null)
                return null;

            // .ota.bin is the raw application image (matches what PlatformIO reports as "Flash used" for
            // our own builds); .factory.bin also bundles the bootloader/partition table/padding, which
            // would overstate the comparison, so only .ota.bin is an apples-to-apples baseline.
            var candidates = entries.Where(e => e.Name.EndsWith(".ota.bin", StringComparison.OrdinalIgnoreCase));
            var match = candidates.FirstOrDefault(e => rule.Matches(Normalize(e.Name)));
            if (match is null || string.IsNullOrEmpty(match.DownloadUrl))
                return null;

            progress?.Report($"Downloading published baseline image for {environment}: {match.Name}...");
            var bytes = await _http.GetByteArrayAsync(match.DownloadUrl, ct);

            var cachedBinPath = ReferenceBinCachePath(tagName, environment);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedBinPath)!);
            await File.WriteAllBytesAsync(cachedBinPath, bytes, ct);

            progress?.Report($"Downloaded {match.Name} ({bytes.Length:N0} bytes) — no local compile needed.");

            return new BaselineSize
            {
                Environment = environment,
                RamUsedBytes = 0,
                RamTotalBytes = 0,
                FlashUsedBytes = bytes.Length,
                FlashTotalBytes = 0,
                Source = $"web-installer:{match.Name}",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            progress?.Report($"Could not fetch the published baseline image ({ex.Message}); will build one locally instead.");
            return null;
        }
    }

    private static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private string ReferenceBinCachePath(string tagName, string environment) =>
        Path.ChangeExtension(_paths.BaselineFile(tagName, environment), ".bin");

    private BaselineSize Save(string tagName, string environment, BaselineSize baseline)
    {
        var baselineFile = _paths.BaselineFile(tagName, environment);
        Directory.CreateDirectory(Path.GetDirectoryName(baselineFile)!);
        File.WriteAllText(baselineFile, JsonSerializer.Serialize(baseline, JsonOptions));
        return baseline;
    }

    private sealed class GitHubContentEntryDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; init; }
    }
}
