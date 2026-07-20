using System.IO;
using System.Linq;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

public sealed class AppPaths
{
    public string RootDir { get; }
    public string ConfigFile { get; }
    public string CacheDir { get; }
    public string RepositoriesDir { get; }
    public string WorkspacesDir { get; }

    /// <summary>
    /// RootDir and ConfigFile are always fixed under the default location — Config.json has to live
    /// somewhere findable before any of its own directory overrides can be known. Passing a loaded
    /// <paramref name="config"/> applies its Repositories/Workspaces/Cache directory overrides (each
    /// falls back to the default under RootDir when unset).
    /// </summary>
    public AppPaths(AppConfig? config = null, string? rootOverride = null)
    {
        RootDir = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BE Cruncher");
        ConfigFile = Path.Combine(RootDir, "Config.json");
        CacheDir = FirstNonEmpty(config?.CacheDir, Path.Combine(RootDir, "Cache"));
        RepositoriesDir = FirstNonEmpty(config?.RepositoriesDir, Path.Combine(RootDir, "Repositories"));
        WorkspacesDir = FirstNonEmpty(config?.WorkspacesDir, Path.Combine(RootDir, "Workspaces"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(RepositoriesDir);
        Directory.CreateDirectory(WorkspacesDir);
    }

    public string ReleaseCacheFile => Path.Combine(CacheDir, "releases.json");

    public string RepositoryDir(string tagName) => Path.Combine(RepositoriesDir, SanitizeFolderName(tagName));

    public string OriginalSourceDir(string tagName) => Path.Combine(RepositoryDir(tagName), "Original Source");

    public string AnalysisFile(string tagName) => Path.Combine(RepositoryDir(tagName), "analysis.json");

    public string BaselineFile(string tagName, string platformIoEnvironment) =>
        Path.Combine(RepositoryDir(tagName), "baselines", SanitizeFolderName(platformIoEnvironment) + ".json");

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string FirstNonEmpty(string? overrideValue, string fallback) =>
        string.IsNullOrWhiteSpace(overrideValue) ? fallback : overrideValue;
}
