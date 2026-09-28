using AbyssRunner.Config;
using AbyssRunner.Core;
using OpenCvSharp;

namespace AbyssRunner.Vision;

public sealed class Detector
{
    private readonly KoreanOcr _ocr;
    private readonly TemplateMatcher _templates;

    public Detector(KoreanOcr ocr, TemplateMatcher templates)
    {
        _ocr = ocr;
        _templates = templates;
    }

    public async Task<DetectionEvidence> DetectAsync(Bitmap screenshot, CaptureProfile profile, string targetName, CancellationToken ct)
    {
        if (!profile.Targets.TryGetValue(targetName, out var def) || !def.Enabled)
            return new(false, 0, "", null, 0, "disabled", null, null, $"대상 {targetName} 비활성/미정의");
        if (!profile.Regions.TryGetValue(def.Region, out var rr))
            return new(false, 0, "", null, 0, "no-region", null, null, $"영역 {def.Region} 미정의");

        var region = rr.Clamp(screenshot.Width, screenshot.Height).ToRectangle();
        OcrReadResult? ocr = null;
        if (def.Ocr is { Phrases.Count: > 0 })
            ocr = await _ocr.FindAsync(screenshot, region, def.Ocr.Phrases, def.Ocr.Mode, ct).ConfigureAwait(false);

        var template = def.Template is { Files.Count: > 0 }
            ? _templates.MatchBest(screenshot, region, def.Template.Files, def.Template.Threshold ?? profile.DefaultTemplateThreshold)
            : new TemplateMatchResult(false, null, 0, null);

        var ocrMatched = ocr?.MatchedLine is not null;
        var tplMatched = template.Matched;
        var ocrBounds = ocr?.MatchedLine?.Bounds;
        var tplBounds = template.Bounds;
        var coherent = !ocrMatched || !tplMatched || AreSameTarget(ocrBounds!.Value, tplBounds!.Value);

        var candidates = new List<(int score, Rectangle? bounds, string color, string reason)>();
        if (ocrMatched)
        {
            var c = EvaluateColor(screenshot, region, ocrBounds!.Value, def.Color);
            candidates.Add(((def.Ocr?.Weight ?? 0) + c.score, ocrBounds, c.label, "ocr"));
        }
        if (tplMatched)
        {
            var c = EvaluateColor(screenshot, region, tplBounds!.Value, def.Color);
            candidates.Add(((def.Template?.Weight ?? 0) + c.score, tplBounds, c.label, "template"));
        }
        if (ocrMatched && tplMatched && coherent)
        {
            var click = tplBounds ?? ocrBounds;
            var c = EvaluateColor(screenshot, region, click!.Value, def.Color);
            candidates.Add(((def.Ocr?.Weight ?? 0) + (def.Template?.Weight ?? 0) + c.score, click, c.label, "ocr+template"));
        }
        if (!ocrMatched && !tplMatched && def.Color is not null)
            candidates.Add((0, null, "색상만으로 판정 금지", "color-only-disallowed"));

        if (candidates.Count == 0)
            return new(false, 0, ocr?.ActualText ?? "", template.FileName, template.Score, "n/a", null, null,
                ocrMatched || tplMatched ? "검출 근거 조합 없음" : "OCR/사진 불일치");

        var best = candidates.OrderByDescending(x => x.score).First();
        var evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ocrMatched && (best.reason.Contains("ocr"))) evidence.Add("ocr");
        if (tplMatched && (best.reason.Contains("template"))) evidence.Add("template");
        if (best.color.StartsWith("green:pass") || best.color.StartsWith("blue:pass")) evidence.Add(best.color.StartsWith("green") ? "green" : "blue");

        var requiredOk = def.RequiredEvidence.All(r => evidence.Contains(r));
        var matched = best.score >= def.MinimumScore && requiredOk;
        var reason = !coherent && ocrMatched && tplMatched
            ? "OCR과 사진 위치가 서로 달라 점수를 합산하지 않음"
            : best.reason;
        if (!requiredOk) reason += $"; 필수근거 부족({string.Join(',', def.RequiredEvidence)})";

        return new DetectionEvidence(
            matched,
            best.score,
            ocr?.ActualText ?? "",
            template.FileName,
            template.Score,
            best.color,
            best.bounds,
            best.bounds,
            reason);
    }

    public async Task<(bool IsOpen, string ActualOcr, IReadOnlyList<string> Names)> DetectMenuOpenAsync(Bitmap screenshot, CaptureProfile profile, CancellationToken ct)
    {
        if (!profile.Regions.TryGetValue("menuCheck", out var rr)) return (false, "", Array.Empty<string>());
        var region = rr.Clamp(screenshot.Width, screenshot.Height).ToRectangle();
        var result = await _ocr.ReadAsync(screenshot, region, ct).ConfigureAwait(false);
        var menuNames = new[] { "캐릭터", "가방", "크래프팅", "연금술", "퀘스트", "아르바이트", "미션", "거래소", "환경설정" };
        var found = FindExactMenuNames(result.Lines, menuNames);
        if (found.Count < 3)
        {
            result = await _ocr.ReadScaledAsync(screenshot, region, 2.0, ct).ConfigureAwait(false);
            found = FindExactMenuNames(result.Lines, menuNames);
        }
        return (found.Count >= 3, result.ActualText, found.ToList());
    }

    public async Task<(bool HasChat, string ActualOcr)> DetectMainChatAsync(Bitmap screenshot, CaptureProfile profile, CancellationToken ct)
    {
        if (!profile.Regions.TryGetValue("chatInput", out var rr)) return (false, "");
        var result = await _ocr.FindAsync(screenshot, rr.Clamp(screenshot.Width, screenshot.Height).ToRectangle(), new[] { "말하기" }, "contains", ct).ConfigureAwait(false);
        return (result.MatchedLine is not null, result.ActualText);
    }

    public async Task<StateClassification> ClassifyRecoverableStateAsync(Bitmap screenshot, CaptureProfile profile, Destination destination, CancellationToken ct)
    {
        var checks = new List<(RunStage stage, string name)> {
            (RunStage.WaitResult, "resultTouch"),
            (RunStage.Retry, "retry"),
            (RunStage.Enter, "enter"),
            (RunStage.SelectDestination, DestinationTarget(destination)),
            (RunStage.SelectAbyss, "abyssMenu")
        };

        var matches = new List<(RunStage stage, string name)>();
        foreach (var c in checks)
        {
            var d = await DetectAsync(screenshot, profile, c.name, ct).ConfigureAwait(false);
            if (d.Matched) matches.Add(c);
        }
        var menu = await DetectMenuOpenAsync(screenshot, profile, ct).ConfigureAwait(false);
        if (menu.IsOpen && !matches.Any(x => x.stage == RunStage.SelectAbyss)) matches.Add((RunStage.SelectAbyss, "menu-open"));

        var distinct = matches.Select(x => x.stage).Distinct().ToList();
        if (distinct.Count == 1)
            return new(distinct[0], string.Join(", ", matches.Select(x => x.name)), matches.Select(x => x.name).ToList());
        if (distinct.Count == 2 && distinct.Contains(RunStage.Enter) && distinct.Contains(RunStage.SelectDestination))
            return new(RunStage.Enter, "입장하기 + 선택 목적지 배너 동시 검출", matches.Select(x => x.name).ToList());
        if (distinct.Count > 1)
            return new(null, "서로 충돌하는 상태가 동시에 감지됨", matches.Select(x => x.name).ToList());
        return new(null, "분류 가능한 상태 근거 없음", Array.Empty<string>());
    }

    public static string DestinationTarget(Destination d) => d switch
    {
        Destination.Husang => "destinationHusang",
        Destination.Kwanggi => "destinationKwanggi",
        Destination.Moolgil => "destinationMoolgil",
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };

    private static HashSet<string> FindExactMenuNames(IReadOnlyList<OcrLineHit> lines, IReadOnlyList<string> menuNames)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var norm = TextMatcher.Normalize(line.Text);
            foreach (var name in menuNames)
            {
                if (string.Equals(norm, TextMatcher.Normalize(name), StringComparison.OrdinalIgnoreCase)) found.Add(name);
            }
        }
        return found;
    }

    private static bool AreSameTarget(Rectangle a, Rectangle b)
    {
        var ca = new Point(a.Left + a.Width / 2, a.Top + a.Height / 2);
        var cb = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        var dx = ca.X - cb.X;
        var dy = ca.Y - cb.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var tolerance = Math.Max(40, Math.Max(Math.Max(a.Width, b.Width), Math.Max(a.Height, b.Height)) * 1.5);
        return distance <= tolerance;
    }

    private static (int score, string label) EvaluateColor(Bitmap screenshot, Rectangle searchRegion, Rectangle candidate, ColorDefinition? def)
    {
        if (def is null) return (0, "n/a");
        var expanded = Expand(candidate, 1.8, 1.6);
        expanded = Rectangle.Intersect(expanded, searchRegion);
        expanded = Rectangle.Intersect(expanded, new Rectangle(Point.Empty, screenshot.Size));
        if (expanded.Width < 2 || expanded.Height < 2) return (0, "color:no-area");

        using var crop = screenshot.Clone(expanded, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var ms = new MemoryStream();
        crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        using var mat = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
        var bs = new byte[mat.Rows * mat.Cols];
        var gs = new byte[bs.Length];
        var rs = new byte[bs.Length];
        var k = 0;
        for (var y = 0; y < mat.Rows; y++)
        for (var x = 0; x < mat.Cols; x++)
        {
            var v = mat.At<Vec3b>(y, x);
            bs[k] = v.Item0; gs[k] = v.Item1; rs[k] = v.Item2; k++;
        }
        Array.Sort(bs); Array.Sort(gs); Array.Sort(rs);
        var b = bs[bs.Length / 2]; var g = gs[gs.Length / 2]; var r = rs[rs.Length / 2];
        var green = g - Math.Max(r, b) >= def.Dominance;
        var blue = b - Math.Max(r, g) >= def.Dominance;
        var mode = def.Mode.ToLowerInvariant();
        var pass = mode switch
        {
            "green" => green,
            "blue" => blue,
            "greenorblue" => green || blue,
            _ => false
        };
        var label = mode == "greenorblue"
            ? $"{(green ? "green" : blue ? "blue" : "greenorblue")}:{(pass ? "pass" : "fail")}(B{b}/G{g}/R{r})"
            : $"{mode}:{(pass ? "pass" : "fail")}(B{b}/G{g}/R{r})";
        return (pass ? def.Weight : 0, label);
    }

    private static Rectangle Expand(Rectangle r, double fx, double fy)
    {
        var w = Math.Max(2, (int)Math.Round(r.Width * fx));
        var h = Math.Max(2, (int)Math.Round(r.Height * fy));
        var cx = r.Left + r.Width / 2;
        var cy = r.Top + r.Height / 2;
        return new Rectangle(cx - w / 2, cy - h / 2, w, h);
    }
}
