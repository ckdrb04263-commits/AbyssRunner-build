using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AbyssRunner.Vision;

public sealed record OcrLineHit(string Text, Rectangle Bounds);
public sealed record OcrReadResult(string ActualText, IReadOnlyList<OcrLineHit> Lines, OcrLineHit? MatchedLine, string? MatchedPhrase, bool UsedUpscale);

public sealed class KoreanOcr
{
    private readonly OcrEngine _engine;

    public KoreanOcr()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("ko-KR"))
                  ?? throw new InvalidOperationException("Windows 한국어 OCR 엔진을 만들 수 없습니다. Windows 한국어 언어 기능을 설치하세요.");
    }

    public async Task<OcrReadResult> FindAsync(Bitmap source, Rectangle region, IReadOnlyList<string> phrases, string mode, CancellationToken ct)
    {
        var first = await ReadInternalAsync(source, region, 1.0, ct).ConfigureAwait(false);
        var hit = Match(first.Lines, phrases, mode);
        if (hit.line is not null)
            return new(first.ActualText, first.Lines, hit.line, hit.phrase, false);

        var second = await ReadInternalAsync(source, region, 2.0, ct).ConfigureAwait(false);
        hit = Match(second.Lines, phrases, mode);
        return new(second.ActualText, second.Lines, hit.line, hit.phrase, true);
    }

    public async Task<OcrReadResult> ReadAsync(Bitmap source, Rectangle region, CancellationToken ct)
    {
        var first = await ReadInternalAsync(source, region, 1.0, ct).ConfigureAwait(false);
        return new(first.ActualText, first.Lines, null, null, false);
    }

    public async Task<OcrReadResult> ReadScaledAsync(Bitmap source, Rectangle region, double scale, CancellationToken ct)
    {
        var read = await ReadInternalAsync(source, region, scale, ct).ConfigureAwait(false);
        return new(read.ActualText, read.Lines, null, null, scale > 1.0);
    }

    private static (OcrLineHit? line, string? phrase) Match(IReadOnlyList<OcrLineHit> lines, IReadOnlyList<string> phrases, string mode)
    {
        foreach (var phrase in phrases)
        {
            foreach (var line in lines)
            {
                if (TextMatcher.IsMatch(line.Text, phrase, mode)) return (line, phrase);
                if (string.Equals(mode, "general", StringComparison.OrdinalIgnoreCase) &&
                    TextMatcher.Normalize(line.Text).Contains(TextMatcher.Normalize(phrase), StringComparison.OrdinalIgnoreCase))
                    return (line, phrase);
            }
        }
        return (null, null);
    }

    private async Task<(string ActualText, IReadOnlyList<OcrLineHit> Lines)> ReadInternalAsync(Bitmap source, Rectangle region, double scale, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var safe = Rectangle.Intersect(new Rectangle(Point.Empty, source.Size), region);
        if (safe.Width <= 1 || safe.Height <= 1) return ("", Array.Empty<OcrLineHit>());

        using var crop = source.Clone(safe, PixelFormat.Format24bppRgb);
        using var prepared = scale == 1.0 ? new Bitmap(crop) : Resize(crop, (int)Math.Round(crop.Width * scale), (int)Math.Round(crop.Height * scale));
        using var ms = new MemoryStream();
        prepared.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(bytes);
            ct.ThrowIfCancellationRequested();
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);

        ct.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(software);
        ct.ThrowIfCancellationRequested();

        var hits = new List<OcrLineHit>();
        var actualLines = new List<string>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            actualLines.Add(line.Text);
            var left = line.Words.Min(w => w.BoundingRect.X);
            var top = line.Words.Min(w => w.BoundingRect.Y);
            var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            var local = ScaleRect(left, top, right, bottom, scale, safe);
            hits.Add(new OcrLineHit(line.Text, local));
            foreach (var word in line.Words)
            {
                var w = word.BoundingRect;
                hits.Add(new OcrLineHit(word.Text, ScaleRect(w.X, w.Y, w.X + w.Width, w.Y + w.Height, scale, safe)));
            }
        }

        return (string.Join(" | ", actualLines), hits);
    }

    private static Rectangle ScaleRect(double left, double top, double right, double bottom, double scale, Rectangle offset)
    {
        var r = Rectangle.FromLTRB(
            (int)Math.Floor(left / scale),
            (int)Math.Floor(top / scale),
            (int)Math.Ceiling(right / scale),
            (int)Math.Ceiling(bottom / scale));
        r.Offset(offset.Left, offset.Top);
        return r;
    }

    private static Bitmap Resize(Bitmap src, int width, int height)
    {
        var dst = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, width, height);
        return dst;
    }
}
