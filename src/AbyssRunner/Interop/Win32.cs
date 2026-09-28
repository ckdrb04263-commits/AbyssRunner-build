using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AbyssRunner.Core;

namespace AbyssRunner.Interop;

internal static class Win32
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_F10 = 0x79;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeLibrary(nint hModule);

    public static Rectangle GetBounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static IReadOnlyList<WindowCandidate> EnumerateVisibleWindows()
    {
        var list = new List<WindowCandidate>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;
            try
            {
                var bounds = GetBounds(hwnd);
                if (bounds.Width > 100 && bounds.Height > 100)
                    list.Add(new WindowCandidate(hwnd, title, bounds));
            }
            catch { }
            return true;
        }, 0);
        return list.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static TimeSpan TimeSinceLastInput()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
        var now = unchecked((uint)Environment.TickCount);
        var elapsed = unchecked(now - lii.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
