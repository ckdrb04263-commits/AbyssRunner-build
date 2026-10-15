$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

$pattern = '(?s)    private async Task<StepResult> OtherDungeonAsync\(nint hwnd, CancellationToken ct\)\s*\{.*?\n    \}\s*\n(?=\s*    private async Task(?:<StepResult>)? (?:RecordLootIfAvailableAsync|RetryAsync))'
$replacement = @'
    private async Task<StepResult> OtherDungeonAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(_config.App.Timeouts.RetrySeconds, 300));
        var clickAttempts = 0;
        var consecutiveMatches = 0;
        var poll = 0;

        var destinationTarget = _activeDestination switch
        {
            Destination.Husang => "destinationHusang",
            Destination.Kwanggi => "destinationKwanggi",
            Destination.Moolgil => "destinationMoolgil",
            _ => "destinationHusang"
        };

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);

            // If the destination list is already visible, the transition succeeded even if
            // the result button disappeared before we could verify it.
            var destinationReady = await DetectTrackedAsync(shot, profile, destinationTarget, ct).ConfigureAwait(false);
            if (destinationReady.Matched)
            {
                _log.Write(_stage, "other-dungeon-destination-ready", new
                {
                    destinationTarget,
                    poll,
                    clickAttempts
                });
                return StepResult.Ok("다른 던전 화면 전환 완료 · 최초 목적지 재선택");
            }

            var other = await DetectTrackedAsync(shot, profile, "otherDungeon", ct).ConfigureAwait(false);
            if (!other.Matched)
            {
                consecutiveMatches = 0;
                poll++;

                if (poll % 12 == 0)
                {
                    _log.Write(_stage, "other-dungeon-wait", new
                    {
                        secondsRemaining = Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds),
                        clickAttempts,
                        ocr = other.ActualOcr,
                        template = other.TemplateScore
                    });
                }

                await Task.Delay(Random.Shared.Next(450, 651), ct).ConfigureAwait(false);
                continue;
            }

            consecutiveMatches++;
            LogDetection("otherDungeon", other);

            // The phrase is unique, but waiting for two consecutive samples prevents a one-frame OCR glitch.
            if (consecutiveMatches < 2)
            {
                await Task.Delay(180, ct).ConfigureAwait(false);
                continue;
            }

            if (other.ClickBounds is not Rectangle click)
            {
                consecutiveMatches = 0;
                await Task.Delay(300, ct).ConfigureAwait(false);
                continue;
            }

            if (clickAttempts >= 8)
                return StepResult.Fail("다른 던전 가기를 8회 시도했지만 화면이 전환되지 않음");

            clickAttempts++;
            var action = await _input.ClickAsync(click, hwnd, ct).ConfigureAwait(false);
            RememberInput("click:otherDungeon", action);
            _log.Write(_stage, "other-dungeon-click", new
            {
                clickAttempts,
                action.Success,
                action.WindowLocalPoint,
                action.Reason,
                other.ActualOcr,
                other.TemplateScore
            });

            if (!action.Success)
            {
                if (Win32.GetForegroundWindow() != hwnd)
                    return StepResult.Fail("다른 던전 가기 입력 실패: 게임 창이 활성 상태가 아님");

                consecutiveMatches = 0;
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            // UI transitions can be slow. Do not decide failure immediately after the click.
            var transitionDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < transitionDeadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(700, ct).ConfigureAwait(false);

                using var verify = CaptureChecked(hwnd, out var verifyProfile);

                var dest = await DetectTrackedAsync(verify, verifyProfile, destinationTarget, ct).ConfigureAwait(false);
                if (dest.Matched)
                {
                    _log.Write(_stage, "other-dungeon-transition-ok", new { clickAttempts, destinationTarget });
                    return StepResult.Ok("다른 던전 가기 처리 · 최초 목적지 화면 확인");
                }

                var still = await DetectTrackedAsync(verify, verifyProfile, "otherDungeon", ct).ConfigureAwait(false);
                if (still.Matched)
                {
                    // The click was swallowed or the button is still interactive.
                    if (still.ClickBounds is Rectangle retryBounds)
                        click = retryBounds;
                    break;
                }

                // Button disappeared but the destination screen is still loading: keep waiting here.
                _log.Write(_stage, "other-dungeon-transition-wait", new
                {
                    clickAttempts,
                    secondsRemaining = Math.Max(0, (transitionDeadline - DateTime.UtcNow).TotalSeconds)
                });
            }

            consecutiveMatches = 0;
        }

        return StepResult.Fail($"{Math.Max(_config.App.Timeouts.RetrySeconds, 300)}초 동안 다른 던전 가기 또는 목적지 화면을 확인하지 못함");
    }

'@

$updated = [regex]::Replace($s, $pattern, $replacement, 1)
if ($updated -eq $s) { throw 'Reliable OtherDungeonAsync replacement point not found' }
$s = $updated

# Keep result-action waiting aligned with the longer timeout and accurate log text.
$s = $s.Replace(
    'DateTime.UtcNow.AddSeconds(Math.Max(_config.App.Timeouts.RetrySeconds, 180))',
    'DateTime.UtcNow.AddSeconds(Math.Max(_config.App.Timeouts.RetrySeconds, 300))')
$s = $s.Replace(
    '버튼이 늦게 표시되는 경우까지 180초 기다렸지만 확인하지 못함',
    '버튼이 늦게 표시되는 경우까지 충분히 기다렸지만 확인하지 못함')

Set-Content $p $s -Encoding utf8
Write-Host 'Reliable late other-dungeon loop patch applied.'
