using AbyssRunner.Vision;
using System.Text;
using System.Text.RegularExpressions;

namespace AbyssRunner.Core;

public sealed record LootItemRecord(string Name, int? Quantity, bool Recognized);

public sealed record LootSnapshot(
    DateTimeOffset CapturedAt,
    int Round,
    string Destination,
    IReadOnlyList<LootItemRecord> Recent,
    IReadOnlyDictionary<string, long> Totals,
    string RawOcr,
    string? ScreenshotPath);

public sealed class LootTracker
{
    private static readonly Regex QuantityRegex = new(@"^[\s×xX]*([0-9][0-9,]{0,8})[\s개]*$", RegexOptions.Compiled);
    private static readonly Regex HasHangulRegex = new(@"[가-힣]", RegexOptions.Compiled);
    private static readonly HashSet<string> IgnoreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "발견한", "전리품", "아이템을", "누르면", "상세", "정보를", "정보", "볼", "수", "있습니다",
        "나가기", "다시", "하기", "다른", "던전", "가기", "매우", "어려움", "순수", "전투", "시간"
    };

    private readonly string _baseDir;
    private readonly string _csvPath;
    private readonly string _unknownDir;
    private readonly KoreanOcr _ocr = new();
    private readonly Dictionary<string, long> _totals = new(StringComparer.OrdinalIgnoreCase);

    public LootTracker(string baseDir)
    {
        _baseDir = baseDir;
        _csvPath = Path.Combine(baseDir, "loot_history.csv");
        _unknownDir = Path.Combine(baseDir, "loot_unknown");
    }

    public void ResetSession() => _totals.Clear();

    public async Task<LootSnapshot> CaptureAsync(
        Bitmap screenshot,
        Rectangle region,
        Destination destination,
        int round,
        CancellationToken ct)
    {
        var safe = Rectangle.Intersect(new Rectangle(Point.Empty, screenshot.Size), region);
        if (safe.Width <= 10 || safe.Height <= 10)
            return new LootSnapshot(DateTimeOffset.Now, round, DestinationName(destination),
                Array.Empty<LootItemRecord>(), new Dictionary<string, long>(_totals), "", null);

        var ocr = await _ocr.ReadScaledAsync(screenshot, safe, 2.0, ct).ConfigureAwait(false);
        var items = ParseItems(ocr.Lines, safe);
        string? screenshotPath = null;

        if (items.Count == 0 || items.Any(x => !x.Recognized || x.Quantity is null))
        {
            Directory.CreateDirectory(_unknownDir);
            screenshotPath = Path.Combine(_unknownDir, $"{DateTime.Now:yyyyMMdd_HHmmssfff}_round{round:D4}.png");
            using var crop = screenshot.Clone(safe, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            crop.Save(screenshotPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        foreach (var item in items)
        {
            if (item.Recognized && item.Quantity is int q && q > 0)
                _totals[item.Name] = _totals.TryGetValue(item.Name, out var old) ? old + q : q;
        }

        AppendCsv(destination, round, items, ocr.ActualText, screenshotPath);

        var shown = items.Count > 0
            ? items
            : new List<LootItemRecord> { new("미인식 보상", null, false) };

        return new LootSnapshot(
            DateTimeOffset.Now,
            round,
            DestinationName(destination),
            shown,
            new Dictionary<string, long>(_totals, StringComparer.OrdinalIgnoreCase),
            ocr.ActualText,
            screenshotPath);
    }

    private static List<LootItemRecord> ParseItems(IReadOnlyList<OcrLineHit> hits, Rectangle region)
    {
        var tokens = hits
            .Select(x => new Token(Clean(x.Text), x.Bounds))
            .Where(x => x.Text.Length > 0)
            .Where(x => !x.Text.Any(char.IsWhiteSpace))
            .Where(x => x.Bounds.Width >= 3 && x.Bounds.Height >= 5)
            .Where(x => x.Bounds.Width <= 230 && x.Bounds.Height <= 100)
            .Where(x => region.IntersectsWith(x.Bounds))
            .GroupBy(x => (x.Text, x.Bounds.Left / 4, x.Bounds.Top / 4))
            .Select(g => g.First())
            .ToList();

        var numbers = tokens
            .Select(x => (Token: x, Qty: TryQuantity(x.Text)))
            .Where(x => x.Qty is not null && x.Qty > 0)
            .OrderBy(x => CenterX(x.Token.Bounds))
            .ToList();

        var items = new List<LootItemRecord>();
        var claimedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxDistance = Math.Clamp(region.Width / 12, 70, 105);

        foreach (var number in numbers)
        {
            var nx = CenterX(number.Token.Bounds);
            var nameParts = tokens
                .Where(t => t != number.Token)
                .Where(t => HasHangulRegex.IsMatch(t.Text))
                .Where(t => !IsIgnored(t.Text))
                .Where(t => Math.Abs(CenterX(t.Bounds) - nx) <= maxDistance)
                .Where(t => t.Bounds.Top >= number.Token.Bounds.Top - 35)
                .OrderBy(t => t.Bounds.Top)
                .ThenBy(t => t.Bounds.Left)
                .Select(t => t.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (nameParts.Count == 0) continue;

            var name = NormalizeName(string.Join(" ", nameParts));
            if (name.Length < 2 || claimedNames.Contains(name)) continue;

            claimedNames.Add(name);
            items.Add(new LootItemRecord(name, number.Qty, true));
        }

        if (items.Count == 0)
        {
            var names = tokens
                .Where(t => HasHangulRegex.IsMatch(t.Text))
                .Where(t => !IsIgnored(t.Text))
                .OrderBy(t => t.Bounds.Left)
                .ThenBy(t => t.Bounds.Top)
                .Select(t => NormalizeName(t.Text))
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            foreach (var name in names)
                items.Add(new LootItemRecord(name, null, false));
        }

        return items;
    }

    private void AppendCsv(Destination destination, int round, IReadOnlyList<LootItemRecord> items, string rawOcr, string? screenshotPath)
    {
        Directory.CreateDirectory(_baseDir);
        if (!File.Exists(_csvPath))
        {
            File.WriteAllText(
                _csvPath,
                "시간,던전,판수,아이템,수량,인식상태,원본OCR,보상캡처" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        var rows = new StringBuilder();
        if (items.Count == 0)
        {
            rows.AppendLine(CsvRow(DateTimeOffset.Now, DestinationName(destination), round, "미인식 보상", null, false, rawOcr, screenshotPath));
        }
        else
        {
            foreach (var item in items)
                rows.AppendLine(CsvRow(DateTimeOffset.Now, DestinationName(destination), round, item.Name, item.Quantity, item.Recognized, rawOcr, screenshotPath));
        }

        File.AppendAllText(_csvPath, rows.ToString(), new UTF8Encoding(false));
    }

    private static string CsvRow(DateTimeOffset time, string destination, int round, string name, int? quantity, bool recognized, string raw, string? image)
        => string.Join(",",
            Csv(time.ToString("yyyy-MM-dd HH:mm:ss")),
            Csv(destination),
            round.ToString(),
            Csv(name),
            quantity?.ToString() ?? "",
            Csv(recognized ? "인식" : "확인필요"),
            Csv(raw),
            Csv(image ?? ""));

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static int? TryQuantity(string text)
    {
        var m = QuantityRegex.Match(text);
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) ? v : null;
    }

    private static string Clean(string text)
    {
        var s = text.Trim();
        s = Regex.Replace(s, @"^[^0-9A-Za-z가-힣(\[]+", "");
        s = Regex.Replace(s, @"[^0-9A-Za-z가-힣)\]+]+$", "");
        return s.Trim();
    }

    private static string NormalizeName(string text)
    {
        var s = Regex.Replace(text, @"\s+", " ").Trim();
        return s.Length > 40 ? s[..40] : s;
    }

    private static bool IsIgnored(string text)
    {
        var normalized = Regex.Replace(text, @"[^0-9A-Za-z가-힣]", "");
        if (IgnoreWords.Contains(normalized)) return true;
        return normalized.Length <= 1;
    }

    private static int CenterX(Rectangle r) => r.Left + r.Width / 2;

    private static string DestinationName(Destination destination) => destination switch
    {
        Destination.Husang => "허상의 정박지",
        Destination.Kwanggi => "광기의 동굴",
        Destination.Moolgil => "흩어진 물길",
        _ => destination.ToString()
    };

    private sealed record Token(string Text, Rectangle Bounds);
}
