using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Raqeeb;

/// <summary>ما يقدّمه البرنامج للوكيل: لقطة الشاشة والإشعار المحلي.</summary>
internal sealed class AppHost : IAgentHost
{
    private readonly Action<string, string> _balloon;
    public AppHost(Action<string, string> balloon) { _balloon = balloon; }
    public string Version => typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "?";

    public Task<byte[]?> ScreenshotJpegAsync(int maxWidth, int quality = 60) => Task.Run<byte[]?>(() =>
    {
        try
        {
            var b = Forms.SystemInformation.VirtualScreen;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
            var scale = Math.Min(1.0, (double)maxWidth / b.Width);
            using var small = new Bitmap(bmp, new Size(Math.Max(1, (int)(b.Width * scale)), Math.Max(1, (int)(b.Height * scale))));
            using var ms = new MemoryStream();
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
            var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            small.Save(ms, enc, ps);
            return ms.ToArray();
        }
        catch { return null; }
    });

    public Task NotifyAsync(string title, string text) { _balloon(title, text); return Task.CompletedTask; }
}

/// <summary>خطّافات تحتاج واجهة: الحافظة ← تلغرام، وغطاء الخصوصية (شاشة سوداء للجالسين بجانبك والمتحكّم يرى الحقيقية).</summary>
internal static class Hooks
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    /// <summary>خيط الواجهة (لازم للحافظة وللنوافذ). يُضبط من TrayApp.</summary>
    public static Forms.Control? Ui { get; set; }

    public static uint ClipboardSeq() => GetClipboardSequenceNumber();

    public static async Task<string> ClipboardToTelegram(string relay, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relay)) throw new InvalidOperationException("عنوان الخادم غير مضبوط");
        if (Ui is null) throw new InvalidOperationException("الواجهة غير جاهزة");
        string? text = null; List<string>? files = null; string? img = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                (text, files, img) = (ValueTuple<string?, List<string>?, string?>)Ui.Invoke(new Func<ValueTuple<string?, List<string>?, string?>>(() =>
                {
                    if (Forms.Clipboard.ContainsFileDropList())
                        return (null, Forms.Clipboard.GetFileDropList().Cast<string>().ToList(), null);
                    if (Forms.Clipboard.ContainsImage())
                    {
                        using var bmp = Forms.Clipboard.GetImage()!;
                        var tmp = Path.Combine(Path.GetTempPath(), "clip-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                        bmp.Save(tmp, ImageFormat.Png);
                        return (null, null, tmp);
                    }
                    if (Forms.Clipboard.ContainsText()) return (Forms.Clipboard.GetText(), null, null);
                    return (null, null, null);
                }));
                break;
            }
            catch (ExternalException) { await Task.Delay(120, ct); }
        }
        Task<string> Send(string p, string cap, CancellationToken c) => RelayNotifier.SendFileAsync(relay, token, p, cap, null, c);
        if (files is { Count: > 0 })
        {
            var names = new List<string>();
            foreach (var f in files.Where(f => File.Exists(f) || Directory.Exists(f)).Take(10))
            {
                await TelegramOutbox.SendAsync(f, "", Send, null, ct);
                names.Add(Path.GetFileName(f.TrimEnd('\\', '/')));
            }
            if (names.Count == 0) throw new InvalidOperationException("العناصر المنسوخة غير صالحة للإرسال");
            return "✈ أُرسل: " + string.Join("، ", names);
        }
        if (img is not null)
        {
            try { await TelegramOutbox.SendAsync(img, "", Send, null, ct); }
            finally { try { File.Delete(img); } catch { } }
            return "✈ أُرسلت الصورة";
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = text.Length > 1500 ? text[..1500] : text;
            if (!await RelayNotifier.SendAsync(relay, token, t)) throw new InvalidOperationException("تعذّر الإرسال (هل البوت مربوط؟)");
            return "✈ أُرسل النص/الرابط";
        }
        throw new InvalidOperationException("لم يُنسخ شيء — حدّد ملفاً أو نصاً ثم أعد المحاولة");
    }

    // ---------------- غطاء الخصوصية
    private static CoverForm? _cover;
    private static Forms.Timer? _guard;
    private static DateTime _since;

    private sealed class CoverForm : Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 /*TRANSPARENT*/ | 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/;
                return cp;
            }
        }
    }

    /// <summary>true ← الغطاء مستثنى من التقاط الشاشة (المتحكّم يرى الشاشة الحقيقية). false ← يظهر أسود في المعاينة أيضاً.</summary>
    public static bool Privacy(bool on, string color)
    {
        if (Ui is null) return false;
        return (bool)Ui.Invoke(new Func<bool>(() =>
        {
            Hide();
            if (!on) return true;
            Color c;
            try { c = ColorTranslator.FromHtml(color); } catch { c = Color.Black; }
            var f = new CoverForm
            {
                FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, StartPosition = Forms.FormStartPosition.Manual,
                Bounds = Forms.SystemInformation.VirtualScreen, BackColor = c, Opacity = 0.995,
            };
            f.Show();
            var excluded = SetWindowDisplayAffinity(f.Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+)
            _cover = f; _since = DateTime.UtcNow;
            // أمان: Ctrl+Alt+Shift+P على الجهاز يُزيله، ويزول وحده بعد ساعتين
            _guard = new Forms.Timer { Interval = 200 };
            _guard.Tick += (_, _) =>
            {
                var held = (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0
                           && (GetAsyncKeyState(0x10) & 0x8000) != 0 && (GetAsyncKeyState(0x50) & 0x8000) != 0;
                if (held || DateTime.UtcNow - _since > TimeSpan.FromHours(2)) Hide();
            };
            _guard.Start();
            return excluded;
        }));
    }

    public static void Hide()
    {
        try { _guard?.Stop(); _guard?.Dispose(); } catch { }
        _guard = null;
        try { _cover?.Close(); _cover?.Dispose(); } catch { }
        _cover = null;
    }
}
