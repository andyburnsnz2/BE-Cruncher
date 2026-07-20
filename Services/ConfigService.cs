using System.IO;
using System.Text.Json;
using BE_Cruncher.Models;

namespace BE_Cruncher.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;

    public ConfigService(AppPaths paths) => _paths = paths;

    public AppConfig Load()
    {
        if (!File.Exists(_paths.ConfigFile))
        {
            var defaultConfig = new AppConfig();
            Save(defaultConfig);
            return defaultConfig;
        }

        var json = File.ReadAllText(_paths.ConfigFile);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_paths.ConfigFile, json);
    }
}
