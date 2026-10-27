$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

$methodStart = $s.IndexOf('    private async Task<StepResult> WaitResultAsync(nint hwnd, CancellationToken ct)')
if ($methodStart -lt 0) { throw 'WaitResultAsync not found' }

$nextCandidates = @(
    '    private async Task<StepResult> OtherDungeonAsync',
    '    private async Task<StepResult> WaitForResultActionAsync',
    '    private async Task<StepResult> RetryAsync'
)
$methodEnd = -1
foreach ($candidate in $nextCandidates) {
    $i = $s.IndexOf($candidate, $methodStart + 10)
    if ($i -ge 0 -and ($methodEnd -lt 0 -or $i -lt $methodEnd)) { $methodEnd = $i }
}
if ($methodEnd -lt 0) { throw 'WaitResultAsync end marker not found' }

$block = $s.Substring($methodStart, $methodEnd - $methodStart)

if (-not $block.Contains('result-direct-action-ready')) {
    $captureNeedle = '            using var shot = CaptureChecked(hwnd, out var profile);'
    $captureIndex = $block.IndexOf($captureNeedle)
    if ($captureIndex -lt 0) { throw 'WaitResult capture point not found' }

    $insertAt = $captureIndex + $captureNeedle.Length
    $insert = @'

            // The game can skip or very briefly show the "화면을 터치해 주세요" overlay.
            // If the next result action is already visible, treat combat as finished immediately.
            var expectedResultAction = _config.App.Features.OtherDungeonLoop ? "otherDungeon" : "retry";
            var directResultAction = await DetectTrackedAsync(shot, profile, expectedResultAction, ct).ConfigureAwait(false);
            if (directResultAction.Matched)
            {
                _log.Write(_stage, "result-direct-action-ready", new
                {
                    target = expectedResultAction,
                    directResultAction.ActualOcr,
                    directResultAction.TemplateScore,
                    directResultAction.Score
                });
                return StepResult.Ok("전투 종료 확인 · " + expectedResultAction + " 화면 이미 표시됨");
            }
'@
    $block = $block.Insert($insertAt, $insert)
}

# Improve diagnostics if a real timeout still happens.
$block = $block.Replace(
    'return StepResult.Fail("전투 결과 대기 시간 초과");',
    'return StepResult.Fail($"전투 결과를 {_config.App.Timeouts.CombatSeconds}초 동안 확인하지 못함");')

$s = $s.Substring(0, $methodStart) + $block + $s.Substring($methodEnd)
Set-Content $p $s -Encoding utf8
Write-Host 'Resilient combat-result detection patch applied.'
