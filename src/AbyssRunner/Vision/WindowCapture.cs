using System.Drawing.Imaging;
using AbyssRunner.Interop;

namespace AbyssRunner.Vision;

public sealed class WindowCapture
{
    public Bitmap Capture(nint hwnd)
    {
        var bounds = Win32.GetBounds(hwnd);
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("게임 창 크기를 읽을 수 없습니다.");
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);

        var printed = false;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { printed = Win32.PrintWindow(hwnd, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        if (printed && !LooksAlmostBlack(bmp)) return bmp;

        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    private static bool LooksAlmostBlack(Bitmap bmp)
    {
        var dark = 0;
        var total = 0;
        var sx = Math.Max(1, bmp.Width / 12);
        var sy = Math.Max(1, bmp.Height / 12);
        for (var y = sy / 2; y < bmp.Height; y += sy)
        for (var x = sx / 2; x < bmp.Width; x += sx)
        {
            var c = bmp.GetPixel(x, y);
            if (c.R < 8 && c.G < 8 && c.B < 8) dark++;
            total++;
        }
        return total > 0 && dark >= total * 0.95;
    }
}
