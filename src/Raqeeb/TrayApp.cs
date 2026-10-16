using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Raqeeb;

/// <summary>أيقونة الصينية + نافذة الحالة والسجل. لا واجهة أخرى: الإعداد كله في .env.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _ui = new();
    private readonly Controller _ctl;
    private readonly ToolStripMenuItem _statusItem = new("…") { Enabled = false };
    private StatusForm? _form;

    public TrayApp(string? envOverride)
    {
        _ui.CreateControl(); _ = _ui.Handle;
        Hooks.Ui = _ui;
        _icon = new NotifyIcon { Visible = true, Text = "رقيب", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Shield };
        _ctl = new Controller((t, m) => OnUi(() => _icon.ShowBalloonTip(4000, t, m, ToolTipIcon.Info)), envOverride);

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("عرض الحالة والسجل", null, (_, _) => ShowForm());
        menu.Items.Add("فتح ملف الإعدادات (.env)", null, (_, _) => OpenEnv());
        menu.Items.Add("إعادة تحميل الإعدادات", null, async (_, _) => await _ctl.ReloadAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("تثبيت مساعد القفل (مسؤول)…", null, (_, _) => RunHelper(true));
        menu.Items.Add("إزالة مساعد القفل…", null, (_, _) => RunHelper(false));
        var auto = new ToolStripMenuItem("تشغيل مع ويندوز") { Checked = Program.Autostart, CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { try { Program.SetAutostart(auto.Checked); } catch (Exception ex) { _icon.ShowBalloonTip(3000, "رقيب", ex.Message, ToolTipIcon.Warning); } };
        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("خروج", null, async (_, _) => { _icon.Visible = false; await _ctl.StopAsync(); Hooks.Hide(); ExitThread(); });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowForm();

        _ctl.Changed += () => OnUi(Refresh);
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await _ctl.ReloadAsync();
        if (_ctl.Config.Created) OnUi(() => { ShowForm(); _icon.ShowBalloonTip(6000, "رقيب", "أُنشئ ملف الإعدادات. اكتب عنوان الخادم وPIN ثم أعد التحميل.", ToolTipIcon.Info); });
        else if (_ctl.Problems.Length > 0) OnUi(ShowForm);
        if (_ctl.Config.Bool("RAQEEB_AUTOSTART") != Program.Autostart) { try { Program.SetAutostart(_ctl.Config.Bool("RAQEEB_AUTOSTART")); } catch { } }
    }

    private void OnUi(Action a) { try { if (_ui.IsHandleCreated) _ui.BeginInvoke(a); } catch { } }

    private void Refresh()
    {
        var s = (_ctl.Connected ? "🟢 " : "🟠 ") + _ctl.Status;
        _statusItem.Text = s.Length > 60 ? s[..60] + "…" : s;
        _icon.Text = ("رقيب — " + _ctl.Status);
        if (_icon.Text.Length > 60) _icon.Text = _icon.Text[..60];
        _form?.Refresh(_ctl);
    }

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed) { _form = new StatusForm(OpenEnv, async () => await _ctl.ReloadAsync()); _form.Refresh(_ctl); }
        _form.Show(); _form.WindowState = FormWindowState.Normal; _form.Activate();
    }

    private void OpenEnv()
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + _ctl.Config.Path + "\"") { UseShellExecute = true }); } catch { }
    }

    private void RunHelper(bool install)
    {
        var r = MessageBox.Show(install
            ? "سيُثبَّت «مساعد رقيب» كخدمة بصلاحية النظام ليتحكم بشاشة القفل (فتحها والكتابة عليها). تحتاج موافقة المسؤول. متابعة؟"
            : "إزالة مساعد القفل؟", "رقيب", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        if (r != DialogResult.Yes) return;
        Task.Run(() => Program.RunHelper(install)).ContinueWith(t => OnUi(() => _icon.ShowBalloonTip(5000, "رقيب", t.Result, ToolTipIcon.Info)));
    }

    protected override void Dispose(bool disposing) { if (disposing) { _icon.Dispose(); _ui.Dispose(); } base.Dispose(disposing); }
}

internal sealed class StatusForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight };
    private readonly TextBox _info = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 150, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, Font = new Font("Segoe UI", 9.5f) };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), HorizontalScrollbar = true, RightToLeft = RightToLeft.No };

    public StatusForm(Action openEnv, Action reload)
    {
        Text = "رقيب"; Width = 760; Height = 560; StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true, Margin = new Padding(4) }; b.Click += (_, _) => a(); return b; }
        bar.Controls.AddRange(new Control[] { B("فتح .env", openEnv), B("إعادة التحميل", reload) });
        Controls.Add(_log); Controls.Add(bar); Controls.Add(_info); Controls.Add(_status);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Refresh(Controller c)
    {
        _status.Text = (c.Connected ? "🟢 " : "🟠 ") + c.Status;
        var o = c.Options;
        string Y(bool b) => b ? "مفعّل" : "معطّل";
        _info.Text =
            $"الجهاز: {o.DeviceName} ({o.DeviceId})\r\nملف الإعدادات: {c.Config.Path}\r\n" +
            $"PIN: {(o.PinHash.Length > 0 ? "مضبوط" : "غير مضبوط ⚠")}   |   مساعد القفل: {(Controller.Helper.Installed ? "مثبّت" : "غير مثبّت")}\r\n" +
            $"إجراءات: {Y(o.AllowActions)}   لقطة: {Y(o.AllowScreenshot)}   فأرة/كيبورد: {Y(o.AllowInput)}   ملفات: {Y(o.AllowFiles)}\r\n" +
            $"فتح القفل: {Y(o.AllowUnlock)}   PowerShell: {Y(o.AllowShell)}   صندوق تلغرام: {Y(o.OutboxEnabled)}";
        _log.BeginUpdate(); _log.Items.Clear();
        List<string> copy; lock (c.Log) copy = c.Log.Take(200).ToList();
        foreach (var l in copy) _log.Items.Add(l);
        _log.EndUpdate();
    }
}
