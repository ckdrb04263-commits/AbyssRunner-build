using AbyssRunner.Config;
using AbyssRunner.Input;
using AbyssRunner.Interop;
using AbyssRunner.Logging;
using AbyssRunner.Vision;

namespace AbyssRunner.Core;

public sealed class AutomationEngine : IDisposable
{
    private readonly LoadedConfig _config;
    private readonly IInputController _input;
    private readonly RollingLogger _log;
    private readonly DiagnosticStore _diagnostics;
    private readonly WindowCapture _capture = new();
    private readonly Detector _detector;
    private readonly string _imagesDir;
    private DateTimeOffset _started;
    private DateTimeOffset _roundStarted;
    private readonly List<TimeSpan> _roundTimes = new();
    private int _completed;
    private RunStage _stage = RunStage.Idle;

    public event Action<RunStage, string>? StatusChanged;
    public event Action<int, TimeSpan, TimeSpan?>? MetricsChanged;

    public AutomationEngine(LoadedConfig config, IInputController input, RollingLogger log, DiagnosticStore diagnostics)
    {
        _config = config;
        _input = input;
        _log = log;
        _diagnostics = diagnostics;
        _imagesDir = Path.Combine(config.BaseDirectory, "images");
        _detector = new Detector(new KoreanOcr(), new TemplateMatcher(_imagesDir));
    }

    public async Task RunAsync(nint gameWindow, Destination destination, bool recoverCurrentState, CancellationToken ct)
    {
        _started = DateTimeOffset.Now;
        _roundStarted = _started;
        _completed = 0;
        _roundTimes.Clear();

        try
        {
            ValidatePrerequisites(gameWindow, destination);
            await CountdownAsync(3, ct).ConfigureAwait(false);

            var stage = recoverCurrentState
                ? await ResolveRecoveryStartAsync(gameWindow, destination, ct).ConfigureAwait(false)
                : RunStage.OpenMenu;

            while (!ct.IsCancellationRequested)
            {
                SetStage(stage, StageText(stage));
                var result = stage switch
                {
                    RunStage.OpenMenu => await OpenMenuAsync(gameWindow, ct).ConfigureAwait(false),
                    RunStage.SelectAbyss => await SelectAbyssAsync(gameWindow, destination, ct).ConfigureAwait(false),
                    RunStage.SelectDestination => await SelectDestinationAsync(gameWindow, destination, ct).ConfigureAwait(false),
                    RunStage.Enter => await EnterAsync(gameWindow, ct).ConfigureAwait(false),
                    RunStage.WaitResult => await WaitResultAsync(gameWindow, ct).ConfigureAwait(false),
                    RunStage.Retry => await RetryAsync(gameWindow, ct).ConfigureAwait(false),
                    _ => StepResult.Fail("지원하지 않는 단계")
                };

                if (result.Outcome == StepOutcome.UserStopped) break;
                if (result.Outcome == StepOutcome.Failure)
                {
                    _input.ReleaseAll();
                    await RecordFailureAsync(gameWindow, stage, result.Reason).ConfigureAwait(false);
                    SetStage(RunStage.ErrorPaused, result.Reason);
                    _ = SoundNotifier.ErrorForThreeSecondsAsync();
                    return;
                }

                stage = stage switch
                {
                    RunStage.OpenMenu => RunStage.SelectAbyss,
                    RunStage.SelectAbyss => RunStage.SelectDestination,
                    RunStage.SelectDestination => RunStage.Enter,
                    RunStage.Enter => RunStage.WaitResult,
                    RunStage.WaitResult => RunStage.Retry,
                    RunStage.Retry => CompleteRound(),
                    _ => throw new InvalidOperationException()
                };
            }
        }
        catch (OperationCanceledException)
        {
            _log.Write(_stage, "user-stop");
        }
        catch (Exception ex)
        {
            _log.Write(_stage, "fatal", new { ex.Message });
            try { await RecordFailureAsync(gameWindow, _stage, ex.Message).ConfigureAwait(false); } catch { }
            SetStage(RunStage.ErrorPaused, ex.Message);
        }
        finally
        {
            _input.ReleaseAll();
            if (ct.IsCancellationRequested) SetStage(RunStage.Stopped, "사용자 정지");
        }
    }

    private void ValidatePrerequisites(nint hwnd, Destination destination)
    {
        using var shot = _capture.Capture(hwnd);
        var profile = ProfileResolver.Resolve(_config, shot.Width, shot.Height);
        var name = Detector.DestinationTarget(destination);
        if (!profile.Targets.TryGetValue(name, out var d) || d.Template is null ||
            !d.Template.Files.Any(f => File.Exists(Path.Combine(_imagesDir, f))))
            throw new InvalidOperationException("선택한 목적지의 실제 배너 템플릿이 없습니다. images 폴더에 교정된 PNG를 먼저 등록하세요.");

        if (_config.App.Features.RevivalAssist || _config.App.Features.FoodAssist || _config.App.Features.ReconnectAssist)
            throw new InvalidOperationException("이 빌드에서 부활/음식/재접속 보조는 실제 화면 교정 자료가 없는 상태이므로 켤 수 없습니다.");
    }

    private async Task<RunStage> ResolveRecoveryStartAsync(nint hwnd, Destination destination, CancellationToken ct)
    {
        using var shot = CaptureChecked(hwnd, out var profile);
        var state = await _detector.ClassifyRecoverableStateAsync(shot, profile, destination, ct).ConfigureAwait(false);
        if (state.Stage is RunStage stage) return stage;
        _diagnostics.Save(shot, RunStage.ErrorPaused, "recovery-unknown");
        throw new InvalidOperationException("현재 화면을 확실히 분류하지 못했습니다. 임의 입력 없이 정지합니다.");
    }

    private async Task CountdownAsync(int seconds, CancellationToken ct)
    {
        for (var i = seconds; i >= 1; i--)
        {
            SetStage(RunStage.Countdown, $"{i}초 후 시작");
            SoundNotifier.CountdownTick();
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private async Task<StepResult> OpenMenuAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var escSent = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var menu = await _detector.DetectMenuOpenAsync(shot, profile, ct).ConfigureAwait(false);
            if (menu.IsOpen)
            {
                _log.Write(_stage, "menu-open", new { menu.ActualOcr, menu.Names });
                return StepResult.Ok();
            }

            if (!escSent)
            {
                var chat = await _detector.DetectMainChatAsync(shot, profile, ct).ConfigureAwait(false);
                if (chat.HasChat)
                {
                    if (Win32.GetForegroundWindow() != hwnd) return StepResult.Fail("ESC 입력 직전 게임 창 비활성");
                    var sent = await _input.TapScanCodeAsync(0x01, ct).ConfigureAwait(false);
                    _log.Write(_stage, "key", new { key = "ESC", sent.Success, sent.Reason });
                    if (!sent.Success) return StepResult.Fail("ESC 입력 실패: " + sent.Reason);
                    escSent = true;
                    await Task.Delay(700, ct).ConfigureAwait(false);
                    continue;
                }
            }
            await GeneralDelay(ct).ConfigureAwait(false);
        }
        return StepResult.Fail("15초 안에 메뉴 열림을 확인하지 못함");
    }

    private async Task<StepResult> SelectAbyssAsync(nint hwnd, Destination destination, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.GeneralSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, "abyssMenu", ct).ConfigureAwait(false);
            LogDetection("abyssMenu", d);
            if (d.Matched && d.ClickBounds is Rectangle r)
            {
                var click = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                if (!click.Success) return StepResult.Fail("어비스 클릭 실패: " + click.Reason);
                return await WaitForTargetAsync(hwnd, Detector.DestinationTarget(destination), _config.App.Timeouts.GeneralSeconds, ct).ConfigureAwait(false);
            }
            await GeneralDelay(ct).ConfigureAwait(false);
        }
        return StepResult.Fail("어비스 항목을 찾지 못함");
    }

    private async Task<StepResult> SelectDestinationAsync(nint hwnd, Destination destination, CancellationToken ct)
    {
        var target = Detector.DestinationTarget(destination);
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.GeneralSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, target, ct).ConfigureAwait(false);
            LogDetection(target, d);
            if (d.Matched && d.ClickBounds is Rectangle r)
            {
                var click = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                if (!click.Success) return StepResult.Fail("목적지 클릭 실패: " + click.Reason);
                return await WaitForTargetAsync(hwnd, "enter", _config.App.Timeouts.GeneralSeconds, ct).ConfigureAwait(false);
            }
            await GeneralDelay(ct).ConfigureAwait(false);
        }
        return StepResult.Fail("선택한 목적지 배너를 찾지 못함");
    }

    private async Task<StepResult> EnterAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.GeneralSeconds);
        var attempts = 0;
        while (DateTime.UtcNow < deadline && attempts < 4)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, "enter", ct).ConfigureAwait(false);
            LogDetection("enter", d);
            if (!d.Matched) { await GeneralDelay(ct).ConfigureAwait(false); continue; }

            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (Win32.GetForegroundWindow() != hwnd) return StepResult.Fail("SPACE 입력 직전 게임 창 비활성");
            var sent = await _input.TapScanCodeAsync(0x39, ct).ConfigureAwait(false);
            attempts++;
            _log.Write(_stage, "key", new { key = "SPACE", sent.Success, sent.Reason, attempts });
            if (!sent.Success) return StepResult.Fail("SPACE 입력 실패: " + sent.Reason);
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await IsGoneStableAsync(hwnd, "enter", ct).ConfigureAwait(false)) return StepResult.Ok();
        }
        return StepResult.Fail("입장하기 입력 후 화면 전환 확인 실패");
    }

    private async Task<StepResult> WaitResultAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.CombatSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);

            if (_config.App.Features.SkipDialogueAssist)
            {
                var skip = await _detector.DetectAsync(shot, profile, "skipDialogue", ct).ConfigureAwait(false);
                if (skip.Matched && skip.ClickBounds is Rectangle skipBox)
                {
                    var action = await _input.ClickAsync(skipBox, hwnd, ct).ConfigureAwait(false);
                    _log.Write(_stage, "skip-dialogue", new { action.Success, action.Reason });
                    if (!action.Success) return StepResult.Fail("장면/대화 넘기기 입력 실패: " + action.Reason);
                    await Task.Delay(Random.Shared.Next(250, 351), ct).ConfigureAwait(false);
                    continue;
                }
            }

            var result = await _detector.DetectAsync(shot, profile, "resultTouch", ct).ConfigureAwait(false);
            if (result.Matched && result.ClickBounds is Rectangle r)
            {
                LogDetection("resultTouch", result);
                var click = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                if (!click.Success) return StepResult.Fail("결과 화면 터치 실패: " + click.Reason);
                return await WaitForTargetAsync(hwnd, "retry", _config.App.Timeouts.RetrySeconds, ct).ConfigureAwait(false);
            }
            await CombatDelay(ct).ConfigureAwait(false);
        }
        return StepResult.Fail("전투 결과 대기 시간 초과");
    }

    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.RetrySeconds);
        var attempts = 0;
        while (DateTime.UtcNow < deadline && attempts < 4)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, "retry", ct).ConfigureAwait(false);
            LogDetection("retry", d);
            if (!d.Matched || d.ClickBounds is not Rectangle r)
            {
                await GeneralDelay(ct).ConfigureAwait(false);
                continue;
            }

            var action = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
            attempts++;
            _log.Write(_stage, "click", new { target = "retry", action.Success, action.Reason, attempts });
            if (!action.Success) return StepResult.Fail("다시 하기 클릭 실패: " + action.Reason);
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await IsGoneStableAsync(hwnd, "retry", ct).ConfigureAwait(false)) return StepResult.Ok();
        }
        return StepResult.Fail("다시 하기 버튼 처리 실패");
    }

    private async Task<StepResult> WaitForTargetAsync(nint hwnd, string target, int seconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, target, ct).ConfigureAwait(false);
            if (d.Matched) return StepResult.Ok();
            await GeneralDelay(ct).ConfigureAwait(false);
        }
        return StepResult.Fail($"{target} 화면 전환을 확인하지 못함");
    }

    private async Task<bool> IsGoneStableAsync(nint hwnd, string target, CancellationToken ct)
    {
        for (var i = 0; i < 2; i++)
        {
            using var shot = CaptureChecked(hwnd, out var profile);
            var d = await _detector.DetectAsync(shot, profile, target, ct).ConfigureAwait(false);
            if (d.Matched) return false;
            if (i == 0) await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return true;
    }

    private RunStage CompleteRound()
    {
        var now = DateTimeOffset.Now;
        var elapsed = now - _roundStarted;
        _roundTimes.Add(elapsed);
        _completed++;
        _roundStarted = now;
        var avg = TimeSpan.FromMilliseconds(_roundTimes.Average(x => x.TotalMilliseconds));
        MetricsChanged?.Invoke(_completed, now - _started, avg);
        SoundNotifier.Success();
        _log.Write(RunStage.Retry, "round-complete", new { completed = _completed, seconds = elapsed.TotalSeconds });
        return RunStage.WaitResult;
    }

    private Bitmap CaptureChecked(nint hwnd, out CaptureProfile profile)
    {
        var shot = _capture.Capture(hwnd);
        try
        {
            profile = ProfileResolver.Resolve(_config, shot.Width, shot.Height);
            return shot;
        }
        catch
        {
            shot.Dispose();
            throw;
        }
    }

    private async Task RecordFailureAsync(nint hwnd, RunStage stage, string reason)
    {
        using var shot = _capture.Capture(hwnd);
        var path = _diagnostics.Save(shot, stage, reason);
        _log.Write(stage, "failure", new { reason, screenshot = path });
        await Task.CompletedTask;
    }

    private void LogDetection(string target, DetectionEvidence d) => _log.Write(_stage, "detection", new
    {
        target, d.Matched, d.Score, d.ActualOcr, d.TemplateName, d.TemplateScore, d.ColorEvidence, d.ClickBounds, d.Reason
    });

    private Task GeneralDelay(CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(_config.App.Timeouts.PollGeneralMinMs, _config.App.Timeouts.PollGeneralMaxMs + 1), ct);

    private Task CombatDelay(CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(_config.App.Timeouts.PollCombatMinMs, _config.App.Timeouts.PollCombatMaxMs + 1), ct);

    private void SetStage(RunStage stage, string text)
    {
        _stage = stage;
        StatusChanged?.Invoke(stage, text);
        var elapsed = _started == default ? TimeSpan.Zero : DateTimeOffset.Now - _started;
        var avg = _roundTimes.Count == 0 ? null : TimeSpan.FromMilliseconds(_roundTimes.Average(x => x.TotalMilliseconds));
        MetricsChanged?.Invoke(_completed, elapsed, avg);
    }

    private static string StageText(RunStage stage) => stage switch
    {
        RunStage.OpenMenu => "메뉴 확인/열기",
        RunStage.SelectAbyss => "어비스 선택",
        RunStage.SelectDestination => "목적지 선택",
        RunStage.Enter => "입장하기 확인 및 SPACE",
        RunStage.WaitResult => "전투 결과 대기",
        RunStage.Retry => "다시 하기 확인",
        _ => stage.ToString()
    };

    public void Dispose() => _input.Dispose();
}
