using AbyssRunner.Config;
using AbyssRunner.Core;
using AbyssRunner.Input;
using AbyssRunner.Interop;
using AbyssRunner.Logging;
using AbyssRunner.Vision;

namespace AbyssRunner.UI;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0xA810;
    private readonly LoadedConfig _config;
    private readonly string _baseDir;
    private readonly ComboBox _windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _keyboard = new() { Minimum = 1, Maximum = 10, Width = 55 };
    private readonly NumericUpDown _mouse = new() { Minimum = 11, Maximum = 20, Width = 55 };
    private readonly TextBox _uiScale = new() { Width = 90 };
    private readonly CheckBox _food = new() { Text = "음식 보조" };
    private readonly CheckBox _revival = new() { Text = "부활 보조" };
    private readonly CheckBox _reconnect = new() { Text = "재접속 보조" };
    private readonly CheckBox _autoResume = new() { Text = "오류 후 10분 자동 재개" };
    private readonly Button _refresh = new() { Text = "창 새로고침", AutoSize = true };
    private readonly Button _calibrate = new() { Text = "템플릿 캡처/교정", AutoSize = true };
    private readonly Button _recognize = new() { Text = "저장 화면/인식 테스트", AutoSize = true };
    private readonly Button _diagnose = new() { Text = "Interception 진단", AutoSize = true };
    private readonly Button _start = new() { Text = "시작", Width = 100, Height = 34 };
    private readonly Button _stop = new() { Text = "정지 (F10)", Width = 110, Height = 34, Enabled = false };
    private readonly Label _stage = new() { AutoSize = true, Text = "현재 단계: 대기" };
    private readonly Label _count = new() { AutoSize = true, Text = "완료: 0" };
    private readonly Label _elapsed = new() { AutoSize = true, Text = "경과: 00:00:00" };
    private readonly Label _average = new() { AutoSize = true, Text = "평균: -" };
    private readonly Label _profile = new() { AutoSize = true, Text = "프로필: -" };
    private readonly RichTextBox _logBox = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9f), Visible = false };
    private readonly CheckBox _showLog = new() { Text = "상세 로그 펼치기", AutoSize = true };
    private CancellationTokenSource? _cts;
    private AutomationEngine? _engine;
    private RollingLogger? _logger;
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 1000 };
    private DateTimeOffset? _uiRunStarted;
    private bool _resumeOnNextStart;
    private bool _hotkeyRegistered;

    public MainForm(LoadedConfig config, string baseDir)
    {
        _config = config;
        _baseDir = baseDir;
        Text = "Mabinogi Mobile Abyss Runner";
        Width = 820;
        Height = 560;
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _destination.Items.AddRange(new object[] { new DestItem("허상의 정박지", Destination.Husang), new DestItem("광기의 동굴", Destination.Kwanggi), new DestItem("흩어진 물길", Destination.Moolgil) });
        _destination.SelectedIndex = 0;
        _keyboard.Value = Math.Clamp(_config.App.Input.KeyboardDevice, 1, 10);
        _mouse.Value = Math.Clamp(_config.App.Input.MouseDevice, 11, 20);
        _uiScale.Text = _config.App.UiScaleLabel;
        _food.Checked = _config.App.Features.FoodAssist;
        _revival.Checked = _config.App.Features.RevivalAssist;
        _reconnect.Checked = _config.App.Features.ReconnectAssist;
        _autoResume.Checked = _config.App.AutoResume.Enabled;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var logRowStyle = new RowStyle(SizeType.Absolute, 0);
        root.RowStyles.Add(logRowStyle);

        var windowRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        windowRow.Controls.AddRange(new Control[] { new Label { Text = "게임 창", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _windows, _refresh, _calibrate, _recognize });

        var configRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        configRow.Controls.AddRange(new Control[] {
            new Label { Text = "목적지", AutoSize = true, Padding = new Padding(0,7,0,0) }, _destination,
            new Label { Text = "UI 배율 기록", AutoSize = true, Padding = new Padding(12,7,0,0) }, _uiScale,
            new Label { Text = "키보드 ID", AutoSize = true, Padding = new Padding(12,7,0,0) }, _keyboard,
            new Label { Text = "마우스 ID", AutoSize = true, Padding = new Padding(12,7,0,0) }, _mouse });

        var featureRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        featureRow.Controls.AddRange(new Control[] { _food, _revival, _reconnect, _autoResume, _diagnose });

        var actionRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actionRow.Controls.AddRange(new Control[] { _start, _stop, _stage, _count, _elapsed, _average, _profile, _showLog });

        root.Controls.Add(windowRow, 0, 0);
        root.Controls.Add(configRow, 0, 1);
        root.Controls.Add(featureRow, 0, 2);
        root.Controls.Add(actionRow, 0, 3);
        root.Controls.Add(_logBox, 0, 4);
        Controls.Add(root);

        _refresh.Click += (_, _) => RefreshWindows();
        _windows.SelectedIndexChanged += (_, _) => UpdateProfileLabel();
        _destination.SelectedIndexChanged += (_, _) => { if (_cts is null) _resumeOnNextStart = false; };
        _calibrate.Click += (_, _) => OpenCalibration();
        _recognize.Click += (_, _) => OpenRecognitionTest();
        _diagnose.Click += (_, _) => DiagnoseInput();
        _start.Click += async (_, _) => await StartRunAsync();
        _stop.Click += (_, _) => StopRun();
        _showLog.CheckedChanged += (_, _) =>
        {
            _logBox.Visible = _showLog.Checked;
            logRowStyle.SizeType = _showLog.Checked ? SizeType.Percent : SizeType.Absolute;
            logRowStyle.Height = _showLog.Checked ? 100 : 0;
        };
        FormClosing += (_, _) => StopRun();
        _metricsTimer.Tick += (_, _) =>
        {
            if (_uiRunStarted is DateTimeOffset started && _cts is not null)
                _elapsed.Text = $"경과: {DateTimeOffset.Now - started:hh\\:mm\\:ss}";
        };
        _metricsTimer.Start();

        RefreshWindows();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = Win32.RegisterHotKey(Handle, HotkeyId, Win32.MOD_NOREPEAT, Win32.VK_F10);
        if (!_hotkeyRegistered)
            _stage.Text = "현재 단계: 경고 — F10 전역 단축키 등록 실패(다른 앱 점유 가능), 정지 버튼은 사용 가능";
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeyRegistered) Win32.UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) StopRun();
        base.WndProc(ref m);
    }

    private void RefreshWindows()
    {
        var current = (_windows.SelectedItem as WindowCandidate)?.Handle;
        _windows.Items.Clear();
        foreach (var w in Win32.EnumerateVisibleWindows().Where(x => x.Handle != Handle)) _windows.Items.Add(w);
        var idx = -1;
        if (current is not null)
        {
            for (var i = 0; i < _windows.Items.Count; i++)
            {
                if ((_windows.Items[i] as WindowCandidate)?.Handle == current) { idx = i; break; }
            }
        }
        if (idx >= 0) _windows.SelectedIndex = idx;
        else if (_windows.Items.Count > 0) _windows.SelectedIndex = 0;
    }

    private void UpdateProfileLabel()
    {
        if (_windows.SelectedItem is not WindowCandidate w) { _profile.Text = "프로필: -"; return; }
        var exact = _config.Targets.Profiles.FirstOrDefault(kv => kv.Value.CaptureWidth == w.Bounds.Width && kv.Value.CaptureHeight == w.Bounds.Height);
        _profile.Text = exact.Value is null ? $"프로필: 없음 ({w.Bounds.Width}×{w.Bounds.Height}, 교정 필요)" : $"프로필: {exact.Key} ({w.Bounds.Width}×{w.Bounds.Height})";
    }

    private void OpenCalibration()
    {
        if (_windows.SelectedItem is not WindowCandidate w) return;
        using var form = new CalibrationForm(_config, w.Handle);
        form.ShowDialog(this);
    }

    private void OpenRecognitionTest()
    {
        if (_windows.SelectedItem is not WindowCandidate w) return;
        using var form = new RecognitionTestForm(_config, w.Handle);
        form.ShowDialog(this);
    }

    private void DiagnoseInput()
    {
        var dll = Path.Combine(_baseDir, "interception.dll");
        if (!File.Exists(dll))
        {
            MessageBox.Show(this, "실행 파일 옆에 interception.dll이 없습니다. DLL만 복사하는 것으로 드라이버 설치가 완료되는 것은 아닙니다.", "입력 진단", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            using var input = new InterceptionInputController(_config.App.Input);
            var d = input.Diagnose();
            var keyboards = string.Join("\n", d.Keyboards.Select(x => $"K{x.Id}: {x.HardwareId}"));
            var mice = string.Join("\n", d.Mice.Select(x => $"M{x.Id}: {x.HardwareId}"));
            MessageBox.Show(this, d.Summary + "\n\n" + keyboards + "\n" + mice, "Interception 진단");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Interception 진단 실패", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task StartRunAsync()
    {
        if (_cts is not null || _windows.SelectedItem is not WindowCandidate w || _destination.SelectedItem is not DestItem dest) return;
        InterceptionInputController? pendingInput = null;
        try
        {
            _config.App.Input.KeyboardDevice = (int)_keyboard.Value;
            _config.App.Input.MouseDevice = (int)_mouse.Value;
            _config.App.UiScaleLabel = _uiScale.Text.Trim();
            _config.App.Features.FoodAssist = _food.Checked;
            _config.App.Features.RevivalAssist = _revival.Checked;
            _config.App.Features.ReconnectAssist = _reconnect.Checked;
            _config.App.AutoResume.Enabled = _autoResume.Checked;
            ConfigStore.SaveScenario(_config);

            using (var shot = new WindowCapture().Capture(w.Handle))
                _ = ProfileResolver.Resolve(_config, shot.Width, shot.Height);

            if (!File.Exists(Path.Combine(_baseDir, "interception.dll")))
                throw new InvalidOperationException("interception.dll이 실행 파일 옆에 없습니다.");

            pendingInput = new InterceptionInputController(_config.App.Input);
            var diagnostic = pendingInput.Diagnose();
            if (!diagnostic.Ready(_config.App.Input.KeyboardDevice, _config.App.Input.MouseDevice))
            {
                pendingInput.Dispose();
                pendingInput = null;
                throw new InvalidOperationException("Interception 준비 상태가 아닙니다: " + diagnostic.Summary + "\n관리자 권한, 드라이버 설치, 선택 장치 ID를 확인하세요.");
            }

            _logger = new RollingLogger(Path.Combine(_baseDir, "logs"), _config.App.Logging);
            _logger.LineWritten += line => Ui(() => { _logBox.AppendText(line + Environment.NewLine); if (_logBox.TextLength > 120_000) _logBox.Text = _logBox.Text[^80_000..]; });
            _engine = new AutomationEngine(_config, pendingInput, _logger, new DiagnosticStore(Path.Combine(_baseDir, "diagnostics"), _config.App.Logging));
            pendingInput = null;
            _engine.StatusChanged += (stage, text) => Ui(() =>
            {
                _stage.Text = $"현재 단계: {text}";
                if (stage == RunStage.ErrorPaused) _resumeOnNextStart = true;
                else if (stage == RunStage.Stopped) _resumeOnNextStart = false;
            });
            _engine.MetricsChanged += (count, elapsed, average) => Ui(() =>
            {
                _count.Text = $"완료: {count}";
                _elapsed.Text = $"경과: {elapsed:hh\\:mm\\:ss}";
                _average.Text = average is null ? "평균: -" : $"평균: {average.Value:mm\\:ss}";
            });

            _cts = new CancellationTokenSource();
            _uiRunStarted = DateTimeOffset.Now;
            var recoverCurrentState = _resumeOnNextStart;
            _resumeOnNextStart = false;
            SetRunningUi(true);
            Win32.SetForegroundWindow(w.Handle);
            await _engine.RunAsync(w.Handle, dest.Value, recoverCurrentState, _cts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "시작 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            pendingInput?.Dispose();
            _engine?.Dispose();
            _engine = null;
            _cts?.Dispose();
            _cts = null;
            _uiRunStarted = null;
            SetRunningUi(false);
        }
    }

    private void StopRun()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private void SetRunningUi(bool running)
    {
        Ui(() =>
        {
            _start.Enabled = !running;
            _stop.Enabled = running;
            _destination.Enabled = !running;
            _windows.Enabled = !running;
            _keyboard.Enabled = !running;
            _mouse.Enabled = !running;
            _uiScale.Enabled = !running;
            _food.Enabled = !running;
            _revival.Enabled = !running;
            _reconnect.Enabled = !running;
            _autoResume.Enabled = !running;
            _refresh.Enabled = !running;
            _calibrate.Enabled = !running;
            _recognize.Enabled = !running;
            _diagnose.Enabled = !running;
        });
    }

    private void Ui(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }

    private sealed record DestItem(string Name, Destination Value)
    {
        public override string ToString() => Name;
    }
}
