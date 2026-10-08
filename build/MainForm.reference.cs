using AbyssRunner.Config;
using AbyssRunner.Core;
using AbyssRunner.Input;
using AbyssRunner.Interop;
using AbyssRunner.Logging;
using AbyssRunner.Vision;
using System.Drawing.Drawing2D;

namespace AbyssRunner.UI;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0xA810;
    private readonly LoadedConfig _config;
    private readonly string _baseDir;

    private readonly ComboBox _windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 410, Height = 34 };
    private readonly ComboBox _destination = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Height = 34 };
    private readonly NumericUpDown _keyboard = new() { Minimum = 1, Maximum = 10, Width = 62 };
    private readonly NumericUpDown _mouse = new() { Minimum = 11, Maximum = 20, Width = 62 };
    private readonly TextBox _uiScale = new() { Width = 135, ReadOnly = true };

    private readonly ToggleSwitch _food = new("음식");
    private readonly ToggleSwitch _revival = new("부활");
    private readonly ToggleSwitch _reconnect = new("재접속");
    private readonly ToggleSwitch _autoResume = new("자동 재개");
    private readonly ToggleSwitch _otherDungeon = new("다른 던전 반복");

    private readonly Button _refresh = MakeButton("새로고침");
    private readonly Button _calibrate = MakeButton("템플릿 교정");
    private readonly Button _recognize = MakeButton("인식 테스트");
    private readonly Button _diagnose = MakeButton("Interception 진단");
    private readonly Button _start = MakeButton("▶  시작", true);
    private readonly Button _stop = MakeButton("■  정지   F10", false, true);

    private readonly Label _connection = MakeLabel("●  게임 연결 확인 중", 9, FontStyle.Bold, Color.FromArgb(26, 138, 89));
    private readonly Label _heroTitle = MakeLabel("실행할 준비가 되었어요", 23, FontStyle.Bold, Color.FromArgb(22, 29, 35));
    private readonly Label _heroSub = MakeLabel("게임 창과 목적지를 선택한 뒤 시작하세요.", 9.5f, FontStyle.Regular, Color.FromArgb(119, 129, 138));
    private readonly Label _nextAction = MakeLabel("다음 동작   메뉴 열기 → 어비스 선택 → 목적지 선택", 9.5f, FontStyle.Bold, Color.FromArgb(61, 77, 70));
    private readonly Label _countValue = MakeLabel("0판", 19, FontStyle.Bold, Color.FromArgb(18, 27, 32));
    private readonly Label _battleValue = MakeLabel("--:--", 19, FontStyle.Bold, Color.FromArgb(18, 27, 32));
    private readonly Label _averageValue = MakeLabel("--:--", 19, FontStyle.Bold, Color.FromArgb(18, 27, 32));
    private readonly Label _elapsedValue = MakeLabel("총 실행  00:00:00", 10, FontStyle.Bold, Color.FromArgb(92, 103, 112));
    private readonly Label _profile = MakeLabel("프로필: -", 8.5f, FontStyle.Regular, Color.FromArgb(126, 137, 146));
    private readonly Label _statusPill = MakeLabel("●  대기", 9, FontStyle.Bold, Color.FromArgb(46, 118, 87));

    private readonly ListBox _recent = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(80, 90, 98),
        Font = new Font("맑은 고딕", 8.7f),
        IntegralHeight = false
    };

    private readonly ListBox _lootRecent = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(43, 56, 63),
        Font = new Font("맑은 고딕", 9.2f, FontStyle.Bold),
        IntegralHeight = false
    };

    private readonly ListBox _lootTotals = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Color.White,
        ForeColor = Color.FromArgb(31, 112, 80),
        Font = new Font("맑은 고딕", 9.2f, FontStyle.Bold),
        IntegralHeight = false
    };

    private readonly Label _lootStatus = MakeLabel("보상 화면이 나오면 자동으로 OCR 기록합니다.", 8.2f, FontStyle.Regular, Color.FromArgb(133, 144, 151));

    private readonly RichTextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Font = new Font("Consolas", 8.5f),
        BackColor = Color.FromArgb(248, 250, 251),
        ForeColor = Color.FromArgb(75, 85, 95),
        BorderStyle = BorderStyle.None,
        Visible = false
    };

    private readonly CheckBox _showLog = new()
    {
        Text = "상세 로그",
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.FromArgb(107, 117, 126),
        BackColor = Color.Transparent
    };

    private readonly Panel[] _stageRows = new Panel[6];
    private readonly Label[] _stageDots = new Label[6];
    private readonly Label[] _stageTitles = new Label[6];

    private CancellationTokenSource? _cts;
    private AutomationEngine? _engine;
    private RollingLogger? _logger;
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 1000 };
    private DateTimeOffset? _uiRunStarted;
    private DateTimeOffset? _roundUiStarted;
    private bool _resumeOnNextStart;
    private bool _hotkeyRegistered;

    public MainForm(LoadedConfig config, string baseDir)
    {
        _config = config;
        _baseDir = baseDir;

        Text = "어비스 오토";
        Width = 1050;
        Height = 790;
        MinimumSize = new Size(980, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 249, 249);
        ForeColor = Color.FromArgb(25, 32, 37);
        Font = new Font("맑은 고딕", 9f);
        DoubleBuffered = true;

        _destination.Items.AddRange(new object[]
        {
            new DestItem("허상의 정박지", Destination.Husang),
            new DestItem("광기의 동굴", Destination.Kwanggi),
            new DestItem("흩어진 물길", Destination.Moolgil)
        });
        _destination.SelectedIndex = 0;
        _keyboard.Value = Math.Clamp(_config.App.Input.KeyboardDevice, 1, 10);
        _mouse.Value = Math.Clamp(_config.App.Input.MouseDevice, 11, 20);
        _uiScale.Text = _config.App.UiScaleLabel;
        _food.Checked = _config.App.Features.FoodAssist;
        _revival.Checked = _config.App.Features.RevivalAssist;
        _reconnect.Checked = _config.App.Features.ReconnectAssist;
        _autoResume.Checked = _config.App.AutoResume.Enabled;
        _otherDungeon.Checked = _config.App.Features.OtherDungeonLoop;

        StyleInput(_windows);
        StyleInput(_destination);
        StyleInput(_keyboard);
        StyleInput(_mouse);
        StyleInput(_uiScale);

        _start.Width = 150;
        _start.Height = 42;
        _stop.Width = 165;
        _stop.Height = 42;
        _stop.Enabled = false;

        BuildLayout();
        WireEvents();
        RefreshWindows();
        UpdateStageSidebar(RunStage.Idle);
        UpdateModeSummary();
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(247, 249, 249),
            Padding = new Padding(0)
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var side = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18, 22, 14, 16) };
        var logo = MakeLabel("◉  어비스 오토", 12, FontStyle.Bold, Color.FromArgb(24, 38, 42));
        logo.Location = new Point(16, 18);
        side.Controls.Add(logo);

        _connection.Location = new Point(18, 58);
        side.Controls.Add(_connection);

        var section = MakeLabel("진행 순서", 8, FontStyle.Bold, Color.FromArgb(126, 138, 146));
        section.Location = new Point(18, 104);
        side.Controls.Add(section);

        string[] titles = { "메뉴 열기", "어비스 선택", "목적지 선택", "입장하기", "전투 대기", "결과 · 반복" };
        string[] subs = { "게임 내 메뉴를 엽니다.", "어비스 메뉴를 선택합니다.", "선택한 목적지를 찾습니다.", "이동 및 도전을 진행합니다.", "전투 종료를 기다립니다.", "다음 판을 준비합니다." };
        for (var i = 0; i < titles.Length; i++)
        {
            var row = new Panel { Width = 180, Height = 58, BackColor = Color.White, Location = new Point(10, 130 + i * 62) };
            var dot = MakeLabel((i + 1).ToString(), 8.5f, FontStyle.Bold, Color.White);
            dot.TextAlign = ContentAlignment.MiddleCenter;
            dot.Size = new Size(25, 25);
            dot.Location = new Point(2, 13);
            dot.BackColor = Color.FromArgb(210, 218, 215);
            SetRoundRegion(dot, 12);

            var title = MakeLabel(titles[i], 9.2f, FontStyle.Bold, Color.FromArgb(45, 55, 61));
            title.Location = new Point(38, 6);
            var sub = MakeLabel(subs[i], 7.6f, FontStyle.Regular, Color.FromArgb(146, 155, 162));
            sub.Location = new Point(38, 29);

            row.Controls.Add(dot); row.Controls.Add(title); row.Controls.Add(sub);
            _stageRows[i] = row; _stageDots[i] = dot; _stageTitles[i] = title;
            side.Controls.Add(row);
        }

        var tip = MakeLabel("현재 단계가 자동으로 표시됩니다.\nF10은 언제든 긴급 정지입니다.", 7.8f, FontStyle.Regular, Color.FromArgb(143, 153, 160));
        tip.Location = new Point(18, 515);
        tip.MaximumSize = new Size(175, 50);
        side.Controls.Add(tip);

        var main = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 249), Padding = new Padding(22, 18, 22, 16) };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = main.BackColor };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = main.BackColor, Padding = new Padding(0, 5, 0, 0) };
        top.Controls.Add(MakeCaption("목적지"));
        top.Controls.Add(_destination);
        top.Controls.Add(MakeCaption("게임 창"));
        top.Controls.Add(_windows);
        top.Controls.Add(_refresh);

        var hero = Card();
        hero.Padding = new Padding(22, 16, 22, 14);
        _statusPill.Location = new Point(20, 18);
        _heroTitle.Location = new Point(20, 52);
        _heroSub.Location = new Point(22, 92);
        hero.Controls.Add(_statusPill); hero.Controls.Add(_heroTitle); hero.Controls.Add(_heroSub);

        var metrics = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Location = new Point(20, 125), Size = new Size(700, 70), BackColor = Color.White };
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        metrics.Controls.Add(Metric("완료", _countValue), 0, 0);
        metrics.Controls.Add(Metric("이번 전투", _battleValue), 1, 0);
        metrics.Controls.Add(Metric("평균 클리어", _averageValue), 2, 0);
        hero.Controls.Add(metrics);

        var next = new Panel { Location = new Point(20, 198), Height = 31, Width = 700, BackColor = Color.FromArgb(241, 246, 243) };
        _nextAction.Location = new Point(12, 7);
        next.Controls.Add(_nextAction);
        hero.Controls.Add(next);

        var assists = Card();
        assists.Padding = new Padding(18, 11, 18, 8);
        var assistFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.White, Padding = new Padding(0, 7, 0, 0) };
        assistFlow.Controls.Add(MakeCaption("전투 보조"));
        assistFlow.Controls.Add(_revival);
        assistFlow.Controls.Add(_food);
        assistFlow.Controls.Add(_reconnect);
        assistFlow.Controls.Add(_otherDungeon);
        assistFlow.Controls.Add(_autoResume);
        assists.Controls.Add(assistFlow);

        var settings = Card();
        settings.Padding = new Padding(18, 10, 18, 8);
        var setFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.White, Padding = new Padding(0, 7, 0, 0) };
        setFlow.Controls.Add(MakeCaption("실행 설정"));
        setFlow.Controls.Add(MakeCaption("UI"));
        setFlow.Controls.Add(_uiScale);
        setFlow.Controls.Add(MakeCaption("키보드"));
        setFlow.Controls.Add(_keyboard);
        setFlow.Controls.Add(MakeCaption("마우스"));
        setFlow.Controls.Add(_mouse);
        setFlow.Controls.Add(_diagnose);
        setFlow.Controls.Add(_calibrate);
        setFlow.Controls.Add(_recognize);
        settings.Controls.Add(setFlow);

        var records = Card();
        records.Padding = new Padding(14, 10, 14, 10);

        var lootGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
            Padding = new Padding(0)
        };
        lootGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lootGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var recentLootCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10, 4, 14, 4) };
        var recentLootTitle = MakeLabel("✦  최근 획득 아이템", 10, FontStyle.Bold, Color.FromArgb(42, 55, 62));
        recentLootTitle.Dock = DockStyle.Top;
        recentLootTitle.Height = 28;
        var recentLootSub = MakeLabel("방금 끝난 판의 OCR 결과", 7.8f, FontStyle.Regular, Color.FromArgb(143, 153, 160));
        recentLootSub.Dock = DockStyle.Top;
        recentLootSub.Height = 22;
        _lootRecent.Dock = DockStyle.Fill;
        recentLootCard.Controls.Add(_lootRecent);
        recentLootCard.Controls.Add(recentLootSub);
        recentLootCard.Controls.Add(recentLootTitle);

        var totalLootCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 252, 249), Padding = new Padding(14, 4, 10, 4) };
        var totalLootTitle = MakeLabel("Σ  이번 실행 누적 획득량", 10, FontStyle.Bold, Color.FromArgb(29, 105, 75));
        totalLootTitle.Dock = DockStyle.Top;
        totalLootTitle.Height = 28;
        _lootStatus.Dock = DockStyle.Top;
        _lootStatus.Height = 22;
        _lootTotals.Dock = DockStyle.Fill;
        _lootTotals.BackColor = Color.FromArgb(247, 252, 249);
        totalLootCard.Controls.Add(_lootTotals);
        totalLootCard.Controls.Add(_lootStatus);
        totalLootCard.Controls.Add(totalLootTitle);

        lootGrid.Controls.Add(recentLootCard, 0, 0);
        lootGrid.Controls.Add(totalLootCard, 1, 0);
        records.Controls.Add(lootGrid);

        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(247, 249, 249), Padding = new Padding(0, 6, 0, 0), Visible = false };
        logPanel.Controls.Add(_logBox);
        _logBox.Visible = true;

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = main.BackColor };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var leftBottom = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = main.BackColor, Padding = new Padding(0, 7, 0, 0) };
        leftBottom.Controls.Add(_elapsedValue);
        leftBottom.Controls.Add(_profile);
        leftBottom.Controls.Add(_showLog);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = main.BackColor, Padding = new Padding(0, 2, 0, 0) };
        actions.Controls.Add(_start); actions.Controls.Add(_stop);
        bottom.Controls.Add(leftBottom, 0, 0); bottom.Controls.Add(actions, 1, 0);

        content.Controls.Add(top, 0, 0);
        content.Controls.Add(hero, 0, 1);
        content.Controls.Add(assists, 0, 2);
        content.Controls.Add(settings, 0, 3);
        content.Controls.Add(records, 0, 4);
        content.Controls.Add(logPanel, 0, 5);
        content.Controls.Add(bottom, 0, 6);
        main.Controls.Add(content);

        _showLog.CheckedChanged += (_, _) =>
        {
            records.Visible = !_showLog.Checked;
            logPanel.Visible = _showLog.Checked;
        };

        shell.Controls.Add(side, 0, 0);
        shell.Controls.Add(main, 1, 0);
        Controls.Add(shell);
    }

    private void WireEvents()
    {
        _refresh.Click += (_, _) => RefreshWindows();
        _windows.SelectedIndexChanged += (_, _) => UpdateProfileLabel();
        _destination.SelectedIndexChanged += (_, _) =>
        {
            if (_cts is null) _resumeOnNextStart = false;
            UpdateModeSummary();
        };
        _calibrate.Click += (_, _) => OpenCalibration();
        _recognize.Click += (_, _) => OpenRecognitionTest();
        _diagnose.Click += (_, _) => DiagnoseInput();
        _start.Click += async (_, _) => await StartRunAsync();
        _stop.Click += (_, _) => StopRun();

        _otherDungeon.CheckedChanged += (_, _) =>
        {
            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;
            SaveScenarioQuietly();
            UpdateModeSummary();
        };

        FormClosing += (_, _) => StopRun();
        _metricsTimer.Tick += (_, _) =>
        {
            if (_uiRunStarted is DateTimeOffset started && _cts is not null)
            {
                var elapsed = DateTimeOffset.Now - started;
                _elapsedValue.Text = $"◷  총 실행  {elapsed:hh\\:mm\\:ss}";
                if (_roundUiStarted is DateTimeOffset round)
                    _battleValue.Text = $"{DateTimeOffset.Now - round:mm\\:ss}";
            }
        };
        _metricsTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyRegistered = Win32.RegisterHotKey(Handle, HotkeyId, Win32.MOD_NOREPEAT, Win32.VK_F10);
        if (!_hotkeyRegistered)
            _connection.Text = "●  F10 단축키 등록 실패";
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
        UpdateConnection();
    }

    private void UpdateConnection()
    {
        var connected = _windows.SelectedItem is WindowCandidate;
        _connection.Text = connected ? "●  게임 연결됨" : "●  게임 창을 선택하세요";
        _connection.ForeColor = connected ? Color.FromArgb(24, 151, 96) : Color.FromArgb(189, 86, 68);
    }

    private void UpdateProfileLabel()
    {
        UpdateConnection();
        if (_windows.SelectedItem is not WindowCandidate w) { _profile.Text = "프로필: -"; return; }
        var exact = _config.Targets.Profiles.FirstOrDefault(kv => kv.Value.CaptureWidth == w.Bounds.Width && kv.Value.CaptureHeight == w.Bounds.Height);
        _profile.Text = exact.Value is null ? $"프로필 없음 · {w.Bounds.Width}×{w.Bounds.Height}" : $"{exact.Key} · {w.Bounds.Width}×{w.Bounds.Height}";
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
            MessageBox.Show(this, "실행 파일 옆에 interception.dll이 없습니다.", "입력 진단", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Interception 진단 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            _config.App.Features.OtherDungeonLoop = _otherDungeon.Checked;
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
                throw new InvalidOperationException("Interception 준비 상태가 아닙니다: " + diagnostic.Summary);
            }

            _logger = new RollingLogger(Path.Combine(_baseDir, "logs"), _config.App.Logging);
            _logger.LineWritten += line => Ui(() =>
            {
                _logBox.AppendText(line + Environment.NewLine);
                if (_logBox.TextLength > 120_000) _logBox.Text = _logBox.Text[^80_000..];
                AddRecent(line);
            });

            _engine = new AutomationEngine(_config, pendingInput, _logger, new DiagnosticStore(Path.Combine(_baseDir, "diagnostics"), _config.App.Logging));
            pendingInput = null;

            _engine.StatusChanged += (stage, text) => Ui(() =>
            {
                ApplyStage(stage, text);
                if (stage == RunStage.ErrorPaused) _resumeOnNextStart = true;
                else if (stage == RunStage.Stopped) _resumeOnNextStart = false;
            });

            _engine.MetricsChanged += (count, elapsed, average) => Ui(() =>
            {
                _countValue.Text = $"{count}판";
                _elapsedValue.Text = $"◷  총 실행  {elapsed:hh\\:mm\\:ss}";
                _averageValue.Text = average is null ? "--:--" : $"{average.Value:mm\\:ss}";
            });

            _engine.LootUpdated += snapshot => Ui(() => UpdateLoot(snapshot));

            _lootRecent.Items.Clear();
            _lootTotals.Items.Clear();
            _lootStatus.Text = "보상 화면이 나오면 자동으로 OCR 기록합니다.";
            _lootStatus.ForeColor = Color.FromArgb(133, 144, 151);

            _cts = new CancellationTokenSource();
            _uiRunStarted = DateTimeOffset.Now;
            _roundUiStarted = DateTimeOffset.Now;
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
            _roundUiStarted = null;
            SetRunningUi(false);
            ApplyStage(RunStage.Idle, "대기");
        }
    }

    private void StopRun()
    {
        try { _cts?.Cancel(); } catch { }
    }

    private void ApplyStage(RunStage stage, string text)
    {
        UpdateStageSidebar(stage);
        _statusPill.Text = stage is RunStage.Idle or RunStage.Stopped ? "●  대기" : stage == RunStage.ErrorPaused ? "●  확인 필요" : "↻  반복 실행 중";
        _statusPill.ForeColor = stage == RunStage.ErrorPaused ? Color.FromArgb(193, 93, 65) : Color.FromArgb(31, 122, 87);

        _heroTitle.Text = stage switch
        {
            RunStage.OpenMenu => "메뉴를 열고 있어요",
            RunStage.SelectAbyss => "어비스를 선택하고 있어요",
            RunStage.SelectDestination => "목적지를 찾고 있어요",
            RunStage.Enter => "입장을 진행하고 있어요",
            RunStage.WaitResult => "전투가 끝나기를 기다리고 있어요",
            RunStage.Retry => _otherDungeon.Checked ? "같은 목적지로 다시 이동할게요" : "다음 판을 준비하고 있어요",
            RunStage.ErrorPaused => "진행을 확인해 주세요",
            RunStage.Stopped => "자동화를 정지했어요",
            _ => "실행할 준비가 되었어요"
        };

        _heroSub.Text = text;
        _nextAction.Text = stage switch
        {
            RunStage.OpenMenu => "다음 동작   메뉴 확인 → 어비스 선택",
            RunStage.SelectAbyss => "다음 동작   어비스 선택 → 목적지 선택",
            RunStage.SelectDestination => "다음 동작   목적지 선택 → 입장",
            RunStage.Enter => "다음 동작   이동하기 → 도전하기(표시될 때만)",
            RunStage.WaitResult => "다음 동작   결과 확인 → 반복 방식 선택",
            RunStage.Retry => _otherDungeon.Checked ? "다음 동작   다른 던전 가기 → 최초 목적지 재선택" : "다음 동작   다시 하기 → 전투 대기",
            _ => "다음 동작   메뉴 열기 → 어비스 선택 → 목적지 선택"
        };

        if (stage == RunStage.WaitResult && _roundUiStarted is null) _roundUiStarted = DateTimeOffset.Now;
        if (stage == RunStage.SelectDestination || stage == RunStage.WaitResult) _roundUiStarted ??= DateTimeOffset.Now;
        if (stage == RunStage.Retry) _battleValue.Text = _roundUiStarted is null ? "--:--" : $"{DateTimeOffset.Now - _roundUiStarted.Value:mm\\:ss}";
        if (stage == RunStage.SelectDestination && _otherDungeon.Checked) _roundUiStarted = DateTimeOffset.Now;
    }

    private void UpdateStageSidebar(RunStage stage)
    {
        var current = stage switch
        {
            RunStage.OpenMenu => 0,
            RunStage.SelectAbyss => 1,
            RunStage.SelectDestination => 2,
            RunStage.Enter => 3,
            RunStage.WaitResult => 4,
            RunStage.Retry => 5,
            _ => -1
        };

        for (var i = 0; i < _stageRows.Length; i++)
        {
            var done = current >= 0 && i < current;
            var active = i == current;
            _stageRows[i].BackColor = active ? Color.FromArgb(237, 246, 241) : Color.White;
            _stageDots[i].BackColor = done || active ? Color.FromArgb(42, 128, 94) : Color.FromArgb(210, 218, 215);
            _stageDots[i].Text = done ? "✓" : (i + 1).ToString();
            _stageTitles[i].ForeColor = active ? Color.FromArgb(28, 82, 60) : Color.FromArgb(45, 55, 61);
        }
    }

    private void UpdateLoot(LootSnapshot snapshot)
    {
        _lootRecent.Items.Clear();
        foreach (var item in snapshot.Recent.Take(6))
        {
            var qty = item.Quantity is int q ? $"  × {q:N0}" : "  · 수량 확인 필요";
            var prefix = item.Recognized ? "◆" : "⚠";
            _lootRecent.Items.Add($"{prefix}  {item.Name}{qty}");
        }

        _lootTotals.Items.Clear();
        foreach (var total in snapshot.Totals.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(8))
            _lootTotals.Items.Add($"{total.Key}    {total.Value:N0}");

        if (snapshot.Totals.Count == 0)
            _lootTotals.Items.Add("아직 누적된 인식 결과가 없습니다.");

        _lootStatus.Text = snapshot.ScreenshotPath is null
            ? $"{snapshot.Destination} · {snapshot.Round}판 보상 기록 완료"
            : $"{snapshot.Destination} · {snapshot.Round}판 · 미인식 캡처 저장됨";

        _lootStatus.ForeColor = snapshot.ScreenshotPath is null
            ? Color.FromArgb(38, 126, 89)
            : Color.FromArgb(194, 112, 55);
    }

    private void UpdateModeSummary()
    {
        var dest = (_destination.SelectedItem as DestItem)?.Name ?? "목적지";
        _nextAction.Text = _otherDungeon.Checked
            ? $"반복 방식   다른 던전 가기 → {dest} 재선택 → 입장"
            : "반복 방식   결과 확인 → 다시 하기";
    }

    private void AddRecent(string line)
    {
        var text = line.Length > 110 ? line[..110] + "…" : line;
        _recent.Items.Insert(0, text);
        while (_recent.Items.Count > 4) _recent.Items.RemoveAt(_recent.Items.Count - 1);
    }

    private void SaveScenarioQuietly()
    {
        try { ConfigStore.SaveScenario(_config); } catch { }
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
            _refresh.Enabled = !running;
            _calibrate.Enabled = !running;
            _recognize.Enabled = !running;
            _diagnose.Enabled = !running;

            _food.Enabled = !running;
            _revival.Enabled = !running;
            _reconnect.Enabled = !running;
            _autoResume.Enabled = !running;
            _otherDungeon.Enabled = true;

            _connection.Text = running ? "●  게임 연결됨 · 실행 중" : "●  게임 연결됨";
            _connection.ForeColor = Color.FromArgb(24, 151, 96);
        });
    }

    private static Panel Card()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Margin = new Padding(0, 4, 0, 6),
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static Control Metric(string caption, Label value)
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var c = MakeLabel(caption, 8, FontStyle.Bold, Color.FromArgb(137, 147, 154));
        c.TextAlign = ContentAlignment.MiddleCenter;
        c.Dock = DockStyle.Top;
        c.Height = 22;
        value.TextAlign = ContentAlignment.MiddleCenter;
        value.Dock = DockStyle.Fill;
        p.Controls.Add(value); p.Controls.Add(c);
        return p;
    }

    private static Label MakeCaption(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(105, 116, 124),
            Padding = new Padding(4, 8, 4, 0),
            Margin = new Padding(4, 0, 4, 0)
        };
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("맑은 고딕", size, style),
            ForeColor = color,
            BackColor = Color.Transparent
        };
    }

    private static Button MakeButton(string text, bool primary = false, bool danger = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = false,
            Width = 104,
            Height = 33,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            BackColor = primary ? Color.FromArgb(32, 184, 124) : danger ? Color.FromArgb(221, 91, 83) : Color.White,
            ForeColor = primary || danger ? Color.White : Color.FromArgb(70, 81, 88),
            Margin = new Padding(5, 0, 0, 0)
        };
        b.FlatAppearance.BorderColor = primary ? Color.FromArgb(32, 184, 124) : danger ? Color.FromArgb(221, 91, 83) : Color.FromArgb(216, 223, 225);
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    private static void StyleInput(Control c)
    {
        c.Font = new Font("맑은 고딕", 8.5f);
        c.BackColor = Color.White;
        c.ForeColor = Color.FromArgb(48, 58, 64);
        c.Margin = new Padding(4, 0, 6, 0);
    }

    private static void SetRoundRegion(Control control, int radius)
    {
        void Apply()
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            using var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(control.Width - d, 0, d, d, 270, 90);
            path.AddArc(control.Width - d, control.Height - d, d, d, 0, 90);
            path.AddArc(0, control.Height - d, d, d, 90, 90);
            path.CloseFigure();
            control.Region = new Region(path);
        }
        control.Resize += (_, _) => Apply();
        Apply();
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

internal sealed class ToggleSwitch : CheckBox
{
    private readonly string _caption;

    public ToggleSwitch(string caption)
    {
        _caption = caption;
        Appearance = Appearance.Button;
        AutoSize = false;
        Size = new Size(118, 34);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(8, 0, 0, 0);
        Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold);
        CheckedChanged += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(Color.White);

        using var textBrush = new SolidBrush(Color.FromArgb(72, 84, 91));
        pevent.Graphics.DrawString(_caption, Font, textBrush, new PointF(2, 8));

        var track = new Rectangle(77, 8, 38, 20);
        using var trackBrush = new SolidBrush(Checked ? Color.FromArgb(41, 154, 105) : Color.FromArgb(196, 204, 207));
        using var trackPath = RoundedRect(track, 10);
        pevent.Graphics.FillPath(trackBrush, trackPath);

        var knobX = Checked ? track.Right - 18 : track.Left + 2;
        using var knobBrush = new SolidBrush(Color.White);
        pevent.Graphics.FillEllipse(knobBrush, knobX, track.Top + 2, 16, 16);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
