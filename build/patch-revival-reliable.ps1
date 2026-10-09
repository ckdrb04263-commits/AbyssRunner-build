$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

# Revival prerequisite: only the exact "여기서 부활" OCR target/ROI is required.
$validationPattern = 'if \(_config\.App\.Features\.RevivalAssist && \([^\r\n]*\)\)\s*throw new InvalidOperationException\("[^"]*부활[^"]*"\);'
$validationReplacement = @'
if (_config.App.Features.RevivalAssist && !Calibrated(profile, "revival", requireActualOcr: true))
            throw new InvalidOperationException("부활 보조를 켜려면 실제 화면의 '여기서 부활' OCR/ROI를 교정해야 합니다.");
'@
$updated = [regex]::Replace($s, $validationPattern, $validationReplacement, 1)
if ($updated -eq $s) {
    Write-Warning 'Revival validation block was not replaced; continuing because a previous workflow patch may have changed it.'
} else {
    $s = $updated
}

# Replace the runtime revival helper with text-only, two-sample confirmation and verified re-click.
$runtimePattern = '(?s)\s*if \(_config\.App\.Features\.RevivalAssist && DateTimeOffset\.Now - _lastRevival >= TimeSpan\.FromSeconds\(4\)\)\s*\{.*?\n\s*\}\s*\n\s*if \(_config\.App\.Features\.FoodAssist'
$runtimeReplacement = @'

            if (_config.App.Features.RevivalAssist && DateTimeOffset.Now - _lastRevival >= TimeSpan.FromSeconds(4))
            {
                var revival = await DetectTrackedAsync(shot, profile, "revival", ct).ConfigureAwait(false);
                if (revival.Matched && revival.ClickBounds is Rectangle initialBounds)
                {
                    // Require the text twice in a row so a one-frame OCR glitch cannot trigger a click.
                    await Task.Delay(140, ct).ConfigureAwait(false);
                    using var confirmShot = CaptureChecked(hwnd, out var confirmProfile);
                    var confirmed = await DetectTrackedAsync(confirmShot, confirmProfile, "revival", ct).ConfigureAwait(false);

                    if (confirmed.Matched && confirmed.ClickBounds is Rectangle confirmedBounds)
                    {
                        var clickBounds = confirmedBounds.Width > 1 && confirmedBounds.Height > 1 ? confirmedBounds : initialBounds;

                        for (var revivalAttempt = 1; revivalAttempt <= 3; revivalAttempt++)
                        {
                            var action = await _input.ClickAsync(clickBounds, hwnd, ct).ConfigureAwait(false);
                            RememberInput("click:revival", action);
                            _log.Write(_stage, "revival-text-click", new
                            {
                                revivalAttempt,
                                action.Success,
                                action.WindowLocalPoint,
                                action.Reason,
                                firstOcr = revival.ActualOcr,
                                confirmOcr = confirmed.ActualOcr
                            });

                            if (!action.Success)
                                return StepResult.Fail("부활 입력 실패: " + action.Reason);

                            _lastRevival = DateTimeOffset.Now;
                            await Task.Delay(Random.Shared.Next(550, 751), ct).ConfigureAwait(false);

                            using var verifyShot = CaptureChecked(hwnd, out var verifyProfile);
                            var stillThere = await DetectTrackedAsync(verifyShot, verifyProfile, "revival", ct).ConfigureAwait(false);
                            if (!stillThere.Matched)
                            {
                                _log.Write(_stage, "revival-success", new { revivalAttempt });
                                break;
                            }

                            if (revivalAttempt == 3)
                                return StepResult.Fail("'여기서 부활'을 3회 클릭했지만 화면이 전환되지 않음");

                            if (stillThere.ClickBounds is Rectangle retryBounds)
                                clickBounds = retryBounds;

                            await Task.Delay(250, ct).ConfigureAwait(false);
                        }

                        continue;
                    }
                }
            }

            if (_config.App.Features.FoodAssist
'@
$updated = [regex]::Replace($s, $runtimePattern, $runtimeReplacement, 1)
if ($updated -eq $s) { throw 'Text-only revival runtime block patch point not found' }
$s = $updated

Set-Content $p $s -Encoding utf8
Write-Host 'Reliable text-only revival patch applied.'
