using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Raqeeb.Helper;

/// <summary>
/// يعمل داخل عملية بصلاحية SYSTEM على سطح مكتب Winlogon (شاشة القفل): يكتب الأحرف ويلتقط الشاشة.
/// يُستدعى فقط من الخدمة عبر <c>--inject</c>.
/// </summary>
internal static class Injector
{
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;
    private const ushort VK_RETURN = 0x0D, VK_SHIFT = 0x10;
    private const int UOI_NAME = 2;

    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr h, int idx, StringBuilder buf, int len, out int needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>يوقظ الشاشة إن كانت مطفأة (بعد القفل تنطفئ حسب مهلة الطاقة) قبل إرسال المفاتيح.</summary>
    private static void WakeDisplay()
    {
        SetThreadExecutionState(0x80000000u | 0x2 | 0x1);                                  // ES_CONTINUOUS|DISPLAY|SYSTEM
        SendMessageTimeout((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)(-1), 2, 1000, out _); // SC_MONITORPOWER = on
        mouse_event(0x0001, 1, 0, 0, IntPtr.Zero); Thread.Sleep(30);                       // حركة فأرة صغيرة
        mouse_event(0x0001, -1, 0, 0, IntPtr.Zero);
        Thread.Sleep(1500);                                                                // مهلة إضاءة الشاشة
    }

    public static string DesktopName()
    {
        var sb = new StringBuilder(256);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : "?";
    }

    private static bool Send(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };
        return SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>()) == 1;
    }

    private static void Tap(ushort vk) { Send(vk, 0, 0); Thread.Sleep(15); Send(vk, 0, KEYEVENTF_KEYUP); }

    /// <summary>حرف Unicode واحد (VK_PACKET): لا يتأثر بلغة لوحة المفاتيح على شاشة القفل.</summary>
    private static void TypeChar(char c) { Send(0, c, KEYEVENTF_UNICODE); Thread.Sleep(8); Send(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP); Thread.Sleep(8); }

    /// <summary>
    /// يوقظ الشاشة ويزيح «ستارة» شاشة القفل (الساعة) ثم يكتب السر ويضغط Enter. لا يسجّل السر في أي مكان.
    /// الستارة لا تزول بمفتاح Shift؛ تزول بنقرة فأرة أو Space/Enter. نستعمل النقرة لأنها بلا أثر إن كان
    /// مربع كلمة السر ظاهراً أصلاً (Space كانت ستُكتب داخل الحقل فتفسد كلمة السر وتحتسب إخفاقاً).
    /// </summary>
    public static int Unlock(string secret)
    {
        WakeDisplay();
        for (var i = 0; i < 2; i++)
        {
            Raqeeb.InputInjector.Click(0.5, 0.5, "left", 1, virtualDesk: false);
            Thread.Sleep(i == 0 ? 1100 : 700);   // انتهاء حركة الستارة وظهور مربع الدخول
        }
        var n = 0;
        foreach (var c in secret) { TypeChar(c); n++; }
        Thread.Sleep(120);
        Tap(VK_RETURN);
        return n;
    }

    /// <summary>لقطة JPEG للسطح الحالي لهذه العملية.</summary>
    public static byte[]? Snap(int maxWidth, int quality)
    {
        try
        {
            var w = GetSystemMetrics(0); var h = GetSystemMetrics(1);   // SM_CXSCREEN/CY
            if (w <= 0 || h <= 0) return null;
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
            var scale = Math.Min(1.0, (double)maxWidth / w);
            using var small = new Bitmap(bmp, new Size(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale))));
            using var ms = new MemoryStream();
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
            var ps = new EncoderParameters(1); ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            small.Save(ms, enc, ps);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
