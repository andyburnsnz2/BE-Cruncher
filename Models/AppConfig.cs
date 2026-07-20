namespace BE_Cruncher.Models;

public sealed class AppConfig
{
    public string GitHubOwner { get; set; } = "dalathegreat";
    public string GitHubRepo { get; set; } = "Battery-Emulator";
    public string? GitHubToken { get; set; }
    public int ReleaseCacheMinutes { get; set; } = 60;

    // Null/empty = use the default location under the fixed root (%LOCALAPPDATA%\BE Cruncher).
    // Config.json's own location is never configurable here — it has to live somewhere fixed so it
    // can be found and loaded before any of these overrides are known.
    public string? RepositoriesDir { get; set; }
    public string? WorkspacesDir { get; set; }
    public string? OutputDir { get; set; }
    public string? CacheDir { get; set; }
}
