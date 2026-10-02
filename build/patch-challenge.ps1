$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

$old1 = 'if (await IsGoneStableAsync(hwnd, "enter", 2, ct).ConfigureAwait(false)) return StepResult.Ok("사용자 동작/화면 전환으로 입장하기가 사라짐");'
$new1 = 'if (await IsGoneStableAsync(hwnd, "enter", 2, ct).ConfigureAwait(false)) { var confirm = await HandleOptionalChallengeConfirmAsync(hwnd, ct).ConfigureAwait(false); if (confirm.Outcome != StepOutcome.Success) return confirm; _roundStarted = DateTimeOffset.Now; return StepResult.Ok(confirm.Reason); }'
if (-not $s.Contains($old1)) { throw 'Enter early-transition patch point not found' }
$s = $s.Replace($old1, $new1)

$old2 = @'
            if (await IsGoneStableAsync(hwnd, "enter", 2, ct).ConfigureAwait(false))
            {
                _roundStarted = DateTimeOffset.Now;
                return StepResult.Ok();
            }
'@
$new2 = @'
            if (await IsGoneStableAsync(hwnd, "enter", 2, ct).ConfigureAwait(false))
            {
                var confirm = await HandleOptionalChallengeConfirmAsync(hwnd, ct).ConfigureAwait(false);
                if (confirm.Outcome != StepOutcome.Success) return confirm;
                _roundStarted = DateTimeOffset.Now;
                return StepResult.Ok(confirm.Reason);
            }
'@
if (-not $s.Contains($old2)) { throw 'Enter post-SPACE patch point not found' }
$s = $s.Replace($old2, $new2)

$old3 = 'if (await IsGoneStableAsync(hwnd, "retry", 2, ct).ConfigureAwait(false)) return StepResult.Ok();'
$new3 = 'if (await IsGoneStableAsync(hwnd, "retry", 2, ct).ConfigureAwait(false)) { var confirm = await HandleOptionalChallengeConfirmAsync(hwnd, ct).ConfigureAwait(false); if (confirm.Outcome != StepOutcome.Success) return confirm; return StepResult.Ok(confirm.Reason); }'
if (-not $s.Contains($old3)) { throw 'Retry patch point not found' }
$s = $s.Replace($old3, $new3)

$marker = '    private async Task<StepResult> RetrySameDetectedActionAsync(nint hwnd, string target, string next, int additionalAttempts, CancellationToken ct)'
if (-not $s.Contains($marker)) { throw 'Helper insertion point not found' }

$helper = @'
    private async Task<StepResult> HandleOptionalChallengeConfirmAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var attempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var confirm = await DetectTrackedAsync(shot, profile, "challengeConfirm", ct).ConfigureAwait(false);

            if (!confirm.Matched)
            {
                await Task.Delay(220, ct).ConfigureAwait(false);
                continue;
            }

            LogDetection("challengeConfirm", confirm);
            if (confirm.ClickBounds is not Rectangle click)
                return StepResult.Fail("도전하기 위치 없음");

            if (attempts >= 3)
                return StepResult.Fail("도전하기 재시도 한도 초과");

            attempts++;
            var action = await _input.ClickAsync(click, hwnd, ct).ConfigureAwait(false);
            RememberInput("click:challengeConfirm", action);
            _log.Write(_stage, "click", new { target = "challengeConfirm", action.Success, action.WindowLocalPoint, action.Reason, attempt = attempts });

            if (!action.Success)
                return StepResult.Fail("도전하기 입력 실패: " + action.Reason);

            await Task.Delay(Random.Shared.Next(350, 601), ct).ConfigureAwait(false);

            if (await IsGoneStableAsync(hwnd, "challengeConfirm", 2, ct).ConfigureAwait(false))
                return StepResult.Ok("도전 팝업 처리");
        }

        return StepResult.Ok("도전 팝업 없음");
    }

'@

if (-not $s.Contains('private async Task<StepResult> HandleOptionalChallengeConfirmAsync')) {
    $s = $s.Replace($marker, $helper + $marker)
}

Set-Content $p $s -Encoding utf8
Write-Host 'Challenge confirmation patch applied.'
