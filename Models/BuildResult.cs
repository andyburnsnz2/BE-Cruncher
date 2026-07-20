namespace BE_Cruncher.Models;

public sealed class BuildResult
{
    public required bool Success { get; init; }
    public required int ExitCode { get; init; }
    public required int WarningCount { get; init; }
    public required int ErrorCount { get; init; }
    public long? RamUsedBytes { get; init; }
    public long? RamTotalBytes { get; init; }
    public long? FlashUsedBytes { get; init; }
    public long? FlashTotalBytes { get; init; }
    public string? FirmwareBinPath { get; init; }
    public required string LogFilePath { get; init; }
    public required TimeSpan Duration { get; init; }
}
