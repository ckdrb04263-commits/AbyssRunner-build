$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 1) Config model: add live-toggle feature flag.
$p = Join-Path $root 'work\src\AbyssRunner\Config\ConfigModels.cs'
$s = Get-Content $p -Raw
if (-not $s.Contains('public bool OtherDungeonLoop')) {
  $needle = '    public bool SkipDialogueAssist { get; set; } = true;'
  if (-not $s.Contains($needle)) { throw 'FeatureConfig patch point not found' }
  $s = $s.Replace($needle, $needle + "`r`n    public bool OtherDungeonLoop { get; set; } = false;")
  Set-Content $p $s -Encoding utf8
}

# 2) Engine: on the result button stage optionally use '다른 던전 가기', then return to SelectDestination.
$p = Join-Path $root 'work\src\AbyssRunner\Core\AutomationEngine.cs'
$s = Get-Content $p -Raw

$transitionOld = '                    RunStage.Retry => CompleteRoundAndReturnToWait(),'
$transitionNew = '                    RunStage.Retry => _config.App.Features.OtherDungeonLoop ? CompleteRoundAndReturnToDestination() : CompleteRoundAndReturnToWait(),'
if (-not $s.Contains($transitionNew)) {
  if (-not $s.Contains($transitionOld)) { throw 'Retry transition patch point not found' }
  $s = $s.Replace($transitionOld, $transitionNew)
}

$retryMarker = '    private async Task<StepResult> RetryAsync(nint hwnd, CancellationToken ct)'
if (-not $s.Contains('return await OtherDungeonAsync(hwnd, ct).ConfigureAwait(false);')) {
  if (-not $s.Contains($retryMarker)) { throw 'RetryAsync patch point not found' }
  $s = $s.Replace($retryMarker + "`r`n    {", $retryMarker + "`r`n    {`r`n        if (_config.App.Features.OtherDungeonLoop)`r`n            return await OtherDungeonAsync(hwnd, ct).ConfigureAwait(false);")
  if (-not $s.Contains('return await OtherDungeonAsync(hwnd, ct).ConfigureAwait(false);')) {
    $s = $s.Replace($retryMarker + "`n    {", $retryMarker + "`n    {`n        if (_config.App.Features.OtherDungeonLoop)`n            return await OtherDungeonAsync(hwnd, ct).ConfigureAwait(false);")
  }
}

if (-not $s.Contains('private async Task<StepResult> OtherDungeonAsync')) {
  $otherMethod = @'
    private async Task<StepResult> OtherDungeonAsync(nint hwnd, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_config.App.Timeouts.RetrySeconds);
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            using var shot = CaptureChecked(hwnd, out var profile);
            var other = await DetectTrackedAsync(shot, profile, "otherDungeon", ct).ConfigureAwait(false);
            if (!other.Matched)
            {
                await GeneralPollDelay(ct).ConfigureAwait(false);
                continue;
            }

            LogDetection("otherDungeon", other);
            if (other.ClickBounds is not Rectangle click) return StepResult.Fail("다른 던전 가기 위치 없음");
            if (attempts >= 4) return StepResult.Fail("다른 던전 가기 재시도 한도 초과");
            attempts++;

            var action = await _input.ClickAsync(click, hwnd, ct).ConfigureAwait(false);
            RememberInput("click:otherDungeon", action);
            _log.Write(_stage, "click", new { target = "otherDungeon", action.Success, action.WindowLocalPoint, action.Reason, attempt = attempts });
            if (!action.Success) return StepResult.Fail("다른 던전 가기 입력 실패: " + action.Reason);

            await Task.Delay(Random.Shared.Next(450, 751), ct).ConfigureAwait(false);
            if (await IsGoneStableAsync(hwnd, "otherDungeon", 2, ct).ConfigureAwait(false))
                return StepResult.Ok("다른 던전 가기 처리 · 최초 목적지 재선택");
        }

        return StepResult.Fail("60초 안에 다른 던전 가기 버튼을 확인/처리하지 못함");
    }

'@
  if (-not $s.Contains($retryMarker)) { throw 'OtherDungeon method insertion point missing' }
  $s = $s.Replace($retryMarker, $otherMethod + $retryMarker)
}

$completeMarker = '    private RunStage CompleteRoundAndReturnToWait()'
if (-not $s.Contains('private RunStage CompleteRoundAndReturnToDestination()')) {
  $completeMethod = @'
    private RunStage CompleteRoundAndReturnToDestination()
    {
        _ = CompleteRoundAndReturnToWait();
        return RunStage.SelectDestination;
    }

'@
  if (-not $s.Contains($completeMarker)) { throw 'CompleteRound method patch point not found' }
  $s = $s.Replace($completeMarker, $completeMethod + $completeMarker)
}
Set-Content $p $s -Encoding utf8

# 3) UI: add toggle + modern dark dashboard styling.
$p = Join-Path $root 'work\src\AbyssRunner\UI\MainForm.cs'
$s = Get-Content $p -Raw

if (-not $s.Contains('private readonly CheckBox _otherDungeon')) {
  $needle = '    private readonly CheckBox _autoResume = new() { Text = "오류 후 10분 자동 재개" };'
  $insert = $needle + "`r`n    private readonly CheckBox _otherDungeon = new() { Text = `"다른 던전 반복`", Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Width = 150, Height = 32, FlatStyle = FlatStyle.Flat };"
  if (-not $s.Contains($needle)) { throw 'MainForm field patch point not found' }
  $s = $s.Replace($needle, $insert)
}

$start = '        Text = "Mabinogi Mobile Abyss Runner";'
$eventAnchor = '        _refresh.Click += (_, _) => RefreshWindows();'
$startIndex = $s.IndexOf($start)
$eventIndex = $s.IndexOf($eventAnchor)
if ($startIndex -lt 0 -or $eventIndex -lt 0 -or $eventIndex -le $startIndex) { throw 'MainForm constructor UI block not found' }

$newUi = @'
        Text = "AbyssRunner · Mabinogi Mobile";
        Width = 980;
        Height = 690;
        MinimumSize = new Size(880, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 23, 32);
        ForeColor = Color.FromArgb(239, 245, 250);
        Font = new Font("Segoe UI", 9.5f);

        _destination.Items.AddRange(new object[] { new DestItem("허상의 정박지", Destination.Husang), new DestItem("광기의 동굴", Destination.Kwanggi), new DestItem("흩어진 물길", Destination.Moolgil) });
        _destination.SelectedIndex = 0;
        _keyboard.Value = Math.Clamp(_config.App.Input.KeyboardDevice, 1, 10);
        _mouse.Value = Math.Clamp(_config.App.Input.MouseDevice, 11, 20);
        _uiScale.Text = _config.App.UiScaleLabel;
        _food.Checked = _config.App.Features.FoodAssist;
        _revival.Checked = _config.App.Features.RevivalAssist;
        _reconnect.Checked = _config.App.Features.ReconnectAssist;
        _autoResume.Checked = _config.App.AutoResume.Enabled;
        _otherDungeon.Checked = _config.App.Features.OtherDungeonLoop;

        foreach (var c in new Control[] { _windows, _destination, _keyboard, _mouse, _uiScale })
        {
            c.BackColor = Color.FromArgb(9, 16, 24);
            c.ForeColor = Color.FromArgb(236, 244, 250);
        }
        foreach (var b in new[] { _refresh, _calibrate, _recognize, _diagnose, _start, _stop })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(52, 67, 82);
            b.BackColor = Color.FromArgb(31, 43, 56);
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            b.Height = 34;
        }
        _start.BackColor = Color.FromArgb(22, 199, 132);
        _start.FlatAppearance.BorderSize = 0;
        _stop.BackColor = Color.FromArgb(177, 54, 64);
        _stop.FlatAppearance.BorderSize = 0;

        foreach (var cb in new[] { _food, _revival, _reconnect, _autoResume })
        {
            cb.ForeColor = Color.FromArgb(201, 213, 225);
            cb.BackColor = Color.FromArgb(25, 35, 48);
            cb.AutoSize = true;
        }
        _otherDungeon.FlatAppearance.BorderColor = Color.FromArgb(22, 199, 132);
        _otherDungeon.ForeColor = _otherDungeon.Checked ? Color.White : Color.FromArgb(170, 184, 198);
        _otherDungeon.BackColor = _otherDungeon.Checked ? Color.FromArgb(12, 99, 74) : Color.FromArgb(25, 35, 48);

        _stage.ForeColor = Color.FromArgb(236, 244, 250);
        _stage.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        foreach (var label in new[] { _count, _elapsed, _average, _profile }) label.ForeColor = Color.FromArgb(166, 182, 197);
        _showLog.ForeColor = Color.FromArgb(166, 182, 197);
        _showLog.BackColor = Color.Transparent;
        _logBox.BackColor = Color.FromArgb(8, 13, 20);
        _logBox.ForeColor = Color.FromArgb(199, 211, 222);
        _logBox.BorderStyle = BorderStyle.None;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(18), BackColor = Color.FromArgb(15, 23, 32) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var logRowStyle = new RowStyle(SizeType.Absolute, 0);
        root.RowStyles.Add(logRowStyle);

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(15, 23, 32) };
        var title = new Label { AutoSize = true, Text = "ABYSS RUNNER", ForeColor = Color.White, Font = new Font("Segoe UI", 20f, FontStyle.Bold), Location = new Point(2, 2) };
        var subtitle = new Label { AutoSize = true, Text = "1920×1080 · 화면 인식 기반 자동 반복 도우미", ForeColor = Color.FromArgb(139, 157, 174), Font = new Font("Segoe UI", 9f), Location = new Point(4, 41) };
        header.Controls.Add(title); header.Controls.Add(subtitle);

        FlowLayoutPanel MakeRow()
        {
            return new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 35, 48), Padding = new Padding(14, 12, 14, 12), Margin = new Padding(0, 0, 0, 10), WrapContents = true };
        }
        Label Cap(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(151, 167, 183), Padding = new Padding(0, 8, 4, 0), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };

        var windowRow = MakeRow();
        windowRow.Controls.AddRange(new Control[] { Cap("게임 창"), _windows, _refresh, _calibrate, _recognize });

        var configRow = MakeRow();
        configRow.Controls.AddRange(new Control[] { Cap("목적지"), _destination, Cap("UI 배율"), _uiScale, Cap("키보드 ID"), _keyboard, Cap("마우스 ID"), _mouse });

        var featureRow = MakeRow();
        featureRow.Controls.AddRange(new Control[] { Cap("보조 기능"), _food, _revival, _reconnect, _autoResume, _otherDungeon, _diagnose });

        var actionRow = MakeRow();
        actionRow.Controls.AddRange(new Control[] { _start, _stop, _stage, _count, _elapsed, _average, _profile, _showLog });

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(windowRow, 0, 1);
        root.Controls.Add(configRow, 0, 2);
        root.Controls.Add(featureRow, 0, 3);
        root.Controls.Add(actionRow, 0, 4);
        root.Controls.Add(_logBox, 0, 5);
        Controls.Add(root);

'@
$s = $s.Substring(0, $startIndex) + $newUi + $s.Substring($eventIndex)

$eventNeedle = '        _diagnose.Click += (_, _) => DiagnoseInput();'
if (-not $s.Contains('_otherDungeon.CheckedChanged')) {
  $eventInsert = $eventNeedle + @'
        _otherDungeon.CheckedChanged += (_, _) =>
        {
            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;
            _otherDungeon.ForeColor = _otherDungeon.Checked ? Color.White : Color.FromArgb(170, 184, 198);
            _otherDungeon.BackColor = _otherDungeon.Checked ? Color.FromArgb(12, 99, 74) : Color.FromArgb(25, 35, 48);
            try { ConfigStore.SaveScenario(_config); } catch { }
        };
'@
  $s = $s.Replace($eventNeedle, $eventInsert)
}

$saveNeedle = '            _config.App.Features.ReconnectAssist = _reconnect.Checked;'
if (-not $s.Contains($saveNeedle + "`r`n            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;") -and -not $s.Contains($saveNeedle + "`n            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;")) {
  $s = $s.Replace($saveNeedle, $saveNeedle + "`r`n            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;")
}

$enableNeedle = '            _autoResume.Enabled = !running;'
if (-not $s.Contains('_otherDungeon.Enabled = true;')) {
  $s = $s.Replace($enableNeedle, $enableNeedle + "`r`n            _otherDungeon.Enabled = true; // 실행 중에도 즉시 ON/OFF 가능")
}
Set-Content $p $s -Encoding utf8
Write-Host 'Other dungeon loop + modern UI patch applied.'
