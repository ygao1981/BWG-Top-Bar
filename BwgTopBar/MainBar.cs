using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace BwgTopBar;

internal sealed class MainBar : Form
{
    private readonly ApplicationSettings _settings;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly NotifyIcon _tray;
    private readonly ToolTip _tip = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };
    private readonly Font _font = new("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private MonitorSnapshot _snapshot = new();
    private CancellationTokenSource _cancellation = new();
    private Task _activeRefresh = Task.CompletedTask;
    private DateTimeOffset _lastLive = DateTimeOffset.MinValue;
    private DateTimeOffset? _trafficUpdatedAt;
    private DateTimeOffset? _resourcesUpdatedAt;
    private string? _trafficWarning;
    private string? _resourceWarning;
    private string? _localWarning;
    private bool _updating;
    private bool _settingsOpen;
    private bool _closing;
    private bool _closeReady;
    private bool _dragging;
    private bool _centerOnResize;
    private Point _mouseStart;
    private Point _windowStart;
    private string _displayText = "BWG  |  右键设置连接";
    private readonly ToolStripMenuItem _topMenu;
    private readonly ToolStripMenuItem _startupMenu;

    public MainBar()
    {
        _settings = ApplicationSettings.Load();
        Text = "BWG 单行悬浮栏";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        TopMost = _settings.TopMost;
        BackColor = Color.FromArgb(21, 28, 41);
        ForeColor = Color.White;
        Opacity = 0.97;
        Size = new Size(360, 32);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = _settings.WindowX.HasValue && _settings.WindowY.HasValue &&
            Screen.AllScreens.Any(s => s.Bounds.Contains(_settings.WindowX.Value, _settings.WindowY.Value))
            ? new Point(_settings.WindowX.Value, _settings.WindowY.Value)
            : new Point(primary.Left + (primary.Width - Width) / 2, primary.Top + 6);
        _centerOnResize = !_settings.WindowX.HasValue || !_settings.WindowY.HasValue;
        Cursor = Cursors.SizeAll;

        var menu = new ContextMenuStrip();
        menu.Items.Add("立即刷新", null, (_, _) => RequestRefresh());
        menu.Items.Add("设置连接与刷新间隔", null, (_, _) => OpenSettings());
        _topMenu = new ToolStripMenuItem("始终置顶", null, (_, _) => ToggleTopMost()) { Checked = TopMost };
        menu.Items.Add(_topMenu);
        _startupMenu = new ToolStripMenuItem("开机启动", null, (_, _) => ToggleStartup()) { Checked = HasStartup() };
        menu.Items.Add(_startupMenu);
        menu.Items.Add("回到屏幕顶部", null, (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            _centerOnResize = true;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Pixels(6));
            SaveWindowPosition();
        });
        menu.Items.Add("打开 KiwiVM 控制台", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://kiwivm.64clouds.com/") { UseShellExecute = true });
            }
            catch (Exception)
            {
                MessageBox.Show(this, "无法打开浏览器，请手动访问 KiwiVM 控制台。", "打开控制台");
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Close());
        ContextMenuStrip = menu;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "搬瓦工单行悬浮栏",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _timer.Interval = Math.Clamp(_settings.RefreshMinutes, 2, 30) * 60 * 1000;
        _timer.Tick += (_, _) => RequestRefresh();
        _timer.Start();

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            _centerOnResize = false;
            Capture = true;
            _mouseStart = Cursor.Position;
            _windowStart = Location;
        };
        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            var here = Cursor.Position;
            Location = new Point(_windowStart.X + here.X - _mouseStart.X,
                _windowStart.Y + here.Y - _mouseStart.Y);
        };
        MouseUp += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            Capture = false;
            UpdateBar();
            SaveWindowPosition();
        };
    }

    // 悬浮栏显示和拖动不抢走当前应用的键盘焦点；设置窗口仍正常激活。
    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            m.Result = new IntPtr(3); // MA_NOACTIVATE
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateBar();
        if (string.IsNullOrWhiteSpace(_settings.Veid)) BeginInvoke(new Action(OpenSettings));
        else RequestRefresh();
    }

    private async void OpenSettings()
    {
        if (_settingsOpen || _closing) return;
        _settingsOpen = true;
        _timer.Stop();
        _cancellation.Cancel();
        try
        {
            // 等待旧配置的请求退出，防止它在保存新配置后回写旧 VPS 的结果。
            await _activeRefresh;
            if (_closing) return;
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
            using var dialog = new SettingsDialog(_settings);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _snapshot = new MonitorSnapshot();
                _trafficUpdatedAt = null;
                _resourcesUpdatedAt = null;
                _trafficWarning = null;
                _resourceWarning = null;
                _localWarning = null;
                _lastLive = DateTimeOffset.MinValue;
                _timer.Interval = Math.Clamp(_settings.RefreshMinutes, 2, 30) * 60 * 1000;
            }
        }
        catch (Exception)
        {
            if (!_closing)
                MessageBox.Show(this, "无法打开设置。请重新启动程序后再试。", "设置未打开");
        }
        finally
        {
            _settingsOpen = false;
            if (!_closing)
            {
                _timer.Start();
                RequestRefresh();
            }
        }
    }

    private void RequestRefresh()
    {
        if (_closing || _settingsOpen || !_activeRefresh.IsCompleted) return;
        _activeRefresh = RefreshAsync(_cancellation.Token);
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        if (_closing || string.IsNullOrWhiteSpace(_settings.Veid)) return;
        try
        {
            var key = WindowsCredentialStore.Read();
            if (string.IsNullOrWhiteSpace(key))
            {
                _trafficWarning = "尚未设置密钥，请右键打开设置";
                UpdateBar();
                return;
            }
            _updating = true;
            UpdateBar();
            var result = await KiwiVmClient.GetAsync("getServiceInfo", _settings.Veid, key, token);
            token.ThrowIfCancellationRequested();
            if (_closing) return;
            _snapshot.ApplyBasic(result);
            _trafficUpdatedAt = DateTimeOffset.Now;
            _trafficWarning = null;
            UpdateBar();

            if (_settings.UseSsh)
            {
                try
                {
                    var stats = await SshReader.ReadAsync(_settings, token);
                    token.ThrowIfCancellationRequested();
                    if (_closing) return;
                    _snapshot.ApplySsh(stats);
                    _resourcesUpdatedAt = DateTimeOffset.Now;
                    _resourceWarning = null;
                }
                catch (Exception) when (!token.IsCancellationRequested && !_closing)
                {
                    _snapshot.ClearResources();
                    _resourcesUpdatedAt = null;
                    _resourceWarning = "资源查询失败，请检查 SSH 连接、密钥和主机指纹";
                }
            }
            else if (DateTimeOffset.Now - _lastLive >= TimeSpan.FromMinutes(5))
            {
                try
                {
                    // 记录尝试时间，使失败请求也受五分钟间隔限制。
                    _lastLive = DateTimeOffset.Now;
                    var live = await KiwiVmClient.GetAsync("getLiveServiceInfo", _settings.Veid, key, token);
                    token.ThrowIfCancellationRequested();
                    if (_closing) return;
                    _snapshot.ApplyLive(live);
                    _resourcesUpdatedAt = DateTimeOffset.Now;
                    _resourceWarning = null;
                }
                catch (Exception) when (!token.IsCancellationRequested && !_closing)
                {
                    _snapshot.ClearResources();
                    _resourcesUpdatedAt = null;
                    _resourceWarning = "资源查询失败，稍后将重试";
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested || _closing) { }
        catch (Exception)
        {
            _trafficWarning = "流量查询失败，请检查网络、VEID 和 API Key";
        }
        finally
        {
            _updating = false;
            if (!_closing && !token.IsCancellationRequested) UpdateBar();
        }
    }

    private int Pixels(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    private string BuildMessage(bool compact)
    {
        var parts = new List<string> { "BWG" };
        if (!string.IsNullOrEmpty(_trafficWarning))
            parts.Add(_trafficUpdatedAt.HasValue ? $"流量未更新（{_trafficUpdatedAt.Value.LocalDateTime:HH:mm}）" : "未连接 · 右键设置");
        else if (!string.IsNullOrEmpty(_resourceWarning)) parts.Add("资源未更新");
        else if (!string.IsNullOrEmpty(_localWarning)) parts.Add("设置未保存");
        else if (_updating && !_trafficUpdatedAt.HasValue) parts.Add("正在连接…");

        if (_trafficUpdatedAt.HasValue)
        {
            parts.AddRange(_snapshot.ToLine(compact).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(1));
        }
        else if (!_updating && string.IsNullOrEmpty(_trafficWarning)) parts.Add("右键设置连接");
        return string.Join("  |  ", parts);
    }

    private void UpdateBar()
    {
        if (_closing || IsDisposed || _tray is null) return;
        var trafficTime = _trafficUpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无数据";
        var resourcesTime = _resourcesUpdatedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无数据";
        _tip.SetToolTip(this, $"{_snapshot.ToLine()}\n流量更新时间：{trafficTime}\n资源更新时间：{resourcesTime}\n" +
            $"{_trafficWarning ?? _resourceWarning ?? _localWarning ?? "按设定间隔自动刷新"}\n左键拖动；右键设置；窄屏隐藏的项目可在此查看。");
        var trayText = _snapshot.UsedPercent is double p
            ? $"BWG 已用 {p:0.#}% · {_trafficUpdatedAt?.LocalDateTime:HH:mm}"
            : "搬瓦工单行悬浮栏";
        if (_trafficWarning != null) trayText += " · 流量未更新";
        else if (_resourceWarning != null) trayText += " · 资源未更新";
        _tray.Text = trayText.Length > 63 ? trayText[..63] : trayText;

        var area = Screen.FromControl(this).WorkingArea;
        var maxWidth = Math.Max(1, area.Width - Pixels(12));
        _displayText = BuildMessage(compact: false);
        using var graphics = CreateGraphics();
        int Measure(string text) => TextRenderer.MeasureText(graphics, text, _font).Width + Pixels(48);
        var measured = Measure(_displayText);
        if (measured > maxWidth)
        {
            _displayText = BuildMessage(compact: true);
            measured = Measure(_displayText);
        }
        if (!_dragging)
        {
            Size = new Size(Math.Clamp(measured, Math.Min(Pixels(240), maxWidth), maxWidth), Pixels(32));
            var x = _centerOnResize ? area.Left + (area.Width - Width) / 2
                : Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
            Location = new Point(x, y);
        }
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateBar();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Pixels(7));
        using var bg = new SolidBrush(BackColor);
        using var border = new Pen(Color.FromArgb(48, 64, 83), 1f);
        g.FillPath(bg, path);
        g.DrawPath(border, path);

        var rate = _snapshot.UsedPercent;
        var hasWarning = _trafficWarning != null || _resourceWarning != null || _localWarning != null;
        var dotColor = hasWarning ? Color.FromArgb(245, 178, 87)
            : !_trafficUpdatedAt.HasValue ? Color.FromArgb(139, 153, 173)
            : rate is >= 90 ? Color.FromArgb(248, 106, 113)
            : rate is >= 80 ? Color.FromArgb(245, 178, 87)
            : Color.FromArgb(84, 208, 170);
        using var dot = new SolidBrush(dotColor);
        var dotSize = Pixels(8);
        g.FillEllipse(dot, Pixels(12), (Height - dotSize) / 2, dotSize, dotSize);
        TextRenderer.DrawText(g, _displayText, _font, new Rectangle(Pixels(29), 0, Width - Pixels(40), Height),
            Color.FromArgb(235, 241, 249), TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void SaveWindowPosition()
    {
        _settings.WindowX = Left;
        _settings.WindowY = Top;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try { _settings.Save(); _localWarning = null; }
        catch (Exception) { _localWarning = "无法保存设置，请检查当前用户的配置目录是否可写"; }
        UpdateBar();
    }

    private void ToggleTopMost()
    {
        TopMost = !TopMost;
        _settings.TopMost = TopMost;
        _topMenu.Checked = TopMost;
        SaveSettings();
    }

    private static bool HasStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("BwgTopBar") != null;
        }
        catch (Exception) { return false; }
    }

    private void ToggleStartup()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "请先发布为独立 EXE，再开启开机启动。", "开机启动");
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) throw new InvalidOperationException();
            if (HasStartup()) key.DeleteValue("BwgTopBar", false);
            else key.SetValue("BwgTopBar", $"\"{path}\"");
            _startupMenu.Checked = HasStartup();
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法修改开机启动设置，请检查当前用户权限。", "开机启动未更新");
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing && !_closeReady)
        {
            e.Cancel = true;
            base.OnFormClosing(e);
            return;
        }

        _closing = true;
        _timer.Stop();
        _cancellation.Cancel();
        if (_activeRefresh.IsCompleted || _closeReady)
        {
            _closeReady = true;
            base.OnFormClosing(e);
            return;
        }

        // 保持 UI 消息循环运行，让取消后的 SSH 子进程清理可以完成。
        e.Cancel = true;
        base.OnFormClosing(e);
        try { await _activeRefresh; }
        catch (Exception) { /* 退出仍需继续，刷新本身已负责清理资源。 */ }
        _closeReady = true;
        if (!IsDisposed) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _cancellation.Cancel();
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _timer.Dispose();
        _tip.Dispose();
        _font.Dispose();
        _cancellation.Dispose();
        base.OnFormClosed(e);
    }
}
