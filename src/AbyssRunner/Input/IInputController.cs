namespace AbyssRunner.Input;

public interface IInputController : IDisposable
{
    InputDiagnostic Diagnose();
    Task<InputActionResult> TapScanCodeAsync(ushort scanCode, CancellationToken ct);
    Task<InputActionResult> ClickAsync(Rectangle windowLocalBounds, nint gameWindow, CancellationToken ct);
    void ReleaseAll();
}

public sealed record InputActionResult(bool Success, Point? WindowLocalPoint, string Reason)
{
    public static InputActionResult Ok(Point? p = null) => new(true, p, "ok");
    public static InputActionResult Fail(string reason, Point? p = null) => new(false, p, reason);
}

public sealed record InputDeviceInfo(int Id, string HardwareId, bool IsKeyboard);

public sealed record InputDiagnostic(
    bool DllPresent,
    bool X64Process,
    bool IsAdministrator,
    IReadOnlyList<InputDeviceInfo> Keyboards,
    IReadOnlyList<InputDeviceInfo> Mice,
    string Summary)
{
    public bool Ready(int keyboard, int mouse) =>
        DllPresent && X64Process && IsAdministrator &&
        Keyboards.Any(x => x.Id == keyboard) && Mice.Any(x => x.Id == mouse);
}
