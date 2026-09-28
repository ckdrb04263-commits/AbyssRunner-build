using AbyssRunner.Config;
using AbyssRunner.Core;

namespace AbyssRunner.Logging;

public sealed class DiagnosticStore
{
    private readonly string _dir;
    private readonly int _maxFiles;

    public DiagnosticStore(string dir, LoggingConfig config)
    {
        _dir = dir;
        _maxFiles = Math.Max(10, config.MaxDiagnostics);
        Directory.CreateDirectory(_dir);
    }

    public string Save(Bitmap screenshot, RunStage stage, string reason)
    {
        var safe = string.Concat(reason.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (safe.Length > 50) safe = safe[..50];
        var path = Path.Combine(_dir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}_{stage}_{safe}.png");
        screenshot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Cleanup();
        return path;
    }

    private void Cleanup()
    {
        var files = new DirectoryInfo(_dir).GetFiles("*.png").OrderByDescending(x => x.CreationTimeUtc).ToList();
        foreach (var f in files.Skip(_maxFiles))
        {
            try { f.Delete(); } catch { }
        }
    }
}
