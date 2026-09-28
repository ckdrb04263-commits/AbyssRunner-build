using System.Drawing.Imaging;
using OpenCvSharp;

namespace AbyssRunner.Vision;

public sealed record TemplateMatchResult(bool Matched, string? FileName, double Score, Rectangle? Bounds);

public sealed class TemplateMatcher
{
    private readonly string _imagesDir;

    public TemplateMatcher(string imagesDir) => _imagesDir = imagesDir;

    public TemplateMatchResult MatchBest(Bitmap screenshot, Rectangle region, IEnumerable<string> templateFiles, double threshold)
    {
        var safe = Rectangle.Intersect(new Rectangle(Point.Empty, screenshot.Size), region);
        if (safe.Width <= 1 || safe.Height <= 1) return new(false, null, 0, null);

        using var source = BitmapToMat(screenshot);
        using var roi = new Mat(source, new OpenCvSharp.Rect(safe.X, safe.Y, safe.Width, safe.Height));

        string? bestName = null;
        double bestScore = double.NegativeInfinity;
        Rectangle? bestBounds = null;

        foreach (var file in templateFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(_imagesDir, file);
            if (!File.Exists(path)) continue;
            using var tpl = Cv2.ImRead(path, ImreadModes.Color);
            if (tpl.Empty() || tpl.Width > roi.Width || tpl.Height > roi.Height) continue;

            using var result = new Mat();
            Cv2.MatchTemplate(roi, tpl, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var max, out _, out var maxLoc);
            if (max > bestScore)
            {
                bestScore = max;
                bestName = file;
                bestBounds = new Rectangle(safe.Left + maxLoc.X, safe.Top + maxLoc.Y, tpl.Width, tpl.Height);
            }
        }

        if (double.IsNegativeInfinity(bestScore)) bestScore = 0;
        return new(bestScore >= threshold, bestName, bestScore, bestBounds);
    }

    private static Mat BitmapToMat(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
    }
}
