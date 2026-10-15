$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

# Anywhere the result flow used to wait specifically for "retry", wait for the active result action instead.
$s = $s.Replace(
    'WaitForTargetAsync(hwnd, "retry", _config.App.Timeouts.RetrySeconds, ct)',
    'WaitForResultActionAsync(hwnd, ct)')

# Do not stop just because the result touch is still visible after the three quick click checks.
$s = $s.Replace(
    'return StepResult.Fail("결과 화면 터치가 3회 연속 처리되지 않음");',
    'return await WaitForResultActionAsync(hwnd, ct).ConfigureAwait(false);')

# Fix old hard-coded timeout wording for the other-dungeon branch.
$s = $s.Replace(
    'return StepResult.Fail("60초 안에 다른 던전 가기 버튼을 확인/처리하지 못함");',
    'return StepResult.Fail($"{_config.App.Timeouts.RetrySeconds}초 안에 다른 던전 가기 버튼을 확인/처리하지 못함");')

if (-not $s.Contains('private async Task<StepResult> WaitForResultActionAsync')) {
    $marker = '    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)'
    if (-not $s.Contains($marker)) { throw 'Result action wait insertion point not found' }

    $helper = @'
    private async Task<StepResult> WaitForResultActionAsync(nint hwnd, CancellationToken ct)
    {
        var target = _config.App.Features.OtherDungeonLoop ? "otherDungeon" : "retry";
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(_config.App.Timeouts.RetrySeconds, 180));
        var lastResultTouchRetry = DateTimeOffset.MinValue;
        var resultTouchRetryCount = 0;
        var poll = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);

            var action = await DetectTrackedAsync(shot, profile, target, ct).ConfigureAwait(false);
            if (action.Matched)
            {
                _log.Write(_stage, "result-action-ready", new
                {
                    target,
                    action.ActualOcr,
                    action.TemplateScore,
                    waitedPolls = poll
                });
                return StepResult.Ok(target + " 준비 완료");
            }

            // If the result-touch overlay is still around, retry it slowly rather than failing fast.
            // This covers both a swallowed click and UI transitions where the action buttons render late.
            var resultTouch = await DetectTrackedAsync(shot, profile, "resultTouch", ct).ConfigureAwait(false);
            if (resultTouch.Matched &&
                resultTouch.ClickBounds is Rectangle touchBounds &&
                resultTouchRetryCount < 6 &&
                DateTimeOffset.Now - lastResultTouchRetry >= TimeSpan.FromSeconds(2))
            {
                var click = await _input.ClickAsync(touchBounds, hwnd, ct).ConfigureAwait(false);
                RememberInput("click:resultTouch-delayed", click);
                _log.Write(_stage, "result-touch-delayed-retry", new
                {
                    target,
                    attempt = resultTouchRetryCount + 1,
                    click.Success,
                    click.WindowLocalPoint,
                    click.Reason
                });

                if (!click.Success && Win32.GetForegroundWindow() != hwnd)
                    return StepResult.Fail("결과 화면 재입력 중 게임 창 비활성");

                resultTouchRetryCount++;
                lastResultTouchRetry = DateTimeOffset.Now;
                await Task.Delay(700, ct).ConfigureAwait(false);
                continue;
            }

            poll++;
            if (poll % 10 == 0)
            {
                _log.Write(_stage, "result-action-wait", new
                {
                    target,
                    secondsRemaining = Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds),
                    resultTouchVisible = resultTouch.Matched
                });
            }

            await Task.Delay(Random.Shared.Next(420, 621), ct).ConfigureAwait(false);
        }

        return StepResult.Fail($"{target} 버튼이 늦게 표시되는 경우까지 180초 기다렸지만 확인하지 못함");
    }

'@
    $s = $s.Replace($marker, $helper + $marker)
}

Set-Content $p $s -Encoding utf8
Write-Host 'Delayed result-button wait patch applied.'
