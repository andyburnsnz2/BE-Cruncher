using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace BE_Cruncher.Services;

/// <summary>
/// Detects and, with explicit user consent, installs PlatformIO Core — the compiler this whole app
/// depends on but does not bundle. Uses PlatformIO's own official installer script
/// (get-platformio.py), which requires Python to already be present; this app does not bundle Python
/// either, so if that's also missing, installation isn't attempted automatically.
/// </summary>
public sealed class PlatformIoInstaller
{
    private const string InstallerScriptUrl =
        "https://raw.githubusercontent.com/platformio/platformio-core-installer/master/get-platformio.py";

    private readonly HttpClient _http;

    public PlatformIoInstaller(HttpClient http) => _http = http;

    /// <summary>
    /// PlatformIO's own default install location (its "core_dir"), independent of PATH — using this
    /// directly means a build can proceed immediately after a fresh install, with no PATH refresh or
    /// app restart needed.
    /// </summary>
    public static string DefaultPioExecutablePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformio", "penv", "Scripts", "pio.exe");

    /// <summary>
    /// PlatformIO's entire self-contained install lives under this one folder — nothing it installs
    /// touches the registry, Add/Remove Programs, or anywhere else on the system (which is also why it
    /// never appears as an "installed app" — that's expected, not a sign of a broken install). A clean
    /// uninstall is therefore just deleting this folder; there is nothing else to clean up.
    /// </summary>
    public static string RootInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".platformio");

    public bool IsInstalled() => ResolvePioExecutable() is not null;

    /// <summary>
    /// Deletes PlatformIO's entire install folder. Safe and precise — this only ever targets
    /// PlatformIO's own dedicated home directory, never a PATH-resolved install that might live
    /// somewhere the user set up independently of this app.
    /// </summary>
    public void Uninstall()
    {
        if (Directory.Exists(RootInstallDir))
            Directory.Delete(RootInstallDir, recursive: true);
    }

    /// <summary>
    /// Resolves the pio executable to actually run: PATH first (respects an existing install, however
    /// it was set up), falling back to PlatformIO's own well-known install location. Falls back to
    /// "pio" (i.e. presumed on PATH) if neither actually responds — this only matters for the caller
    /// producing a sensible error message; IsInstalled()/ResolvePioExecutable() are what gate whether a
    /// build is even attempted.
    /// </summary>
    public static string ResolvePioCommand() => CanRun("pio") ? "pio" : DefaultPioExecutablePath;

    /// <summary>
    /// Unlike a bare file-existence check, this actually runs `--version` against the candidate and
    /// requires a clean exit — PlatformIO's installer can leave a `pio.exe` on disk backed by a broken
    /// or incomplete Python environment (observed in the wild: installed but missing a dependency like
    /// `click`, failing with a traceback on every invocation), which a plain File.Exists would wrongly
    /// treat as "installed".
    /// </summary>
    public string? ResolvePioExecutable()
    {
        if (CanRun("pio"))
            return "pio";
        return CanRun(DefaultPioExecutablePath) ? DefaultPioExecutablePath : null;
    }

    public bool IsPythonAvailable() => CanRun("python") || CanRun("py");

    /// <summary>
    /// Downloads PlatformIO's official installer script and runs it with Python, streaming output as
    /// progress. Never shows a console window. Returns true only if the resulting pio executable
    /// actually runs afterward — a script that exits 0 but leaves a broken environment is not success.
    /// </summary>
    public async Task<bool> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsPythonAvailable())
        {
            progress?.Report("Python was not found — PlatformIO's installer requires it. Install Python from https://python.org/downloads/ first, then try again.");
            return false;
        }

        var pythonCommand = CanRun("python") ? "python" : "py";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"get-platformio-{Guid.NewGuid():N}.py");

        try
        {
            progress?.Report("Downloading PlatformIO's official installer...");
            var script = await _http.GetStringAsync(InstallerScriptUrl, ct);
            await File.WriteAllTextAsync(scriptPath, script, ct);

            progress?.Report("Installing PlatformIO Core (this can take a few minutes)...");
            var psi = new ProcessStartInfo
            {
                FileName = pythonCommand,
                ArgumentList = { scriptPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) progress?.Report(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) progress?.Report(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                progress?.Report($"PlatformIO installer exited with code {process.ExitCode}.");
                return false;
            }

            if (ResolvePioExecutable() is null)
            {
                progress?.Report(
                    "The installer reported success, but the resulting PlatformIO install doesn't actually run. " +
                    $"Try deleting the folder at {Path.GetDirectoryName(Path.GetDirectoryName(DefaultPioExecutablePath))} " +
                    "(a leftover broken install) and running the install again.");
                return false;
            }

            progress?.Report("PlatformIO Core installed successfully.");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"PlatformIO installation failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch (IOException) { }
        }
    }

    private static bool CanRun(string fileNameOrPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileNameOrPath,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(8000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }
}
