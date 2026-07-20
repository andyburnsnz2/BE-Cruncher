namespace BE_Cruncher.Models;

public sealed class BuildConfig
{
    public required string Version { get; init; }
    public required string BoardId { get; init; }
    public required string BatteryId { get; init; }
    public required string InverterId { get; init; }
    public List<string> OptionalModuleIds { get; init; } = [];

    // Opt-in: also delete the excluded module's own controls from the device's web configuration page
    // (see WebUiSectionEditor). Off by default — unlike every other trim in this app, this edit is not
    // verified by the compiler, only by whether the file still compiles at all.
    public bool StripWebUiSections { get; init; }
}
