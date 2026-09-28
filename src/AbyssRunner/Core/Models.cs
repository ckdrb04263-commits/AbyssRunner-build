using System.Drawing;

namespace AbyssRunner.Core;

public enum Destination
{
    Husang,
    Kwanggi,
    Moolgil
}

public enum RunStage
{
    Idle,
    Countdown,
    OpenMenu,
    SelectAbyss,
    SelectDestination,
    Enter,
    WaitResult,
    Retry,
    ErrorPaused,
    Stopped
}

public enum StepOutcome
{
    Success,
    Failure,
    UserStopped
}

public sealed record StepResult(StepOutcome Outcome, string Reason = "")
{
    public static StepResult Ok(string reason = "") => new(StepOutcome.Success, reason);
    public static StepResult Fail(string reason) => new(StepOutcome.Failure, reason);
    public static StepResult Stopped(string reason = "사용자 정지") => new(StepOutcome.UserStopped, reason);
}

public readonly record struct IntRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public Rectangle ToRectangle() => new(Left, Top, Width, Height);
    public bool Contains(Point p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public IntRect Clamp(int width, int height)
        => new(Math.Clamp(Left, 0, width), Math.Clamp(Top, 0, height), Math.Clamp(Right, 0, width), Math.Clamp(Bottom, 0, height));

    public static IntRect FromRectangle(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

public sealed record DetectionEvidence(
    bool Matched,
    int Score,
    string ActualOcr,
    string? TemplateName,
    double TemplateScore,
    string ColorEvidence,
    Rectangle? MatchBounds,
    Rectangle? ClickBounds,
    string Reason);

public sealed record StateClassification(RunStage? Stage, string Reason, IReadOnlyList<string> Matches);

public sealed record WindowCandidate(nint Handle, string Title, Rectangle Bounds)
{
    public override string ToString() => $"{Title}  [{Bounds.Width}×{Bounds.Height}]";
}
