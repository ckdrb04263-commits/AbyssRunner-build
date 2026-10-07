$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Stabilize Interception mouse clicks: center-biased point, longer settle/hold, and cursor checks.
$p = Join-Path $root 'work\src\AbyssRunner\Input\InterceptionInputController.cs'
$s = Get-Content $p -Raw

if (-not $s.Contains('PickStablePointInside')) {
  $s = $s.Replace('var p = PickPointInside(windowLocalBounds);', 'var p = PickStablePointInside(windowLocalBounds);')
  $s = $s.Replace('await Task.Delay(35, ct).ConfigureAwait(false);', 'await Task.Delay(Random.Shared.Next(180, 261), ct).ConfigureAwait(false);')

  $oldHold = 'await Task.Delay(Random.Shared.Next(_config.MouseHoldMinMs, _config.MouseHoldMaxMs + 1), ct).ConfigureAwait(false);'
  $newHold = @'
            var stableHoldMin = Math.Max(_config.MouseHoldMinMs, 90);
            var stableHoldMax = Math.Max(stableHoldMin, Math.Max(_config.MouseHoldMaxMs, 140));
            await Task.Delay(Random.Shared.Next(stableHoldMin, stableHoldMax + 1), ct).ConfigureAwait(false);

            if (!Win32.GetCursorPos(out var beforeUp))
            {
                var emergencyUp = new InterceptionNative.MouseStroke { State = InterceptionNative.MouseLeftUp };
                try { InterceptionNative.SendMouse(_context, _config.MouseDevice, ref emergencyUp, 1); } catch { }
                _mouseHeld = false;
                return InputActionResult.Fail("mouse-up 직전 커서 위치 확인 실패", p);
            }

            var upDx = beforeUp.X - screen.X;
            var upDy = beforeUp.Y - screen.Y;
            if (Math.Sqrt(upDx * upDx + upDy * upDy) > _config.CursorTolerancePx)
            {
                var emergencyUp = new InterceptionNative.MouseStroke { State = InterceptionNative.MouseLeftUp };
                try { InterceptionNative.SendMouse(_context, _config.MouseDevice, ref emergencyUp, 1); } catch { }
                _mouseHeld = false;
                return InputActionResult.Fail("클릭 중 사용자 이동/커서 목표 이탈", p);
            }
'@
  if (-not $s.Contains($oldHold)) { throw 'Mouse hold patch point not found' }
  $s = $s.Replace($oldHold, $newHold)

  $oldReturn = '            _mouseHeld = false;' + "`r`n" + '            return ok ? InputActionResult.Ok(p) : InputActionResult.Fail("mouse-up 전달 실패", p);'
  if (-not $s.Contains($oldReturn)) {
    $oldReturn = '            _mouseHeld = false;' + "`n" + '            return ok ? InputActionResult.Ok(p) : InputActionResult.Fail("mouse-up 전달 실패", p);'
  }
  if (-not $s.Contains($oldReturn)) { throw 'Mouse-up return patch point not found' }

  $newReturn = @'
            _mouseHeld = false;
            if (!ok) return InputActionResult.Fail("mouse-up 전달 실패", p);
            await Task.Delay(Random.Shared.Next(120, 181), ct).ConfigureAwait(false);
            return InputActionResult.Ok(p);
'@
  $s = $s.Replace($oldReturn, $newReturn)

  $marker = '    private static Point PickPointInside(Rectangle r)'
  if (-not $s.Contains($marker)) { throw 'PickPointInside marker not found' }
  $helper = @'
    private static Point PickStablePointInside(Rectangle r)
    {
        if (r.Width <= 1 || r.Height <= 1) return new Point(r.Left, r.Top);

        var centerX = r.Left + r.Width / 2;
        var centerY = r.Top + r.Height / 2;
        var jitterX = Math.Max(1, Math.Min(6, r.Width / 20));
        var jitterY = Math.Max(1, Math.Min(6, r.Height / 20));

        var x = centerX + Random.Shared.Next(-jitterX, jitterX + 1);
        var y = centerY + Random.Shared.Next(-jitterY, jitterY + 1);

        x = Math.Clamp(x, r.Left + 1, Math.Max(r.Left + 1, r.Right - 2));
        y = Math.Clamp(y, r.Top + 1, Math.Max(r.Top + 1, r.Bottom - 2));
        return new Point(x, y);
    }

'@
  $s = $s.Replace($marker, $helper + $marker)
}
Set-Content $p $s -Encoding utf8

# If the result-touch click is visibly swallowed, re-detect and click again up to 3 times.
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw
if (-not $s.Contains('result-touch-verified-retry')) {
  $pattern = '(?s)var click = await _input\.ClickAsync\(r, hwnd, ct\)\.ConfigureAwait\(false\);\s*if \(!click\.Success\) return StepResult\.Fail\("결과 화면 터치 실패: " \+ click\.Reason\);\s*return await WaitForTargetAsync\(hwnd, "retry", _config\.App\.Timeouts\.RetrySeconds, ct\)\.ConfigureAwait\(false\);'
  $replacement = @'
                for (var clickAttempt = 1; clickAttempt <= 3; clickAttempt++)
                {
                    var click = await _input.ClickAsync(r, hwnd, ct).ConfigureAwait(false);
                    _log.Write(_stage, "result-touch-verified-retry", new { clickAttempt, click.Success, click.Reason, click.WindowLocalPoint });

                    if (!click.Success)
                    {
                        if (Win32.GetForegroundWindow() != hwnd)
                            return StepResult.Fail("결과 화면 터치 실패: " + click.Reason);

                        await Task.Delay(300, ct).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(Random.Shared.Next(550, 751), ct).ConfigureAwait(false);
                    using var verify = CaptureChecked(hwnd, out var verifyProfile);

                    var next = await _detector.DetectAsync(verify, verifyProfile, "retry", ct).ConfigureAwait(false);
                    if (next.Matched)
                        return StepResult.Ok("결과 화면 터치 처리");

                    var still = await _detector.DetectAsync(verify, verifyProfile, "resultTouch", ct).ConfigureAwait(false);
                    if (!still.Matched)
                        return await WaitForTargetAsync(hwnd, "retry", _config.App.Timeouts.RetrySeconds, ct).ConfigureAwait(false);

                    await Task.Delay(300, ct).ConfigureAwait(false);
                }

                return StepResult.Fail("결과 화면 터치가 3회 연속 처리되지 않음");
'@
  $updated = [regex]::Replace($s, $pattern, $replacement, 1)
  if ($updated -eq $s) { throw 'Result-touch retry patch point not found' }
  $s = $updated
}
Set-Content $p $s -Encoding utf8

Write-Host 'Stable click + verified retry patch applied.'
