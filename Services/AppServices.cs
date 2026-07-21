using System.Net.Http;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

/// <summary>
/// Composition root: constructs every service once and hands the shared instances to whichever
/// page needs them. Not a DI container — just a single place that wires dependencies together.
/// </summary>
public sealed class AppServices
{
    public AppPaths Paths { get; }
    public AppConfig Config { get; }
    public GitHubReleaseService ReleaseService { get; }
    public RepositoryService RepositoryService { get; }
    public RepositoryAnalyzer AnalysisService { get; }
    public WorkspaceService WorkspaceService { get; }
    public BuildGenerator BuildGenerator { get; }
    public PlatformIoBuildService PlatformIoBuildService { get; }
    public PlatformIoInstaller PlatformIoInstaller { get; }
    public BaselineSizeService BaselineSizeService { get; }
    public BuildOrchestrator BuildOrchestrator { get; }

    public AppServices()
    {
        // Config.json's own location is fixed (has to be, to bootstrap) — load it via a plain default
        // AppPaths first, then build the real Paths, applying any directory overrides it specifies.
        var bootstrapPaths = new AppPaths();
        bootstrapPaths.EnsureDirectories();
        Config = new ConfigService(bootstrapPaths).Load();

        Paths = new AppPaths(Config);
        Paths.EnsureDirectories();

        var httpClient = new HttpClient();
        ReleaseService = new GitHubReleaseService(httpClient, Paths, Config);
        RepositoryService = new RepositoryService(httpClient, Paths, Config);
        AnalysisService = new RepositoryAnalyzer(Paths);
        WorkspaceService = new WorkspaceService(Paths);
        BuildGenerator = new BuildGenerator();
        PlatformIoBuildService = new PlatformIoBuildService();
        PlatformIoInstaller = new PlatformIoInstaller(httpClient);
        BaselineSizeService = new BaselineSizeService(Paths, Config, WorkspaceService, PlatformIoBuildService, httpClient);
        BuildOrchestrator = new BuildOrchestrator(WorkspaceService, BuildGenerator, PlatformIoBuildService, BaselineSizeService);
    }
}
