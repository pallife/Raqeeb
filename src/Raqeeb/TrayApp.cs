using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Raqeeb;

/// <summary>
/// هوية رقيب (نفس لوحة التحكم): داكن افتراضياً بلمسة تيل للحالة الحيّة وبرتقالي للإجراء الرئيسي. RAQEEB_THEME=light للفاتح.
/// بلا مكتبات: رسم GDI+ مباشر لأزرار وشارات خفيفة بحركة مرور ناعمة.
/// </summary>
internal static class Skin
{
    public static bool Dark = true;
    public static Color Bg, Surface, Line, Ink, Soft, Brand, Cta, CtaInk, Ok, Warn, Bad, Hover;

    public static void Apply(string? theme)
    {
        Dark = !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        if (Dark)
        {
            Bg = Color.FromArgb(0x0A, 0x0E, 0x17); Surface = Color.FromArgb(0x11, 0x18, 0x27); Line = Color.FromArgb(0x1F, 0x2A, 0x3F);
            Ink = Color.FromArgb(0xE8, 0xEC, 0xF4); Soft = Color.FromArgb(0x8C, 0x98, 0xAE); Brand = Color.FromArgb(0x19, 0xD3, 0xAE);
            Cta = Color.FromArgb(0xFF, 0x6B, 0x3D); CtaInk = Color.FromArgb(0x1A, 0x0B, 0x05);
            Ok = Color.FromArgb(0x34, 0xD3, 0x99); Warn = Color.FromArgb(0xF5, 0xB9, 0x4D); Bad = Color.FromArgb(0xFF, 0x6B, 0x81); Hover = Color.FromArgb(0x17, 0x21, 0x3A);
        }
        else
        {
            Bg = Color.FromArgb(0xF5, 0xF7, 0xFA); Surface = Color.White; Line = Color.FromArgb(0xE1, 0xE6, 0xEE);
            Ink = Color.FromArgb(0x0E, 0x15, 0x22); Soft = Color.FromArgb(0x5D, 0x6A, 0x80); Brand = Color.FromArgb(0x0A, 0x9C, 0x80);
            Cta = Color.FromArgb(0xEE, 0x5A, 0x2C); CtaInk = Color.White;
            Ok = Color.FromArgb(0x0E, 0x9F, 0x6E); Warn = Color.FromArgb(0xB7, 0x79, 0x1F); Bad = Color.FromArgb(0xD1, 0x34, 0x4B); Hover = Color.FromArgb(0xEB, 0xEF, 0xF5);
        }
    }

    public static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static GraphicsPath Round(Rectangle r, int rad)
    {
        var d = rad * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    /// <summary>شريط عنوان داكن ليتّسق مع النافذة (ويندوز 10 1809+ و11).</summary>
    public static void TitleBar(Form f)
    {
        try { var v = Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref v, 4); DwmSetWindowAttribute(f.Handle, 19, ref v, 4); } catch { }
    }

    public static void Style(Form f)
    {
        f.BackColor = Bg; f.ForeColor = Ink; f.Font = new Font("Segoe UI", 10f);
        f.RightToLeft = RightToLeft.Yes; f.RightToLeftLayout = true;
        f.HandleCreated += (_, _) => TitleBar(f);
        // ظهور هادئ: تلاشٍ سريع بدل الظهور الفجائي
        var t = new System.Windows.Forms.Timer { Interval = 15 };
        f.VisibleChanged += (_, _) => { if (f.Visible) { f.Opacity = 0; t.Start(); } };
        t.Tick += (_, _) => { f.Opacity = Math.Min(1, f.Opacity + 0.14); if (f.Opacity >= 1) t.Stop(); };
    }
}

/// <summary>زر مسطّح مرسوم يدوياً: يتلوّن عند المرور بنعومة ويصغر قليلاً عند الضغط.</summary>
internal sealed class FlatBtn : Control
{
    public enum Kind { Primary, Outline, Ghost }
    private readonly Kind _kind;
    private float _h;
    private bool _over, _down;
    private readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };

    public FlatBtn(string text, Kind kind, Action click)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text; _kind = kind; Cursor = Cursors.Hand; TabStop = true;
        Font = new Font("Segoe UI", 10f, kind == Kind.Primary ? FontStyle.Bold : FontStyle.Regular);
        Height = 38; Margin = new Padding(0, 0, 8, 0);
        Width = TextRenderer.MeasureText(text, Font).Width + 40;
        Click += (_, _) => click();
        MouseEnter += (_, _) => { _over = true; _t.Start(); };
        MouseLeave += (_, _) => { _over = false; _down = false; _t.Start(); };
        MouseDown += (_, _) => { _down = true; Invalidate(); };
        MouseUp += (_, _) => { _down = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate(); LostFocus += (_, _) => Invalidate();
        _t.Tick += (_, _) =>
        {
            var target = _over ? 1f : 0f; _h += (target - _h) * 0.32f;
            if (Math.Abs(target - _h) < 0.02f) { _h = target; _t.Stop(); }
            Invalidate();
        };
    }

    protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Skin.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_down) r.Inflate(-1, -1);
        Color fill, border, fg;
        switch (_kind)
        {
            case Kind.Primary: fill = Skin.Mix(Skin.Cta, Color.White, _h * 0.14f); border = fill; fg = Skin.CtaInk; break;
            case Kind.Outline: fill = Skin.Mix(Skin.Surface, Skin.Hover, _h); border = Skin.Line; fg = Skin.Ink; break;
            default: fill = Skin.Mix(Skin.Bg, Skin.Hover, _h); border = fill; fg = Skin.Mix(Skin.Soft, Skin.Ink, _h); break;
        }
        if (Focused && ShowFocusCues) border = Skin.Brand;
        using (var path = Skin.Round(r, 9)) { using var b = new SolidBrush(fill); g.FillPath(b, path); using var p = new Pen(border); g.DrawPath(p, path); }
        TextRenderer.DrawText(g, Text, Font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.SingleLine);
    }
}

/// <summary>شارة صغيرة: نقطة ملوّنة + نص (حالة صلاحية أو تنبيه).</summary>
internal sealed class Chip : Control
{
    private readonly Color _accent;
    public Chip(string text, Color accent)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Text = text; _accent = accent; Font = new Font("Segoe UI", 9.5f); Margin = new Padding(0, 0, 8, 8);
        Height = 28; Width = TextRenderer.MeasureText(text, Font).Width + 14 + 7 + 8 + 14;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? Skin.Bg);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Skin.Round(r, 13))
        {
            using var b = new SolidBrush(Skin.Mix(Skin.Bg, _accent, Skin.Dark ? 0.14f : 0.10f)); g.FillPath(b, path);
            using var p = new Pen(Skin.Mix(Skin.Bg, _accent, 0.35f)); g.DrawPath(p, path);
        }
        using (var d = new SolidBrush(_accent)) g.FillEllipse(d, Width - 14 - 7, (Height - 7) / 2, 7, 7);
        var tr = new Rectangle(10, 0, Width - 14 - 7 - 8 - 10, Height);
        TextRenderer.DrawText(g, Text, Font, tr, Skin.Mix(Skin.Ink, _accent, 0.2f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.SingleLine);
    }
}

/// <summary>قائمة الصينية بنفس الألوان بدل الأبيض الافتراضي.</summary>
internal sealed class SkinRenderer : ToolStripProfessionalRenderer
{
    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Skin.Surface;
        public override Color ImageMarginGradientBegin => Skin.Surface;
        public override Color ImageMarginGradientMiddle => Skin.Surface;
        public override Color ImageMarginGradientEnd => Skin.Surface;
        public override Color MenuBorder => Skin.Line;
        public override Color MenuItemBorder => Skin.Hover;
        public override Color MenuItemSelected => Skin.Hover;
        public override Color MenuItemSelectedGradientBegin => Skin.Hover;
        public override Color MenuItemSelectedGradientEnd => Skin.Hover;
        public override Color MenuItemPressedGradientBegin => Skin.Hover;
        public override Color MenuItemPressedGradientEnd => Skin.Hover;
        public override Color SeparatorDark => Skin.Line;
        public override Color SeparatorLight => Skin.Line;
        public override Color CheckBackground => Skin.Hover;
        public override Color CheckSelectedBackground => Skin.Hover;
        public override Color CheckPressedBackground => Skin.Hover;
    }

    public SkinRenderer() : base(new Colors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Skin.Soft : Skin.Ink;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Skin.Soft; base.OnRenderArrow(e); }
}

/// <summary>أيقونة الصينية + نافذة الحالة. الإعداد الكامل في .env؛ النافذة للمتابعة السريعة فقط.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Control _ui = new();
    private readonly Controller _ctl;
    private readonly ToolStripMenuItem _statusItem = new("…") { Enabled = false };
    private readonly ToolStripMenuItem _helperItem = new("مساعد القفل");
    private StatusForm? _form;

    public TrayApp(string? envOverride)
    {
        _ui.CreateControl(); _ = _ui.Handle;
        Hooks.Ui = _ui;
        _icon = new NotifyIcon { Visible = true, Text = "رقيب", Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Shield };
        _ctl = new Controller((t, m) => OnUi(() => _icon.ShowBalloonTip(4000, t, m, ToolTipIcon.Info)), envOverride);
        Skin.Apply(_ctl.Config.Str("RAQEEB_THEME", "dark"));

        // قائمة قصيرة: الحالة، الاقتران، ثم كل ما يندر استعماله داخل «المزيد»
        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, Renderer = new SkinRenderer(), Font = new Font("Segoe UI", 10f), ShowImageMargin = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("الحالة والسجل", null, (_, _) => ShowForm());
        menu.Items.Add("اقتران الجوال", null, (_, _) => ShowPair());

        var more = new ToolStripMenuItem("المزيد");
        more.DropDown.RightToLeft = RightToLeft.Yes; more.DropDown.Renderer = new SkinRenderer(); more.DropDown.Font = menu.Font; ((ToolStripDropDownMenu)more.DropDown).ShowImageMargin = false;
        more.DropDownItems.Add("إعادة تحميل الإعدادات", null, async (_, _) => await _ctl.ReloadAsync());
        more.DropDownItems.Add("فتح ملف الإعدادات (.env)", null, (_, _) => OpenEnv());
        _helperItem.Click += (_, _) => RunHelper(!Controller.Helper.Installed);
        more.DropDownItems.Add(_helperItem);
        var auto = new ToolStripMenuItem("تشغيل مع ويندوز") { Checked = Program.Autostart, CheckOnClick = true };
        auto.CheckedChanged += (_, _) => { try { Program.SetAutostart(auto.Checked); } catch (Exception ex) { _icon.ShowBalloonTip(3000, "رقيب", ex.Message, ToolTipIcon.Warning); } };
        more.DropDownItems.Add(auto);
        menu.Items.Add(more);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("خروج", null, async (_, _) => { _icon.Visible = false; await _ctl.StopAsync(); Hooks.Hide(); ExitThread(); });
        menu.Opening += (_, _) => _helperItem.Text = Controller.Helper.Installed ? "إزالة مساعد القفل" : "تثبيت مساعد القفل (مسؤول)";
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
        var s = (_ctl.Connected ? "● " : "○ ") + _ctl.Status;
        _statusItem.Text = s.Length > 60 ? s[..60] + "…" : s;
        _icon.Text = ("رقيب — " + _ctl.Status);
        if (_icon.Text.Length > 60) _icon.Text = _icon.Text[..60];
        _form?.Refresh(_ctl);
    }

    private void ShowForm()
    {
        if (_form is null || _form.IsDisposed) { _form = new StatusForm(OpenEnv, async () => await _ctl.ReloadAsync(), ShowPair); _form.Refresh(_ctl); }
        _form.Show(); _form.WindowState = FormWindowState.Normal; _form.Activate();
    }

    private void ShowPair() { using var f = new PairForm(_ctl.Config); f.ShowDialog(); }

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

/// <summary>
/// نافذة الحالة: رأس بنقطة حيّة وسطرين، ثم شارات الصلاحيات (بدل كتلة نصّ)، ثم السجل، وزر رئيسي واحد.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly Panel _head = new() { Dock = DockStyle.Top, Height = 84 };
    private readonly FlowLayoutPanel _chips = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 4, 20, 0), WrapContents = true };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 24, IntegralHeight = false, HorizontalScrollbar = false, RightToLeft = RightToLeft.No };
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = 50 };
    private string _title = "…", _sub = ""; private Color _dot = Skin.Warn; private bool _live; private float _phase;
    private string _chipSig = "";
    private readonly Font _logText = new("Segoe UI", 9.5f);

    public StatusForm(Action openEnv, Action reload, Action pair)
    {
        Text = "رقيب"; Width = 620; Height = 560; MinimumSize = new Size(480, 420); StartPosition = FormStartPosition.CenterScreen;
        Skin.Style(this);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _head.BackColor = Skin.Bg; _head.Paint += PaintHead;
        _chips.BackColor = Skin.Bg;
        _log.BackColor = Skin.Surface; _log.ForeColor = Skin.Ink; _log.Font = new Font("Consolas", 9.5f);
        _log.DrawItem += DrawLogItem;

        var logBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8), BackColor = Skin.Bg };
        var logCard = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Skin.Line };
        logCard.Controls.Add(_log); logBox.Controls.Add(logCard);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(20, 13, 20, 0), BackColor = Skin.Bg };
        bar.Controls.Add(new FlatBtn("إعادة التحميل", FlatBtn.Kind.Primary, reload));
        bar.Controls.Add(new FlatBtn("اقتران الجوال", FlatBtn.Kind.Outline, pair));
        bar.Controls.Add(new FlatBtn("الإعدادات (.env)", FlatBtn.Kind.Ghost, openEnv));

        Controls.Add(logBox); Controls.Add(bar); Controls.Add(_chips); Controls.Add(_head);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };

        // نبضة هادئة للنقطة الخضراء فقط حين تكون النافذة ظاهرة والاتصال حيّ
        _pulse.Tick += (_, _) => { if (_live && Visible) { _phase = (_phase + 0.06f) % 6.2832f; _head.Invalidate(new Rectangle(0, 0, _head.Width, _head.Height)); } };
        VisibleChanged += (_, _) => { if (Visible) _pulse.Start(); else _pulse.Stop(); };
    }

    protected override void Dispose(bool disposing) { if (disposing) _pulse.Dispose(); base.Dispose(disposing); }

    private void PaintHead(object? s, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        const int pad = 20;
        var cx = _head.Width - pad - 8; var cy = 30;
        if (_live) { var a = (int)(40 + 30 * Math.Sin(_phase)); using var halo = new SolidBrush(Color.FromArgb(Math.Clamp(a, 0, 255), _dot)); g.FillEllipse(halo, cx - 12, cy - 12, 24, 24); }
        using (var d = new SolidBrush(_dot)) g.FillEllipse(d, cx - 6, cy - 6, 12, 12);
        var tx = new Rectangle(pad, 14, _head.Width - pad - 30 - pad, 32);
        TextRenderer.DrawText(g, _title, new Font("Segoe UI", 14f, FontStyle.Bold), tx, Skin.Ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        var sx = new Rectangle(pad, 46, _head.Width - pad - 30 - pad, 24);
        TextRenderer.DrawText(g, _sub, new Font("Segoe UI", 9.5f), sx, Skin.Soft, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        using var line = new Pen(Skin.Line); g.DrawLine(line, 0, _head.Height - 1, _head.Width, _head.Height - 1);
    }

    private void DrawLogItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var g = e.Graphics;
        using (var b = new SolidBrush(e.Index % 2 == 0 ? Skin.Surface : Skin.Mix(Skin.Surface, Skin.Bg, 0.35f))) g.FillRectangle(b, e.Bounds);
        var text = _log.Items[e.Index]?.ToString() ?? "";
        var time = text.Length >= 8 ? text[..8] : "";
        var rest = text.Length > 10 ? text[10..] : text;
        var warn = rest.StartsWith("⚠");
        // الوقت (أحادي العرض) في أقصى اليمين ثم النص بخط يدعم العربية ومحاذاة يمينية (اتجاه RTL فتستقيم علامات الترقيم)
        TextRenderer.DrawText(g, time, _log.Font, new Rectangle(e.Bounds.Right - 82, e.Bounds.Y, 70, e.Bounds.Height), Skin.Soft, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, rest, _logText, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 82 - 24, e.Bounds.Height), warn ? Skin.Warn : Skin.Ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    public new void Refresh(Controller c)
    {
        _live = c.Connected;
        _dot = c.Connected ? Skin.Ok : c.Problems.Length > 0 ? Skin.Bad : Skin.Warn;
        var o = c.Options;
        _title = c.Status;
        _sub = $"{o.DeviceName}  ·  {o.DeviceId}";
        _head.Invalidate();

        // الشارات: لا تُعاد بنيتها إلا إذا تغيّرت الحالة (يتجنّب الوميض مع كل سطر سجل)
        var helper = Controller.Helper.Installed;
        var sig = string.Join("", new[] { o.AllowActions, o.AllowScreenshot, o.AllowInput, o.AllowFiles, o.AllowUnlock, o.AllowShell, o.OutboxEnabled, helper }.Select(b => b ? '1' : '0')) + (o.PinHash.Length > 0);
        if (sig != _chipSig)
        {
            _chipSig = sig;
            _chips.SuspendLayout(); foreach (Control x in _chips.Controls.OfType<Control>().ToList()) { _chips.Controls.Remove(x); x.Dispose(); }
            if (o.PinHash.Length == 0) _chips.Controls.Add(new Chip("PIN غير مضبوط — الإجراءات مرفوضة", Skin.Warn));
            void Add(string name, bool on, bool risky = false) { if (on) _chips.Controls.Add(new Chip(name, risky ? Skin.Warn : Skin.Brand)); }
            Add("إجراءات", o.AllowActions); Add("لقطة الشاشة", o.AllowScreenshot); Add("فأرة وكيبورد", o.AllowInput); Add("ملفات", o.AllowFiles);
            Add("فتح القفل", o.AllowUnlock, true); Add("PowerShell", o.AllowShell, true); Add("صندوق تلغرام", o.OutboxEnabled);
            _chips.Controls.Add(new Chip(helper ? "مساعد القفل مثبّت" : "مساعد القفل غير مثبّت", helper ? Skin.Brand : Skin.Soft));
            _chips.ResumeLayout();
        }

        List<string> copy; lock (c.Log) copy = c.Log.Take(200).ToList();
        _log.BeginUpdate(); _log.Items.Clear(); foreach (var l in copy) _log.Items.Add(l); _log.EndUpdate();
    }
}

/// <summary>اقتران الجوال: رمز QR على بطاقة بيضاء (يلزم لقراءته) وجملة واحدة وزرّان؛ السرّ في الجزء بعد # فلا يصل أي خادم.</summary>
internal sealed class PairForm : Form
{
    public PairForm(Config cfg)
    {
        Text = "اقتران الجوال"; Width = 420; Height = 600; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        Skin.Style(this);
        var link = cfg.PairLink();

        var qrHost = new Panel { Dock = DockStyle.Top, Height = 340, Padding = new Padding(40, 24, 40, 8), BackColor = Skin.Bg };
        var qrCard = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
        qrCard.Paint += (_, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; };
        var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
        if (link.Length > 0)
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(link, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
            pb.Image = Image.FromStream(new MemoryStream(png));
        }
        qrCard.Controls.Add(pb); qrHost.Controls.Add(qrCard);

        var note = new Label
        {
            Dock = DockStyle.Top, Height = 64, Padding = new Padding(24, 8, 24, 0), TextAlign = ContentAlignment.TopCenter, ForeColor = Skin.Soft,
            Text = link.Length == 0 ? "أضف RAQEEB_RELAY_URL وRAQEEB_SECRET في ملف .env أولاً." : "امسح الرمز بكاميرا الجوال لفتح رقيب مقترناً بهذا الجهاز.",
        };
        var warn = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0), BackColor = Skin.Bg, WrapContents = false };
        if (link.Length > 0) { var chip = new Chip("الرابط سرّي — لا تشاركه", Skin.Warn) { Margin = new Padding(0) }; warn.Controls.Add(chip); warn.Padding = new Padding((Width - chip.Width) / 2 - 8, 0, 0, 0); warn.FlowDirection = FlowDirection.LeftToRight; }

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(24, 13, 24, 0), BackColor = Skin.Bg };
        bar.Controls.Add(new FlatBtn("نسخ الرابط", FlatBtn.Kind.Outline, () => { if (link.Length > 0) Clipboard.SetText(link); }));
        bar.Controls.Add(new FlatBtn("نسخ قيمة الخادم", FlatBtn.Kind.Ghost, () => Clipboard.SetText("RAQEEB_RELAY_AUTH_SHA256=" + cfg.WebTokenHash())));

        Controls.Add(bar); Controls.Add(warn); Controls.Add(note); Controls.Add(qrHost);
    }
}
