namespace BE_Cruncher.Models;

public sealed class GitHubRelease
{
    public required string TagName { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public bool Prerelease { get; init; }
    public bool Draft { get; init; }
    public required string ZipUrl { get; init; }
    public required string HtmlUrl { get; init; }
    public bool IsMainDevelopment { get; init; }

    public string DisplayName => IsMainDevelopment ? "Main Development" : Name;
}
