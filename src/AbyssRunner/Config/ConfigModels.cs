using AbyssRunner.Core;

namespace AbyssRunner.Config;

public sealed class AppConfig
{
    public string ActiveProfile { get; set; } = "816x1039";
    public string UiScaleLabel { get; set; } = "확인 필요";
    public TimeoutConfig Timeouts { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public FeatureConfig Features { get; set; } = new();
    public AutoResumeConfig AutoResume { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public sealed class TimeoutConfig
{
    public int GeneralSeconds { get; set; } = 20;
    public int RetrySeconds { get; set; } = 60;
    public int CombatSeconds { get; set; } = 600;
    public int PollGeneralMinMs { get; set; } = 380;
    public int PollGeneralMaxMs { get; set; } = 520;
    public int PollCombatMinMs { get; set; } = 620;
    public int PollCombatMaxMs { get; set; } = 800;
}

public sealed class InputConfig
{
    public string Provider { get; set; } = "Interception";
    public int KeyboardDevice { get; set; } = 1;
    public int MouseDevice { get; set; } = 11;
    public int KeyHoldMinMs { get; set; } = 40;
    public int KeyHoldMaxMs { get; set; } = 110;
    public int MouseHoldMinMs { get; set; } = 30;
    public int MouseHoldMaxMs { get; set; } = 110;
    public int CursorTolerancePx { get; set; } = 8;
}

public sealed class FeatureConfig
{
    public bool FoodAssist { get; set; } = false;
    public bool RevivalAssist { get; set; } = false;
    public bool ReconnectAssist { get; set; } = false;
    public bool SkipDialogueAssist { get; set; } = true;
}

public sealed class AutoResumeConfig
{
    public bool Enabled { get; set; } = false;
    public int ErrorWaitMinutes { get; set; } = 10;
    public int UserIdleMinutes { get; set; } = 10;
    public int StableSamples { get; set; } = 3;
    public int SampleIntervalMs { get; set; } = 250;
    public int CountdownSeconds { get; set; } = 3;
}

public sealed class LoggingConfig
{
    public int MaxFileMb { get; set; } = 8;
    public int MaxFiles { get; set; } = 6;
    public int MaxDiagnostics { get; set; } = 80;
}

public sealed class TargetsFile
{
    public Dictionary<string, CaptureProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CaptureProfile
{
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public bool IncludesWindowFrame { get; set; } = true;
    public double DefaultTemplateThreshold { get; set; } = 0.75;
    public Dictionary<string, IntRect> Regions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TargetDefinition> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TargetDefinition
{
    public string Region { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int MinimumScore { get; set; } = 2;
    public List<string> RequiredEvidence { get; set; } = new();
    public OcrDefinition? Ocr { get; set; }
    public TemplateDefinition? Template { get; set; }
    public ColorDefinition? Color { get; set; }
}

public sealed class OcrDefinition
{
    public List<string> Phrases { get; set; } = new();
    public string Mode { get; set; } = "general";
    public int Weight { get; set; } = 2;
}

public sealed class TemplateDefinition
{
    public List<string> Files { get; set; } = new();
    public double? Threshold { get; set; }
    public int Weight { get; set; } = 2;
}

public sealed class ColorDefinition
{
    public string Mode { get; set; } = "green";
    public int Weight { get; set; } = 1;
    public int Dominance { get; set; } = 60;
}
