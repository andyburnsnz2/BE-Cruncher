namespace BE_Cruncher.Models;

public sealed class BoardInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string BuildFlag { get; init; }
    public required string PlatformIoEnv { get; init; }
}

public sealed class ComponentInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string EnumValue { get; init; }
    public required List<string> SourceFiles { get; init; }
    public required string RegistrationNotes { get; init; }
}

public sealed class OptionalModuleInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required bool HasNativeBuildFlag { get; init; }
    public required string BuildFlag { get; init; }
    public required List<string> SourceFiles { get; init; }
    public required string Notes { get; init; }
}

public sealed class PlatformIoInfo
{
    public required string SourceDir { get; init; }
    public required string ConfigFile { get; init; }
    public required List<string> Environments { get; init; }
}

public sealed class AnalysisResult
{
    public required List<BoardInfo> Boards { get; init; }
    public required List<ComponentInfo> Batteries { get; init; }
    public required List<ComponentInfo> Inverters { get; init; }
    public required List<OptionalModuleInfo> OptionalModules { get; init; }
    public required List<string> ProtectedPaths { get; init; }
    public required PlatformIoInfo PlatformIo { get; init; }
    public required string Notes { get; init; }

    public string Version { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
}
