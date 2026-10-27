$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

# Replace result-touch click in WaitResultAsync with a wide, fixed safe-region click
# plus explicit verification and retry. This avoids clicking only the OCR glyph box.
$old = @'
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
'@

$new = @'
            var result = await DetectTrackedAsync(shot, profile, "resultTouch", ct).ConfigureAwait(false);
            if (result.Matched && result.ClickBounds is Rectangle detectedTouchBounds)
            {
                LogDetection("resultTouch", result);
                var tapBounds = profile.Regions.TryGetValue("resultTapSafe", out var safeTap)
                    ? safeTap.ToRectangle()
                    : detectedTouchBounds;

                for (var touchAttempt = 1; touchAttempt <= 3; touchAttempt++)
                {
                    var click = await _input.ClickAsync(tapBounds, hwnd, ct).ConfigureAwait(false);
                    RememberInput("click:resultTouch-safe", click);
                    _log.Write(_stage, "result-touch-safe-click", new
                    {
                        touchAttempt,
                        click.Success,
                        click.Reason,
                        click.WindowLocalPoint,
                        tapBounds,
                        ocrBounds = detectedTouchBounds,
                        result.ActualOcr,
                        result.Score
                    });

                    if (!click.Success)
                    {
                        if (Win32.GetForegroundWindow() != hwnd)
                            return StepResult.Fail("결과 화면 터치 실패: 게임 창 비활성");

                        await Task.Delay(350, ct).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(Random.Shared.Next(700, 951), ct).ConfigureAwait(false);
                    using var verify = CaptureChecked(hwnd, out var verifyProfile);

                    var retryAfterTap = await DetectTrackedAsync(verify, verifyProfile, "retry", ct).ConfigureAwait(false);
                    var otherAfterTap = await DetectTrackedAsync(verify, verifyProfile, "otherDungeon", ct).ConfigureAwait(false);
                    if (retryAfterTap.Matched || otherAfterTap.Matched)
                    {
                        _log.Write(_stage, "result-touch-transition-ok", new
                        {
                            touchAttempt,
                            retry = retryAfterTap.Matched,
                            otherDungeon = otherAfterTap.Matched
                        });
                        return StepResult.Ok("클리어 화면 터치 처리 완료");
                    }

                    var stillTouch = await DetectTrackedAsync(verify, verifyProfile, "resultTouch", ct).ConfigureAwait(false);
                    if (!stillTouch.Matched)
                    {
                        _log.Write(_stage, "result-touch-overlay-gone", new { touchAttempt });
                        return StepResult.Ok("클리어 화면 터치 처리 · 다음 화면 로딩 중");
                    }

                    await Task.Delay(300, ct).ConfigureAwait(false);
                }

                // Do not fail immediately. Keep monitoring and try the safe tap again on the next loop.
                _log.Write(_stage, "result-touch-still-visible", new { attempts = 3, result.ActualOcr });
                await Task.Delay(900, ct).ConfigureAwait(false);
                continue;
            }
'@

if (-not $s.Contains($old)) { throw 'WaitResult safe-tap patch point not found' }
$s = $s.Replace($old, $new)

# Use the same safe click region if the clear overlay is encountered later in RetryAsync.
$old2 = '                var touchClick = await _input.ClickAsync(touchBounds, hwnd, ct).ConfigureAwait(false);'
$new2 = @'
                var retryTapBounds = profile.Regions.TryGetValue("resultTapSafe", out var retrySafeTap)
                    ? retrySafeTap.ToRectangle()
                    : touchBounds;
                var touchClick = await _input.ClickAsync(retryTapBounds, hwnd, ct).ConfigureAwait(false);
'@
if (-not $s.Contains($old2)) { throw 'Retry-stage safe-tap patch point not found' }
$s = $s.Replace($old2, $new2)

$s = $s.Replace('RememberInput("click:resultTouch-from-retry", touchClick);', 'RememberInput("click:resultTouch-safe-from-retry", touchClick);')
$s = $s.Replace('_log.Write(_stage, "retry-stage-result-touch", new', '_log.Write(_stage, "retry-stage-result-touch-safe", new')

Set-Content $p $s -Encoding utf8
Write-Host 'Safe result-screen click patch applied.'
