using System.Text.Json.Serialization;

namespace BE_Cruncher.Services;

internal sealed class GitHubReleaseApiDto
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("zipball_url")]
    public string ZipballUrl { get; set; } = "";

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = "";
}

internal sealed class GitHubRepoApiDto
{
    [JsonPropertyName("default_branch")]
    public string DefaultBranch { get; set; } = "main";
}
