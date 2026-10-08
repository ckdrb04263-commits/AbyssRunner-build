$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Install tracker source into reconstructed build tree.
Copy-Item (Join-Path $PSScriptRoot 'LootTracker.cs') (Join-Path $root 'work\src\AbyssRunner\Core\LootTracker.cs') -Force

# Patch automation engine to capture rewards once the result/retry screen is visible.
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

if (-not $s.Contains('private readonly LootTracker _lootTracker;')) {
    $needle = '    private readonly Detector _detector;'
    if (-not $s.Contains($needle)) { throw 'Loot field patch point not found' }
    $s = $s.Replace($needle, $needle + "`r`n    private readonly LootTracker _lootTracker;")
}

if (-not $s.Contains('private Destination _activeDestination;')) {
    $needle = '    private RunStage _stage = RunStage.Idle;'
    if (-not $s.Contains($needle)) { throw 'Loot destination field patch point not found' }
    $s = $s.Replace($needle, $needle + "`r`n    private Destination _activeDestination;")
}

if (-not $s.Contains('public event Action<LootSnapshot>? LootUpdated;')) {
    $needle = '    public event Action<int, TimeSpan, TimeSpan?>? MetricsChanged;'
    if (-not $s.Contains($needle)) { throw 'Loot event patch point not found' }
    $s = $s.Replace($needle, $needle + "`r`n    public event Action<LootSnapshot>? LootUpdated;")
}

if (-not $s.Contains('_lootTracker = new LootTracker(config.BaseDirectory);')) {
    $needle = '        _detector = new Detector(new KoreanOcr(), new TemplateMatcher(_imagesDir));'
    if (-not $s.Contains($needle)) { throw 'Loot constructor patch point not found' }
    $s = $s.Replace($needle, $needle + "`r`n        _lootTracker = new LootTracker(config.BaseDirectory);")
}

if (-not $s.Contains('_lootTracker.ResetSession();')) {
    $needle = '        _roundTimes.Clear();'
    if (-not $s.Contains($needle)) { throw 'Loot session patch point not found' }
    $s = $s.Replace($needle, $needle + "`r`n        _activeDestination = destination;`r`n        _lootTracker.ResetSession();")
}

if (-not $s.Contains('RecordLootIfAvailableAsync')) {
    $retryPattern = 'private async Task<StepResult> RetryAsync\(nint hwnd, CancellationToken ct\)\s*\{'
    $retryReplacement = @'
private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)
    {
        await RecordLootIfAvailableAsync(hwnd, ct).ConfigureAwait(false);
'@
    $updated = [regex]::Replace($s, $retryPattern, $retryReplacement, 1)
    if ($updated -eq $s) { throw 'Loot RetryAsync patch point not found' }
    $s = $updated

    $marker = '    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)'
    if (-not $s.Contains($marker)) { throw 'Loot helper insertion point not found' }

    $helper = @'
    private async Task RecordLootIfAvailableAsync(nint hwnd, CancellationToken ct)
    {
        try
        {
            using var shot = CaptureChecked(hwnd, out var profile);
            if (!profile.Regions.TryGetValue("lootRegion", out var lootRect))
            {
                _log.Write(_stage, "loot-skip", new { reason = "lootRegion 없음" });
                return;
            }

            var snapshot = await _lootTracker.CaptureAsync(
                shot,
                lootRect.ToRectangle(),
                _activeDestination,
                _completed + 1,
                ct).ConfigureAwait(false);

            LootUpdated?.Invoke(snapshot);
            _log.Write(_stage, "loot-recorded", new
            {
                round = snapshot.Round,
                destination = snapshot.Destination,
                items = snapshot.Recent.Select(x => new { x.Name, x.Quantity, x.Recognized }).ToArray(),
                totals = snapshot.Totals,
                snapshot.ScreenshotPath,
                rawOcr = snapshot.RawOcr
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Write(_stage, "loot-capture-failed", new { ex.Message });
        }
    }

'@
    $s = $s.Replace($marker, $helper + $marker)
}

Set-Content $p $s -Encoding utf8
Write-Host 'Loot tracking patch applied.'
