using System.Security.Principal;
using AbyssRunner.Config;
using AbyssRunner.Interop;

namespace AbyssRunner.Input;

public sealed class InterceptionInputController : IInputController
{
    private readonly InputConfig _config;
    private readonly nint _context;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ushort? _heldKey;
    private bool _mouseHeld;
    private bool _disposed;

    public InterceptionInputController(InputConfig config)
    {
        _config = config;
        _context = InterceptionNative.CreateContext();
        if (_context == 0) throw new InvalidOperationException("Interception 컨텍스트 생성 실패. 드라이버와 DLL을 확인하세요.");
    }

    public InputDiagnostic Diagnose()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, "interception.dll");
        var dllPresent = File.Exists(dllPath);
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var keyboards = new List<InputDeviceInfo>();
        var mice = new List<InputDeviceInfo>();
        for (var id = 1; id <= 20; id++)
        {
            var sb = new System.Text.StringBuilder(512);
            uint n;
            try { n = InterceptionNative.GetHardwareId(_context, id, sb, (uint)(sb.Capacity * sizeof(char))); }
            catch { n = 0; }
            if (n == 0 || string.IsNullOrWhiteSpace(sb.ToString())) continue;
            var info = new InputDeviceInfo(id, sb.ToString(), id <= 10);
            if (id <= 10) keyboards.Add(info); else mice.Add(info);
        }
        var summary = $"DLL={(dllPresent ? "OK" : "없음")}, x64={Environment.Is64BitProcess}, 관리자={admin}, 키보드={keyboards.Count}, 마우스={mice.Count}";
        return new(dllPresent, Environment.Is64BitProcess, admin, keyboards, mice, summary);
    }

    public async Task<InputActionResult> TapScanCodeAsync(ushort scanCode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return InputActionResult.Fail("disposed");
            var down = new InterceptionNative.KeyStroke { Code = scanCode, State = InterceptionNative.KeyDown };
            if (InterceptionNative.SendKey(_context, _config.KeyboardDevice, ref down, 1) != 1) return InputActionResult.Fail("key-down 전달 실패");
            _heldKey = scanCode;
            await Task.Delay(Random.Shared.Next(_config.KeyHoldMinMs, _config.KeyHoldMaxMs + 1), ct).ConfigureAwait(false);
            var up = new InterceptionNative.KeyStroke { Code = scanCode, State = InterceptionNative.KeyUp };
            var ok = InterceptionNative.SendKey(_context, _config.KeyboardDevice, ref up, 1) == 1;
            _heldKey = null;
            return ok ? InputActionResult.Ok() : InputActionResult.Fail("key-up 전달 실패");
        }
        catch (OperationCanceledException)
        {
            ReleaseAllUnsafe();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<InputActionResult> ClickAsync(Rectangle windowLocalBounds, nint gameWindow, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return InputActionResult.Fail("disposed");
            if (Win32.GetForegroundWindow() != gameWindow) return InputActionResult.Fail("게임 창 비활성");
            var wb = Win32.GetBounds(gameWindow);
            var p = PickPointInside(windowLocalBounds);
            var screen = new Point(wb.Left + p.X, wb.Top + p.Y);
            if (!MoveAbsolute(screen)) return InputActionResult.Fail("마우스 이동 전달 실패", p);
            await Task.Delay(35, ct).ConfigureAwait(false);
            if (!Win32.GetCursorPos(out var cursor)) return InputActionResult.Fail("커서 위치 확인 실패", p);
            var dx = cursor.X - screen.X;
            var dy = cursor.Y - screen.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > _config.CursorTolerancePx) return InputActionResult.Fail("사용자 이동/커서 목표 이탈", p);
            if (Win32.GetForegroundWindow() != gameWindow) return InputActionResult.Fail("클릭 직전 게임 창 비활성", p);

            var down = new InterceptionNative.MouseStroke { State = InterceptionNative.MouseLeftDown };
            if (InterceptionNative.SendMouse(_context, _config.MouseDevice, ref down, 1) != 1) return InputActionResult.Fail("mouse-down 전달 실패", p);
            _mouseHeld = true;
            await Task.Delay(Random.Shared.Next(_config.MouseHoldMinMs, _config.MouseHoldMaxMs + 1), ct).ConfigureAwait(false);
            var up = new InterceptionNative.MouseStroke { State = InterceptionNative.MouseLeftUp };
            var ok = InterceptionNative.SendMouse(_context, _config.MouseDevice, ref up, 1) == 1;
            _mouseHeld = false;
            return ok ? InputActionResult.Ok(p) : InputActionResult.Fail("mouse-up 전달 실패", p);
        }
        catch (OperationCanceledException)
        {
            ReleaseAllUnsafe();
            throw;
        }
        finally { _gate.Release(); }
    }

    public void ReleaseAll()
    {
        if (_disposed) return;
        _gate.Wait();
        try { ReleaseAllUnsafe(); }
        finally { _gate.Release(); }
    }

    private void ReleaseAllUnsafe()
    {
        if (_heldKey is ushort key)
        {
            var up = new InterceptionNative.KeyStroke { Code = key, State = InterceptionNative.KeyUp };
            try { InterceptionNative.SendKey(_context, _config.KeyboardDevice, ref up, 1); } catch { }
            _heldKey = null;
        }
        if (_mouseHeld)
        {
            var up = new InterceptionNative.MouseStroke { State = InterceptionNative.MouseLeftUp };
            try { InterceptionNative.SendMouse(_context, _config.MouseDevice, ref up, 1); } catch { }
            _mouseHeld = false;
        }
    }

    private bool MoveAbsolute(Point screen)
    {
        var v = SystemInformation.VirtualScreen;
        var x = v.Width <= 1 ? 0 : (int)Math.Round((screen.X - v.Left) * 65535.0 / (v.Width - 1));
        var y = v.Height <= 1 ? 0 : (int)Math.Round((screen.Y - v.Top) * 65535.0 / (v.Height - 1));
        x = Math.Clamp(x, 0, 65535); y = Math.Clamp(y, 0, 65535);
        var move = new InterceptionNative.MouseStroke
        {
            Flags = (ushort)(InterceptionNative.MouseMoveAbsolute | InterceptionNative.MouseVirtualDesktop),
            X = x,
            Y = y
        };
        return InterceptionNative.SendMouse(_context, _config.MouseDevice, ref move, 1) == 1;
    }

    private static Point PickPointInside(Rectangle r)
    {
        if (r.Width <= 1 || r.Height <= 1) return new Point(r.Left, r.Top);
        var marginX = Math.Min(Math.Max(1, r.Width / 5), Math.Max(1, (r.Width - 1) / 2));
        var marginY = Math.Min(Math.Max(1, r.Height / 5), Math.Max(1, (r.Height - 1) / 2));
        var minX = r.Left + marginX; var maxX = Math.Max(minX, r.Right - marginX - 1);
        var minY = r.Top + marginY; var maxY = Math.Max(minY, r.Bottom - marginY - 1);
        return new Point(Random.Shared.Next(minX, maxX + 1), Random.Shared.Next(minY, maxY + 1));
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseAll();
        _disposed = true;
        if (_context != 0) InterceptionNative.DestroyContext(_context);
        _gate.Dispose();
    }
}
