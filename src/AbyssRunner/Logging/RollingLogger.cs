using System.Text.Json;
using AbyssRunner.Config;
using AbyssRunner.Core;

namespace AbyssRunner.Logging;

public sealed class RollingLogger
{
    private readonly string _dir;
    private readonly LoggingConfig _config;
    private readonly object _sync = new();
    private string _path;

    public event Action<string>? LineWritten;

    public RollingLogger(string dir, LoggingConfig config)
    {
        _dir = dir;
        _config = config;
        Directory.CreateDirectory(_dir);
        _path = NewPath();
        Cleanup();
    }

    public void Write(RunStage stage, string eventName, object? details = null)
    {
        lock (_sync)
        {
            RollIfNeeded();
            var item = new
            {
                time = DateTimeOffset.Now.ToString("O"),
                stage = stage.ToString(),
                @event = eventName,
                details
            };
            var line = JsonSerializer.Serialize(item);
            File.AppendAllText(_path, line + Environment.NewLine);
            LineWritten?.Invoke($"[{DateTime.Now:HH:mm:ss}] {stage} / {eventName} {Compact(details)}");
        }
    }

    private static string Compact(object? o)
    {
        if (o is null) return "";
        var s = JsonSerializer.Serialize(o);
        return s.Length <= 260 ? s : s[..260] + "…";
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(_path)) return;
        var max = Math.Max(1, _config.MaxFileMb) * 1024L * 1024L;
        if (new FileInfo(_path).Length < max) return;
        _path = NewPath();
        Cleanup();
    }

    private string NewPath() => Path.Combine(_dir, $"abyss-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");

    private void Cleanup()
    {
        var files = new DirectoryInfo(_dir).GetFiles("abyss-*.jsonl").OrderByDescending(x => x.CreationTimeUtc).ToList();
        foreach (var old in files.Skip(Math.Max(1, _config.MaxFiles)))
        {
            try { old.Delete(); } catch { }
        }
    }
}
