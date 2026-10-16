using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Raqeeb;

/// <summary>
/// حقن مدخلات الفأرة/الكيبورد عبر SendInput. ملف مستقل بلا اعتماديات: يُترجَم داخل التطبيق ويُربط (Link) أيضاً في
/// «مساعد الفتح» ليعمل على شاشة القفل. الإحداثيات مُطبَّعة 0..1 من حجم الشاشة فلا تهمّ دقة المعاينة.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4, KEYEVENTF_EXTENDEDKEY = 1;
    private const uint MOVE = 0x0001, LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010, MDOWN = 0x0020, MUP = 0x0040,
        WHEEL = 0x0800, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;

    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    /// <summary>أسماء المفاتيح المسموحة من الجوال (قائمة بيضاء).</summary>
    private static readonly Dictionary<string, ushort> Keys = BuildKeys();
    private static Dictionary<string, ushort> BuildKeys()
    {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["enter"] = 0x0D, ["esc"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27, ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pagedown"] = 0x22, ["win"] = 0x5B, ["insert"] = 0x2D,
            ["volup"] = 0xAF, ["voldown"] = 0xAE, ["mute"] = 0xAD, ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1,
        };
        for (var i = 1; i <= 12; i++) d["f" + i] = (ushort)(0x6F + i);
        for (var c = 'a'; c <= 'z'; c++) d[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = (ushort)c;
        return d;
    }
    private static readonly HashSet<ushort> Extended = new() { 0x26, 0x28, 0x25, 0x27, 0x24, 0x23, 0x21, 0x22, 0x2E, 0x2D, 0x5B };
    private static readonly Dictionary<string, ushort> Mods = new(StringComparer.OrdinalIgnoreCase)
        { ["ctrl"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B };

    public static bool IsKnownKey(string name) => Keys.ContainsKey(name);

    private static void Mouse(int x, int y, uint flags, uint data = 0)
    {
        var i = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags } };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    private static void Key(ushort vk, ushort scan, uint flags)
    {
        var i = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } };
        SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());
    }

    private static int Abs(double n) => (int)Math.Round(Math.Clamp(n, 0, 1) * 65535);

    public static void MoveTo(double nx, double ny, bool virtualDesk)
        => Mouse(Abs(nx), Abs(ny), MOVE | ABSOLUTE | (virtualDesk ? VIRTUALDESK : 0));

    public static void Click(double nx, double ny, string button, int count, bool virtualDesk)
    {
        MoveTo(nx, ny, virtualDesk); Thread.Sleep(25);
        var (d, u) = button switch { "right" => (RDOWN, RUP), "middle" => (MDOWN, MUP), _ => (LDOWN, LUP) };
        for (var k = 0; k < Math.Clamp(count, 1, 2); k++) { Mouse(0, 0, d); Thread.Sleep(20); Mouse(0, 0, u); Thread.Sleep(40); }
    }

    public static void Drag(double x1, double y1, double x2, double y2, bool virtualDesk)
    {
        MoveTo(x1, y1, virtualDesk); Thread.Sleep(40);
        Mouse(0, 0, LDOWN); Thread.Sleep(60);
        const int steps = 12;
        for (var s = 1; s <= steps; s++) { MoveTo(x1 + (x2 - x1) * s / steps, y1 + (y2 - y1) * s / steps, virtualDesk); Thread.Sleep(15); }
        Thread.Sleep(40); Mouse(0, 0, LUP);
    }

    // زر مضغوط (للسحب بالتراك باد: نقرة+سحب). أمان: يُرفع تلقائياً بعد 15 ثانية إن انقطع الجوال.
    private static System.Threading.Timer? _holdTimer;
    private static string? _held;
    public static void Hold(string button, bool down)
    {
        var (d, u) = button switch { "right" => (RDOWN, RUP), "middle" => (MDOWN, MUP), _ => (LDOWN, LUP) };
        lock (typeof(InputInjector))
        {
            if (down)
            {
                if (_held is not null) return;
                Mouse(0, 0, d); _held = button;
                _holdTimer?.Dispose();
                _holdTimer = new System.Threading.Timer(_ => Hold(button, false), null, 15000, Timeout.Infinite);
            }
            else if (_held is not null)
            {
                var (_, up) = _held switch { "right" => (RDOWN, RUP), "middle" => (MDOWN, MUP), _ => (LDOWN, LUP) };
                Mouse(0, 0, up); _held = null; _holdTimer?.Dispose(); _holdTimer = null;
            }
        }
    }

    public static void ScrollH(int notches)
    {
        notches = Math.Clamp(notches, -30, 30);
        Mouse(0, 0, 0x1000, unchecked((uint)(notches * 120)));
    }

    /// <summary>dy موجب = للأسفل. وحدة = «نقرة» عجلة (120).</summary>
    public static void Scroll(int notches)
    {
        notches = Math.Clamp(notches, -30, 30);
        Mouse(0, 0, WHEEL, unchecked((uint)(-notches * 120)));
    }

    /// <summary>نص Unicode (VK_PACKET): لا يتأثر بلغة لوحة المفاتيح.</summary>
    public static void TypeText(string text)
    {
        foreach (var c in text.Length > 500 ? text[..500] : text)
        {
            if (c == '\n') { Tap(0x0D); continue; }
            if (c < 0x20) continue;
            Key(0, c, KEYEVENTF_UNICODE); Thread.Sleep(6); Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP); Thread.Sleep(6);
        }
    }

    private static void Tap(ushort vk)
    {
        var ext = Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0u;
        Key(vk, 0, ext); Thread.Sleep(15); Key(vk, 0, ext | KEYEVENTF_KEYUP);
    }

    /// <summary>اختصار: مفتاح واحد من القائمة البيضاء مع معدّلات (ctrl/alt/shift/win). يرمي لأي اسم غير معروف.</summary>
    public static void Press(string name, IEnumerable<string>? mods)
    {
        if (!Keys.TryGetValue(name ?? "", out var vk)) throw new InvalidOperationException("مفتاح غير مسموح: " + name);
        var held = new List<ushort>();
        try
        {
            foreach (var m in mods ?? Array.Empty<string>())
            {
                if (!Mods.TryGetValue(m, out var mv)) throw new InvalidOperationException("معدّل غير مسموح: " + m);
                if (held.Contains(mv)) continue;
                Key(mv, 0, mv == 0x5B ? KEYEVENTF_EXTENDEDKEY : 0u); held.Add(mv);
            }
            Thread.Sleep(15);
            Tap(vk);
        }
        finally
        {
            for (var i = held.Count - 1; i >= 0; i--) Key(held[i], 0, KEYEVENTF_KEYUP | (held[i] == 0x5B ? KEYEVENTF_EXTENDEDKEY : 0u));
        }
    }

    /// <summary>ينفّذ أمر إدخال واحد بصيغة JSON: {kind:move|click|drag|scroll|text|key, ...}. يعيد وصفاً قصيراً.</summary>
    public static string Execute(JsonObject a, bool virtualDesk)
    {
        double D(string k) => a[k]?.GetValue<double>() ?? 0.5;
        var kind = a["kind"]?.GetValue<string>() ?? "";
        switch (kind)
        {
            case "move": MoveTo(D("x"), D("y"), virtualDesk); return "move";
            case "click":
                {
                    var btn = a["button"]?.GetValue<string>() ?? "left";
                    if (btn is not ("left" or "right" or "middle")) throw new InvalidOperationException("زر غير معروف");
                    var cnt = a["count"]?.GetValue<int>() ?? 1;
                    Click(D("x"), D("y"), btn, cnt, virtualDesk); return "click";
                }
            case "drag": Drag(D("x"), D("y"), D("x2"), D("y2"), virtualDesk); return "drag";
            case "scroll":
                {
                    var sdy = a["dy"]?.GetValue<int>() ?? 0; var sdx = a["dx"]?.GetValue<int>() ?? 0;
                    if (sdy != 0) Scroll(sdy);
                    if (sdx != 0) ScrollH(sdx);
                    return "scroll";
                }
            case "down":
            case "up":
                {
                    var btn = a["button"]?.GetValue<string>() ?? "left";
                    if (btn is not ("left" or "right" or "middle")) throw new InvalidOperationException("زر غير معروف");
                    if (kind == "down" && a["x"] is not null) { MoveTo(D("x"), D("y"), virtualDesk); Thread.Sleep(15); }
                    Hold(btn, kind == "down"); return kind;
                }
            case "text":
                {
                    var t = a["text"]?.GetValue<string>() ?? "";
                    if (t.Length == 0 || t.Length > 500) throw new InvalidOperationException("النص فارغ أو أطول من 500");
                    TypeText(t); return "text";
                }
            case "key":
                {
                    var mods = (a["mods"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList();
                    if (mods is { Count: > 3 }) throw new InvalidOperationException("معدّلات كثيرة");
                    Press(a["name"]?.GetValue<string>() ?? "", mods); return "key";
                }
        }
        throw new InvalidOperationException("نوع إدخال غير معروف");
    }
}
