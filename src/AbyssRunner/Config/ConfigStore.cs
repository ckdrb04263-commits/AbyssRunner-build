using System.Text.Json;

namespace AbyssRunner.Config;

public sealed class LoadedConfig
{
    public required AppConfig App { get; init; }
    public required TargetsFile Targets { get; init; }
    public required string BaseDirectory { get; init; }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new IntRectJsonConverter() }
    };

    public static LoadedConfig LoadOrCreate(string baseDir)
    {
        var scenarioPath = Path.Combine(baseDir, "scenario.json");
        var targetsPath = Path.Combine(baseDir, "targets.json");
        if (!File.Exists(scenarioPath) || !File.Exists(targetsPath))
            throw new FileNotFoundException("scenario.json 또는 targets.json이 실행 파일 옆에 없습니다.");

        var app = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(scenarioPath), JsonOptions)
                  ?? throw new InvalidDataException("scenario.json 파싱 실패");
        var targets = JsonSerializer.Deserialize<TargetsFile>(File.ReadAllText(targetsPath), JsonOptions)
                      ?? throw new InvalidDataException("targets.json 파싱 실패");

        return new LoadedConfig { App = app, Targets = targets, BaseDirectory = baseDir };
    }

    public static void SaveScenario(LoadedConfig config)
    {
        var path = Path.Combine(config.BaseDirectory, "scenario.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config.App, JsonOptions));
    }

    public static void SaveTargets(LoadedConfig config)
    {
        var path = Path.Combine(config.BaseDirectory, "targets.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config.Targets, JsonOptions));
    }
}
