$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

$waitStart = $s.IndexOf('    private async Task<StepResult> WaitResultAsync(nint hwnd, CancellationToken ct)')
if ($waitStart -lt 0) { throw 'WaitResultAsync not found' }

$waitEnd = -1
foreach ($candidate in @(
    '    private async Task<StepResult> OtherDungeonAsync',
    '    private async Task<StepResult> WaitForResultActionAsync',
    '    private async Task<StepResult> RetryAsync'
)) {
    $i = $s.IndexOf($candidate, $waitStart + 10)
    if ($i -ge 0 -and ($waitEnd -lt 0 -or $i -lt $waitEnd)) { $waitEnd = $i }
}
if ($waitEnd -lt 0) { throw 'WaitResultAsync end marker not found' }

$wait = $s.Substring($waitStart, $waitEnd - $waitStart)

$wait = [regex]::Replace(
    $wait,
    '(?s)\s*// The game can skip or very briefly show the "화면을 터치해 주세요" overlay\..*?return StepResult\.Ok\("전투 종료 확인 · " \+ expectedResultAction \+ " 화면 이미 표시됨"\);\s*\}\s*',
    [Environment]::NewLine)

$oldPattern = '(?s)\s*var result = await DetectTrackedAsync\(shot, profile, "resultTouch", ct\)\.ConfigureAwait\(false\);\s*if \(result\.Matched && result\.ClickBounds is Rectangle touch\)\s*\{.*?\}\s*await CombatPollDelay\(ct\)\.ConfigureAwait\(false\);'
$newBlock = @'
            var retryVisible = await DetectTrackedAsync(shot, profile, "retry", ct).ConfigureAwait(false);
            var otherVisible = await DetectTrackedAsync(shot, profile, "otherDungeon", ct).ConfigureAwait(false);
            if (retryVisible.Matched || otherVisible.Matched)
            {
                var seen = retryVisible.Matched ? "retry" : "otherDungeon";
                _log.Write(_stage, "result-action-observed", new
                {
                    seen,
                    retryOcr = retryVisible.ActualOcr,
                    otherOcr = otherVisible.ActualOcr,
                    retryScore = retryVisible.Score,
                    otherScore = otherVisible.Score
                });
                return StepResult.Ok("전투 종료 확인 · 결과 버튼 감지");
            }

            var result = await DetectTrackedAsync(shot, profile, "resultTouch", ct).ConfigureAwait(false);
            if (result.Matched && result.ClickBounds is Rectangle r)
            {
                LogDetection("resultTouch", result);
                var click = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                RememberInput("click:resultTouch", click);
                _log.Write(_stage, "result-touch-click", new
                {
                    click.Success,
                    click.Reason,
                    click.WindowLocalPoint,
                    result.ActualOcr,
                    result.Score
                });

                if (!click.Success && Win32.GetForegroundWindow() != hwnd)
                    return StepResult.Fail("결과 화면 터치 실패: 게임 창 비활성");

                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            await CombatPollDelay(ct).ConfigureAwait(false);
'@
$updated = [regex]::Replace($wait, $oldPattern, $newBlock, 1)
if ($updated -eq $wait) {
    throw 'WaitResult result branch patch point not found'
}
$wait = $updated

$wait = $wait.Replace(
    'return StepResult.Fail($"전투 결과를 {_config.App.Timeouts.CombatSeconds}초 동안 확인하지 못함");',
    'return StepResult.Fail($"전투 결과 버튼을 {_config.App.Timeouts.CombatSeconds}초 동안 확인하지 못함");')

$s = $s.Substring(0, $waitStart) + $wait + $s.Substring($waitEnd)

$retryStart = $s.IndexOf('    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)')
if ($retryStart -lt 0) { throw 'RetryAsync not found' }
$retryEnd = -1
foreach ($candidate in @(
    '    private async Task<StepResult> RetrySameDetectedActionAsync',
    '    private async Task<bool> IsGoneOrNextStableAsync',
    '    private async Task<StepResult> WaitForTargetAsync',
    '    private async Task<bool> IsGoneStableAsync'
)) {
    $i = $s.IndexOf($candidate, $retryStart + 10)
    if ($i -ge 0 -and ($retryEnd -lt 0 -or $i -lt $retryEnd)) { $retryEnd = $i }
}
if ($retryEnd -lt 0) { throw 'RetryAsync end marker not found' }

$retryMethod = @'
    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)
    {
        if (_config.App.Features.OtherDungeonLoop)
            return await OtherDungeonAsync(hwnd, ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(_config.App.Timeouts.RetrySeconds, 300));
        var clickAttempts = 0;
        var consecutiveMatches = 0;
        var poll = 0;
        var lastTouchRetry = DateTimeOffset.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);

            var retry = await DetectTrackedAsync(shot, profile, "retry", ct).ConfigureAwait(false);
            if (retry.Matched)
            {
                consecutiveMatches++;
                LogDetection("retry", retry);

                if (consecutiveMatches < 2)
                {
                    await Task.Delay(160, ct).ConfigureAwait(false);
                    continue;
                }

                if (retry.ClickBounds is not Rectangle r)
                {
                    consecutiveMatches = 0;
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                if (clickAttempts >= 8)
                    return StepResult.Fail("다시 하기를 8회 시도했지만 화면이 전환되지 않음");

                clickAttempts++;
                var action = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                RememberInput("click:retry", action);
                _log.Write(_stage, "retry-click", new
                {
                    clickAttempts,
                    action.Success,
                    action.Reason,
                    action.WindowLocalPoint,
                    retry.ActualOcr,
                    retry.Score
                });

                if (!action.Success)
                {
                    if (Win32.GetForegroundWindow() != hwnd)
                        return StepResult.Fail("다시 하기 입력 실패: 게임 창 비활성");

                    consecutiveMatches = 0;
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(700, ct).ConfigureAwait(false);

                var gone = true;
                for (var stable = 0; stable < 2; stable++)
                {
                    using var verify = CaptureChecked(hwnd, out var verifyProfile);
                    var still = await DetectTrackedAsync(verify, verifyProfile, "retry", ct).ConfigureAwait(false);
                    if (still.Matched)
                    {
                        gone = false;
                        break;
                    }

                    if (stable == 0)
                        await Task.Delay(350, ct).ConfigureAwait(false);
                }

                if (gone)
                {
                    var confirm = await HandleOptionalChallengeConfirmAsync(hwnd, ct).ConfigureAwait(false);
                    if (confirm.Outcome != StepOutcome.Success) return confirm;

                    _log.Write(_stage, "retry-transition-ok", new { clickAttempts, confirm = confirm.Reason });
                    return StepResult.Ok(confirm.Reason);
                }

                consecutiveMatches = 0;
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            consecutiveMatches = 0;

            var touch = await DetectTrackedAsync(shot, profile, "resultTouch", ct).ConfigureAwait(false);
            if (touch.Matched &&
                touch.ClickBounds is Rectangle touchBounds &&
                DateTimeOffset.Now - lastTouchRetry >= TimeSpan.FromSeconds(2))
            {
                var touchClick = await _input.ClickAsync(touchBounds, hwnd, ct).ConfigureAwait(false);
                RememberInput("click:resultTouch-from-retry", touchClick);
                _log.Write(_stage, "retry-stage-result-touch", new
                {
                    touchClick.Success,
                    touchClick.Reason,
                    touchClick.WindowLocalPoint,
                    touch.ActualOcr
                });

                if (!touchClick.Success && Win32.GetForegroundWindow() != hwnd)
                    return StepResult.Fail("결과 화면 재입력 실패: 게임 창 비활성");

                lastTouchRetry = DateTimeOffset.Now;
                await Task.Delay(900, ct).ConfigureAwait(false);
                continue;
            }

            poll++;
            if (poll % 12 == 0)
            {
                _log.Write(_stage, "retry-wait", new
                {
                    secondsRemaining = Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds),
                    clickAttempts,
                    retryOcr = retry.ActualOcr,
                    touchVisible = touch.Matched
                });
            }

            await Task.Delay(Random.Shared.Next(450, 651), ct).ConfigureAwait(false);
        }

        return StepResult.Fail("다시 하기 버튼을 충분히 기다렸지만 확인하지 못함");
    }

'@

$s = $s.Substring(0, $retryStart) + $retryMethod + $s.Substring($retryEnd)
Set-Content $p $s -Encoding utf8
Write-Host 'Unified result + robust retry patch applied.'
