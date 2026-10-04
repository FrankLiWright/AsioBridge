using AsioBridge.Audio;

namespace AsioBridge;

/// <summary>
/// Compact dark audio-tool UI. All key controls are owner-drawn so colors,
/// spacing and alignment stay consistent (ASIO4ALL / DAW utility style).
/// </summary>
public sealed class MainForm : Form
{
    // Palette
    private static readonly Color Bg = Color.FromArgb(28, 28, 36);
    private static readonly Color Panel = Color.FromArgb(42, 42, 54);
    private static readonly Color Field = Color.FromArgb(54, 54, 70);
    private static readonly Color Border = Color.FromArgb(72, 72, 92);
    private static readonly Color TextPrimary = Color.FromArgb(236, 236, 244);
    private static readonly Color TextDim = Color.FromArgb(155, 155, 175);
    private static readonly Color Green = Color.FromArgb(80, 210, 130);
    private static readonly Color Accent = Color.FromArgb(72, 148, 255);

    private readonly BridgeEngine _engine = new();

    private readonly ComboBox _asioCombo = new();
    private readonly ComboBox _modeCombo = new();
    private readonly ComboBox _processCombo = new();
    private readonly Button _refreshProcesses = new();
    private readonly DarkSlider _bufferSlider = new();
    private readonly CheckBox _includeChildren = new();
    private readonly StartStopButton _startStop = new();
    private readonly Label _statusLabel = new();
    private readonly PeakMeter _peakMeter = new();
    private readonly Label _bufferValue = new();
    private readonly NotifyIcon _tray;

    private readonly float _uiScale;
    private readonly int _pad;
    private readonly int _rowH;

    public MainForm()
    {
        Text = "AsioBridge";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowIcon = true;
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9.5F);

        _uiScale = Math.Max(1f, DeviceDpi / 96f);
        _pad = S(20);
        _rowH = S(44);

        int bottomH = S(104);
        int formW = S(540);
        int formH = _pad + _rowH * 3 + S(12) * 2 + S(4) + bottomH;
        ClientSize = new Size(formW, formH);

        BuildLayout(bottomH);

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "AsioBridge",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.ContextMenuStrip = BuildTrayMenu();

        Load += (_, _) =>
        {
            LoadAsioDrivers();
            RefreshProcesses();
            UpdateUi();
            SetStatus("就绪", TextDim);
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1800, "AsioBridge", "仍在托盘运行。双击图标可重新打开。", ToolTipIcon.Info);
                return;
            }
            Cleanup();
        };

        _engine.StatusChanged += OnStatus;
        _engine.Error += msg =>
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (!IsDisposed) SetStatus(msg, Color.FromArgb(255, 120, 120));
            });
        };

        _modeCombo.SelectedIndexChanged += (_, _) => UpdateUi();
        _startStop.Click += (_, _) => Toggle();
        _refreshProcesses.Click += (_, _) => RefreshProcesses();
    }

    private int S(float v) => (int)Math.Round(v * _uiScale);

    private void BuildLayout(int bottomH)
    {
        // ── Bottom bar ──
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = bottomH,
            BackColor = Panel,
            Padding = new Padding(_pad, S(16), _pad, S(12)),
        };

        _startStop.Dock = DockStyle.Top;
        _startStop.Height = S(50);
        _startStop.SetRunning(false);

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = S(30),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, S(10), 0, 0),
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.Text = "就绪";
        _statusLabel.AutoSize = true;
        _statusLabel.Font = new Font("Segoe UI", 9F);
        _statusLabel.ForeColor = TextDim;
        _statusLabel.Anchor = AnchorStyles.Left;

        _peakMeter.Width = S(96);
        _peakMeter.Height = S(16);
        _peakMeter.Anchor = AnchorStyles.Right;

        statusRow.Controls.Add(_statusLabel, 0, 0);
        statusRow.Controls.Add(_peakMeter, 1, 0);
        bottom.Controls.Add(_startStop);
        bottom.Controls.Add(statusRow);

        // ── Options ──
        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(_pad, _pad, _pad, S(8)),
            ColumnCount = 2,
            RowCount = 3,
        };
        int labelW = S(56);
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelW));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, _rowH));

        Label MakeLabel(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Width = labelW,
            Height = _rowH,
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };

        // Row 0 — ASIO
        StyleCombo(_asioCombo);
        _asioCombo.Dock = DockStyle.Fill;
        _asioCombo.Margin = new Padding(0, S(7), 0, S(7));
        options.Controls.Add(MakeLabel("ASIO"), 0, 0);
        options.Controls.Add(_asioCombo, 1, 0);

        // Row 1 — mode / process / refresh  (all share the same cell margins → same baseline)
        StyleCombo(_modeCombo);
        _modeCombo.Items.AddRange(new object[] { "全局", "按进程" });
        _modeCombo.SelectedIndex = 1;
        _modeCombo.Dock = DockStyle.Fill;
        _modeCombo.Margin = new Padding(0, S(7), S(10), S(7));

        StyleCombo(_processCombo);
        _processCombo.Dock = DockStyle.Fill;
        _processCombo.Margin = new Padding(0, S(7), S(10), S(7));

        _refreshProcesses.Text = "↻";
        _refreshProcesses.FlatStyle = FlatStyle.Flat;
        _refreshProcesses.FlatAppearance.BorderColor = Border;
        _refreshProcesses.FlatAppearance.MouseOverBackColor = Field;
        _refreshProcesses.BackColor = Field;
        _refreshProcesses.ForeColor = TextDim;
        _refreshProcesses.Font = new Font("Segoe UI", 11F);
        _refreshProcesses.Cursor = Cursors.Hand;
        // Same height as the adjacent combos so the row reads as one line.
        _refreshProcesses.Dock = DockStyle.Top;
        _refreshProcesses.Height = S(24);
        _refreshProcesses.Margin = new Padding(0, S(7), 0, S(7));

        var modeRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(100)));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(40)));
        modeRow.Controls.Add(_modeCombo, 0, 0);
        modeRow.Controls.Add(_processCombo, 1, 0);
        modeRow.Controls.Add(_refreshProcesses, 2, 0);

        options.Controls.Add(MakeLabel("模式"), 0, 1);
        options.Controls.Add(modeRow, 1, 1);

        // Row 2 — slider + value + checkbox
        _bufferSlider.Minimum = 10;
        _bufferSlider.Maximum = 200;
        _bufferSlider.Value = 50;
        _bufferSlider.Dock = DockStyle.Fill;
        _bufferSlider.Margin = new Padding(0, S(8), 0, 0);
        _bufferSlider.ValueChanged += (_, _) => _bufferValue.Text = _bufferSlider.Value + " ms";

        _bufferValue.Text = "50 ms";
        _bufferValue.AutoSize = false;
        _bufferValue.Width = S(68);
        _bufferValue.Height = _rowH;
        _bufferValue.Font = new Font("Segoe UI Semibold", 10F);
        _bufferValue.ForeColor = TextPrimary;
        _bufferValue.TextAlign = ContentAlignment.MiddleLeft;
        _bufferValue.Dock = DockStyle.Fill;
        _bufferValue.Margin = new Padding(S(12), 0, S(14), 0);

        _includeChildren.Text = "含子进程";
        _includeChildren.Checked = true;
        _includeChildren.AutoSize = true;
        _includeChildren.ForeColor = TextDim;
        _includeChildren.Font = new Font("Segoe UI", 9.5F);
        _includeChildren.FlatStyle = FlatStyle.Flat;
        _includeChildren.Anchor = AnchorStyles.Left;
        _includeChildren.Margin = new Padding(0, S(11), 0, 0);

        var bufferRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        bufferRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bufferRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(80)));
        bufferRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bufferRow.Controls.Add(_bufferSlider, 0, 0);
        bufferRow.Controls.Add(_bufferValue, 1, 0);
        bufferRow.Controls.Add(_includeChildren, 2, 0);

        options.Controls.Add(MakeLabel("缓冲"), 0, 2);
        options.Controls.Add(bufferRow, 1, 2);

        Controls.Add(options);
        Controls.Add(bottom);
    }

    private void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Field;
        combo.ForeColor = TextPrimary;
        combo.Font = new Font("Segoe UI", 9.5F);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => ShowFromTray());
        menu.Items.Add("开始 / 停止", null, (_, _) => Toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Cleanup());
        return menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void LoadAsioDrivers()
    {
        _asioCombo.Items.Clear();
        foreach (var n in BridgeEngine.GetAsioDriverNames())
            _asioCombo.Items.Add(n);
        if (_asioCombo.Items.Count > 0) _asioCombo.SelectedIndex = 0;
    }

    private void RefreshProcesses()
    {
        int keepPid = (_processCombo.SelectedItem as AudioSessionInfo)?.ProcessId ?? 0;

        _processCombo.BeginUpdate();
        try
        {
            _processCombo.Items.Clear();
            foreach (var s in AudioSessionList.GetDefaultRenderSessions())
                _processCombo.Items.Add(s);
            _processCombo.DisplayMember = nameof(AudioSessionInfo.DisplayName);

            if (keepPid != 0)
            {
                for (int i = 0; i < _processCombo.Items.Count; i++)
                {
                    if (_processCombo.Items[i] is AudioSessionInfo s && s.ProcessId == keepPid)
                    {
                        _processCombo.SelectedIndex = i;
                        return;
                    }
                }
            }
            if (_processCombo.Items.Count > 0) _processCombo.SelectedIndex = 0;
        }
        finally
        {
            _processCombo.EndUpdate();
        }
    }

    private void UpdateUi()
    {
        bool running = _engine.IsRunning;
        bool processMode = _modeCombo.SelectedIndex == 1;

        _modeCombo.Enabled = !running;
        _asioCombo.Enabled = !running;
        _processCombo.Enabled = processMode && !running;
        _refreshProcesses.Enabled = processMode && !running;
        _includeChildren.Enabled = processMode && !running;
        _bufferSlider.Enabled = !running;

        _startStop.SetRunning(running);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void Toggle()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            SetStatus("已停止", TextDim);
            _peakMeter.SetPeaks(0, 0);
            UpdateUi();
            return;
        }

        if (_asioCombo.SelectedItem is not string driver || string.IsNullOrEmpty(driver))
        {
            MessageBox.Show(this, "请先选择 ASIO 设备。", "AsioBridge",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var options = new BridgeOptions
        {
            AsioDriverName = driver,
            Mode = _modeCombo.SelectedIndex == 1 ? CaptureMode.ProcessLoopback : CaptureMode.SystemLoopback,
            IncludeChildProcesses = _includeChildren.Checked,
            BufferMs = _bufferSlider.Value,
        };

        if (options.Mode == CaptureMode.ProcessLoopback)
        {
            if (_processCombo.SelectedItem is not AudioSessionInfo s)
            {
                MessageBox.Show(this,
                    "请选择要捕获的进程。\n若列表为空，先让软件出声再点「↻」。",
                    "AsioBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            options.TargetProcessId = s.ProcessId;
        }

        try
        {
            SetStatus("启动中…", TextDim);
            _engine.Start(options);
            SetStatus("运行中", Green);
        }
        catch (Exception ex)
        {
            SetStatus("启动失败", Color.FromArgb(255, 120, 120));
            MessageBox.Show(this, ex.Message, "AsioBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        UpdateUi();
    }

    private void OnStatus(BridgeStatus s)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                if (!s.IsRunning)
                {
                    _peakMeter.SetPeaks(0, 0);
                    return;
                }
                SetStatus($"缓冲 {s.BufferedMs} ms    欠载 {s.Underruns}", Green);
                _peakMeter.SetPeaks(s.PeakLeft, s.PeakRight);
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Cleanup()
    {
        try { _engine.Stop(); } catch { /* ignore */ }
        try { _engine.Dispose(); } catch { /* ignore */ }
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }
}

/// <summary>Dark horizontal slider with a round thumb.</summary>
internal sealed class DarkSlider : Control
{
    private int _value = 50;
    private int _minimum = 10;
    private int _maximum = 200;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; if (_value < value) _value = value; Invalidate(); }
    }

    public int Maximum
    {
        get => _maximum;
        set { _maximum = value; if (_value > value) _value = value; Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, _minimum, _maximum);
            if (v == _value) return;
            _value = v;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public DarkSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 28;
        Cursor = Cursors.Hand;
        BackColor = Color.FromArgb(28, 28, 36);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.Clear(BackColor);

        int trackH = 6;
        int thumbR = 9;
        int pad = thumbR + 2;
        int trackY = (Height - trackH) / 2;
        int trackW = Math.Max(10, Width - pad * 2);

        var track = new Rectangle(pad, trackY, trackW, trackH);
        using (var bg = new SolidBrush(Color.FromArgb(60, 60, 76)))
            e.Graphics.FillRectangle(bg, track);

        float t = (_value - _minimum) / (float)Math.Max(1, _maximum - _minimum);
        int fillW = (int)(trackW * t);
        if (fillW > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(72, 148, 255));
            e.Graphics.FillRectangle(fill, pad, trackY, fillW, trackH);
        }

        int thumbX = pad + fillW;
        int thumbY = Height / 2;
        using (var tb = new SolidBrush(Color.FromArgb(236, 236, 244)))
            e.Graphics.FillEllipse(tb, thumbX - thumbR, thumbY - thumbR, thumbR * 2, thumbR * 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragging = true;
        UpdateFromMouse(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) UpdateFromMouse(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    private void UpdateFromMouse(int x)
    {
        int pad = 11;
        int trackW = Math.Max(10, Width - pad * 2);
        float t = Math.Clamp((x - pad) / (float)trackW, 0f, 1f);
        Value = _minimum + (int)Math.Round(t * (_maximum - _minimum));
    }
}

/// <summary>Flat rounded primary action button.</summary>
internal sealed class StartStopButton : Button
{
    private bool _running;
    private bool _hover;

    private static readonly Color IdleBg = Color.FromArgb(72, 148, 255);
    private static readonly Color IdleHover = Color.FromArgb(96, 166, 255);
    private static readonly Color RunBg = Color.FromArgb(220, 70, 70);
    private static readonly Color RunHover = Color.FromArgb(235, 90, 90);
    private static readonly Color BtnFg = Color.White;

    public StartStopButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = IdleBg;
        ForeColor = BtnFg;
        Font = new Font("Segoe UI Semibold", 12F);
        Cursor = Cursors.Hand;
        Text = "▶  开 始";
    }

    public void SetRunning(bool running)
    {
        _running = running;
        Text = running ? "■  停 止" : "▶  开 始";
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        const int r = 10;

        Color fill = _running
            ? (_hover ? RunHover : RunBg)
            : (_hover ? IdleHover : IdleBg);

        using var path = RoundedRect(rect, r);
        using var brush = new SolidBrush(fill);
        e.Graphics.FillPath(brush, path);

        TextRenderer.DrawText(
            e.Graphics, this.Text, Font, rect, BtnFg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>LED-style stereo peak meter.</summary>
internal sealed class PeakMeter : Control
{
    private float _left;
    private float _right;

    public PeakMeter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(96, 16);
    }

    public void SetPeaks(float left, float right)
    {
        _left = Math.Max(left, _left * 0.72f);
        _right = Math.Max(right, _right * 0.72f);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(42, 42, 54));

        const int segments = 18;
        int gap = 1;
        int segW = Math.Max(2, (Width - gap * (segments - 1)) / segments);
        int h = Math.Max(5, (Height - 4) / 2);

        DrawRow(e.Graphics, 0, h, _left, segments, segW, gap);
        DrawRow(e.Graphics, h + 4, h, _right, segments, segW, gap);
    }

    private static void DrawRow(Graphics g, int y, int h, float peak, int segments, int segW, int gap)
    {
        int lit = (int)Math.Clamp(peak * segments, 0, segments);
        for (int i = 0; i < segments; i++)
        {
            int x = i * (segW + gap);
            Color c = i < lit
                ? (i >= segments - 2 ? Color.FromArgb(255, 120, 80)
                  : i >= segments - 5 ? Color.FromArgb(255, 200, 80)
                  : Color.FromArgb(80, 210, 130))
                : Color.FromArgb(55, 55, 70);
            using var b = new SolidBrush(c);
            g.FillRectangle(b, x, y, segW, h);
        }
    }
}
