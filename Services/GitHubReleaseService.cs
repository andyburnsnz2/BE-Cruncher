using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

public sealed class GitHubReleaseService
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly AppConfig _config;

    public GitHubReleaseService(HttpClient http, AppPaths paths, AppConfig config)
    {
        _http = http;
        _paths = paths;
        _config = config;
    }

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && TryLoadCache(out var cached))
            return cached;

        var releases = await FetchFromGitHubAsync(ct);
        SaveCache(releases);
        return releases;
    }

    private bool TryLoadCache(out List<GitHubRelease> releases)
    {
        releases = [];
        if (!File.Exists(_paths.ReleaseCacheFile))
            return false;

        try
        {
            var cache = JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(_paths.ReleaseCacheFile), CacheJsonOptions);
            if (cache is null)
                return false;
            if (DateTimeOffset.UtcNow - cache.FetchedAt > TimeSpan.FromMinutes(_config.ReleaseCacheMinutes))
                return false;

            releases = cache.Releases;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void SaveCache(IReadOnlyList<GitHubRelease> releases)
    {
        var cache = new ReleaseCache(DateTimeOffset.UtcNow, releases.ToList());
        File.WriteAllText(_paths.ReleaseCacheFile, JsonSerializer.Serialize(cache, CacheJsonOptions));
    }

    private async Task<List<GitHubRelease>> FetchFromGitHubAsync(CancellationToken ct)
    {
        var releasesUrl = $"https://api.github.com/repos/{_config.GitHubOwner}/{_config.GitHubRepo}/releases?per_page=100";
        var releaseDtos = await GetJsonAsync<List<GitHubReleaseApiDto>>(releasesUrl, ct) ?? [];

        var results = releaseDtos
            .Where(r => !r.Draft)
            .Select(r => new GitHubRelease
            {
                TagName = r.TagName,
                Name = string.IsNullOrWhiteSpace(r.Name) ? r.TagName : r.Name,
                PublishedAt = r.PublishedAt,
                Prerelease = r.Prerelease,
                Draft = r.Draft,
                ZipUrl = r.ZipballUrl,
                HtmlUrl = r.HtmlUrl,
                IsMainDevelopment = false
            })
            .ToList();

        var repoUrl = $"https://api.github.com/repos/{_config.GitHubOwner}/{_config.GitHubRepo}";
        var repoDto = await GetJsonAsync<GitHubRepoApiDto>(repoUrl, ct);
        var defaultBranch = repoDto?.DefaultBranch ?? "main";

        results.Insert(0, new GitHubRelease
        {
            TagName = defaultBranch,
            Name = "Main Development",
            PublishedAt = null,
            Prerelease = false,
            Draft = false,
            ZipUrl = $"https://github.com/{_config.GitHubOwner}/{_config.GitHubRepo}/archive/refs/heads/{defaultBranch}.zip",
            HtmlUrl = $"https://github.com/{_config.GitHubOwner}/{_config.GitHubRepo}/tree/{defaultBranch}",
            IsMainDevelopment = true
        });

        return results;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BE-Cruncher", "1.0"));
        if (!string.IsNullOrWhiteSpace(_config.GitHubToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.GitHubToken);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
    }

    private sealed record ReleaseCache(DateTimeOffset FetchedAt, List<GitHubRelease> Releases);
}
