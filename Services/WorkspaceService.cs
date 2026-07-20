using System.IO;

namespace BE_Cruncher.Services;

public sealed record Workspace(string RootDir, string SourceDir, string GeneratedDir, string OutputDir, string LogsDir);

/// <summary>
/// Creates disposable build workspaces. The original downloaded release copy is never touched —
/// every build works from a fresh copy under Workspaces/.
/// </summary>
public sealed class WorkspaceService
{
    private readonly AppPaths _paths;

    public WorkspaceService(AppPaths paths) => _paths = paths;

    public Workspace CreateWorkspace(string originalSourceDir)
    {
        var name = $"Build-{DateTime.Now:yyyyMMdd-HHmmss}";
        var root = Path.Combine(_paths.WorkspacesDir, name);
        var workspace = new Workspace(
            root,
            Path.Combine(root, "Source"),
            Path.Combine(root, "Generated"),
            Path.Combine(root, "Output"),
            Path.Combine(root, "Logs"));

        Directory.CreateDirectory(workspace.SourceDir);
        Directory.CreateDirectory(workspace.GeneratedDir);
        Directory.CreateDirectory(workspace.OutputDir);
        Directory.CreateDirectory(workspace.LogsDir);

        CopyDirectory(originalSourceDir, workspace.SourceDir);

        return workspace;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dirPath.Replace(sourceDir, destDir));

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            File.Copy(filePath, filePath.Replace(sourceDir, destDir), overwrite: true);
    }
}
