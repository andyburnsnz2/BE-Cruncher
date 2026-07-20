namespace BE_Cruncher.Models;

public sealed class BuildReport
{
    public required BuildConfig Config { get; init; }
    public required string PlatformIoEnvironment { get; init; }
    public required bool Success { get; init; }
    public required int Attempts { get; init; }
    public required int WarningCount { get; init; }
    public required int ErrorCount { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public long? RamUsedBytes { get; init; }
    public long? RamTotalBytes { get; init; }
    public long? FlashUsedBytes { get; init; }
    public long? FlashTotalBytes { get; init; }
    public BaselineSize? Baseline { get; init; }
    public required List<string> Warnings { get; init; }
    public required List<string> RepairNotes { get; init; }
    public string? FirmwareBinPath { get; init; }
    public required string OutputDir { get; init; }
    public required string LogsDir { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public long? FlashBytesSaved => Baseline is not null && FlashUsedBytes is not null
        ? Baseline.FlashUsedBytes - FlashUsedBytes.Value
        : null;

    public double? FlashPercentReduction => Baseline is { FlashUsedBytes: > 0 } && FlashBytesSaved is not null
        ? FlashBytesSaved.Value / (double)Baseline.FlashUsedBytes * 100.0
        : null;
}
