namespace BE_Cruncher.Models;

public sealed class BuildManifest
{
    public required BuildConfig Config { get; init; }
    public required string PlatformIoEnvironment { get; init; }
    public required bool RegistrationTrimmed { get; init; }
    public required List<string> ExcludedBatteryFiles { get; init; }
    public required List<string> ExcludedInverterFiles { get; init; }
    public required List<string> ExcludedOptionalModuleFiles { get; init; }
    public required List<string> Warnings { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
