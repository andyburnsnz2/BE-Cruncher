namespace BE_Cruncher.Models;

public sealed record BaselineSize
{
    public required string Environment { get; init; }
    public required long RamUsedBytes { get; init; }
    public required long RamTotalBytes { get; init; }
    public required long FlashUsedBytes { get; init; }
    public required long FlashTotalBytes { get; init; }
    public string Source { get; init; } = "local-compile";
    public string? ReferenceBinPath { get; init; }
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;
}
