using System.Drawing;
using System.Text.RegularExpressions;

namespace BwgTopBar;

internal sealed class SettingsDialog : Form
{
    private readonly ApplicationSettings _settings;
    private readonly TextBox _veid = new();
    private readonly TextBox _api = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown _minutes = new() { Minimum = 2, Maximum = 30, Value = 2 };
    private readonly CheckBox _ssh = new() { Text = "通过 SSH 读取实际剩余资源", AutoSize = true };
    private readonly TextBox _sshHost = new();
    private readonly TextBox _sshUser = new();
    private readonly NumericUpDown _sshPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _sshKey = new();

    public SettingsDialog(ApplicationSettings settings)
    {
        _settings = settings;
        Text = "BWG 单行悬浮栏 · 设置";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(600, 486);
        BackColor = Color.White;

        _veid.Text = settings.Veid;
        try { _api.Text = WindowsCredentialStore.Read(); }
        catch (Exception) { _api.Text = ""; }
        _minutes.Value = Math.Clamp(settings.RefreshMinutes, 2, 30);
        _ssh.Checked = settings.UseSsh;
        _sshHost.Text = settings.SshHost;
        _sshUser.Text = settings.SshUser;
        _sshPort.Value = Math.Clamp(settings.SshPort, 1, 65535);
        _sshKey.Text = settings.SshKeyPath;

        var table = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 9, Left = 24, Top = 24,
            Width = 548, Height = 360, CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 9; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        void Row(int i, string caption, Control input)
        {
            var label = new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };
            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(0, 6, 0, 6);
            table.Controls.Add(label, 0, i);
            table.Controls.Add(input, 1, i);
        }
        Row(0, "VEID", _veid);
        Row(1, "KiwiVM API Key", _api);
        Row(2, "刷新间隔（分钟）", _minutes);
        Row(3, "资源读取", _ssh);
        Row(4, "SSH 地址（可选）", _sshHost);
        Row(5, "SSH 用户", _sshUser);
        Row(6, "SSH 端口", _sshPort);
        Row(7, "SSH 私钥路径", _sshKey);
        var note = new Label
        {
            Text = "API Key 只保存在 Windows 凭据管理器中。SSH 使用现有密钥及严格主机指纹校验，不保存 SSH 密码。",
            ForeColor = Color.FromArgb(84, 95, 110),
            Dock = DockStyle.Fill,
            AutoSize = false,
        };
        table.Controls.Add(note, 0, 8);
        table.SetColumnSpan(note, 2);
        Controls.Add(table);

        var info = new Label
        {
            Left = 24, Top = 390, Width = 550, Height = 36,
            ForeColor = Color.FromArgb(95, 105, 115),
            Text = "如果仅需每月流量，可关闭 SSH。首次使用 SSH 前，请在终端手工连接以确认服务器指纹。",
        };
        Controls.Add(info);
        var save = new Button { Text = "保存并刷新", Left = 364, Top = 442, Width = 120, Height = 32 };
        var cancel = new Button { Text = "取消", Left = 492, Top = 442, Width = 80, Height = 32, DialogResult = DialogResult.Cancel };
        save.Click += SaveClick;
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        _ssh.CheckedChanged += (_, _) => SetSshEnabled();
        SetSshEnabled();
    }

    private void SetSshEnabled()
    {
        _sshHost.Enabled = _ssh.Checked;
        _sshUser.Enabled = _ssh.Checked;
        _sshPort.Enabled = _ssh.Checked;
        _sshKey.Enabled = _ssh.Checked;
    }

    private void SaveClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_veid.Text) || !_veid.Text.Trim().All(c => c is >= '0' and <= '9'))
        {
            MessageBox.Show(this, "请填写 KiwiVM 中的数字 VEID。", "检查设置"); return;
        }
        if (string.IsNullOrWhiteSpace(_api.Text))
        {
            MessageBox.Show(this, "请填写 KiwiVM API Key。", "检查设置"); return;
        }
        if (_ssh.Checked && (string.IsNullOrWhiteSpace(_sshHost.Text) || string.IsNullOrWhiteSpace(_sshUser.Text)))
        {
            MessageBox.Show(this, "启用 SSH 时，必须填写 SSH 地址和用户名。", "检查设置"); return;
        }
        if (_ssh.Checked &&
            (!Regex.IsMatch(_sshUser.Text.Trim(), "^[A-Za-z_][A-Za-z0-9_-]*$") ||
             !Regex.IsMatch(_sshHost.Text.Trim(), "^[A-Za-z0-9.:_][A-Za-z0-9.:_-]*$")))
        {
            MessageBox.Show(this, "请检查 SSH 用户名和服务器地址。地址只填 IP 或域名，不包含协议或路径。", "检查设置"); return;
        }
        var next = new ApplicationSettings
        {
            Veid = _veid.Text.Trim(),
            RefreshMinutes = (int)_minutes.Value,
            UseSsh = _ssh.Checked,
            SshHost = _sshHost.Text.Trim(),
            SshUser = _sshUser.Text.Trim(),
            SshPort = (int)_sshPort.Value,
            SshKeyPath = _sshKey.Text.Trim(),
            WindowX = _settings.WindowX,
            WindowY = _settings.WindowY,
            TopMost = _settings.TopMost,
        };
        string? oldKey = null;
        var keySaved = false;
        try
        {
            oldKey = WindowsCredentialStore.Read();
            WindowsCredentialStore.Write(_api.Text.Trim());
            keySaved = true;
            next.Save();
            _settings.Veid = next.Veid;
            _settings.RefreshMinutes = next.RefreshMinutes;
            _settings.UseSsh = next.UseSsh;
            _settings.SshHost = next.SshHost;
            _settings.SshUser = next.SshUser;
            _settings.SshPort = next.SshPort;
            _settings.SshKeyPath = next.SshKeyPath;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception)
        {
            var restored = true;
            if (keySaved)
            {
                try { WindowsCredentialStore.Write(oldKey ?? ""); }
                catch (Exception) { restored = false; }
            }
            MessageBox.Show(this, restored
                ? "设置未保存。请检查 Windows 凭据管理器和配置目录的访问权限后重试。"
                : "设置未完整保存。请核对 API Key，并再次保存后再开始监控。",
                "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
