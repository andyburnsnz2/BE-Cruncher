using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

public sealed class RepositoryService
{
    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;

    public RepositoryService(HttpClient http, AppPaths paths, AppConfig config)
    {
        _http = http;
        _paths = paths;
        _config = config;
    }

    public bool IsDownloaded(GitHubRelease release)
    {
        var dir = _paths.OriginalSourceDir(release.TagName);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    public async Task<string> EnsureDownloadedAsync(GitHubRelease release, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var originalSourceDir = _paths.OriginalSourceDir(release.TagName);
        if (IsDownloaded(release))
        {
            progress?.Report("Already downloaded.");
            return originalSourceDir;
        }

        Directory.CreateDirectory(_paths.RepositoryDir(release.TagName));

        progress?.Report("Downloading source archive...");
        var zipPath = Path.Combine(_paths.RepositoryDir(release.TagName), "source.zip");
        await DownloadZipAsync(release.ZipUrl, zipPath, ct);

        progress?.Report("Extracting...");
        ExtractFlattened(zipPath, originalSourceDir);
        File.Delete(zipPath);

        progress?.Report("Done.");
        return originalSourceDir;
    }

    private async Task DownloadZipAsync(string url, string destination, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BE-Cruncher", "1.0"));
        if (!string.IsNullOrWhiteSpace(_config.GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.GitHubToken);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fileStream = File.Create(destination);
        await response.Content.CopyToAsync(fileStream, ct);
    }

    private static void ExtractFlattened(string zipPath, string destinationDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

        // GitHub zipballs wrap everything in a single top-level "<owner>-<repo>-<sha>" folder; strip it.
        var topLevelPrefix = entries
            .Select(e => e.FullName.Split('/')[0])
            .Distinct()
            .Count() == 1
            ? entries[0].FullName.Split('/')[0] + "/"
            : "";

        Directory.CreateDirectory(destinationDir);

        foreach (var entry in entries)
        {
            var relativePath = entry.FullName.StartsWith(topLevelPrefix, StringComparison.Ordinal)
                ? entry.FullName[topLevelPrefix.Length..]
                : entry.FullName;

            if (string.IsNullOrEmpty(relativePath))
                continue;

            var destPath = Path.Combine(destinationDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
