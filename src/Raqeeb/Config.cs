using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Raqeeb;

/// <summary>
/// إعدادات رقيب من ملف <c>.env</c> (سطر KEY=VALUE، والتعليقات تبدأ بـ #). الترتيب: RAQEEB_ENV ثم بجانب الـexe ثم %APPDATA%\Raqeeb.
/// متغيرات البيئة الحقيقية تتغلّب على الملف. الملف يحوي أسراراً — لا يُرفع إلى GitHub (موجود في .gitignore).
/// </summary>
public sealed class Config
{
    public string Path { get; private set; } = "";
    /// <summary>true ← أُنشئ الملف الآن بمفاتيح عشوائية (أول تشغيل).</summary>
    public bool Created { get; private set; }
    private readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Raqeeb");

    /// <summary>يبحث عن .env؛ إن لم يوجد ينشئ نسخة بمفتاح ورمز عشوائيين في %APPDATA%\Raqeeb.</summary>
    public static Config Load(string? explicitPath = null)
    {
        var candidates = new List<string>();
        if (explicitPath is not null) candidates.Add(explicitPath);
        var fromEnv = Environment.GetEnvironmentVariable("RAQEEB_ENV");
        if (!string.IsNullOrWhiteSpace(fromEnv)) candidates.Add(fromEnv);
        candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, ".env"));
        candidates.Add(System.IO.Path.Combine(DefaultDir, ".env"));
        var path = candidates.FirstOrDefault(File.Exists);
        var created = false;
        if (path is null) { path = System.IO.Path.Combine(DefaultDir, ".env"); WriteTemplate(path); created = true; }
        var c = new Config { Path = path, Created = created };
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            var val = line[(i + 1)..].Trim();
            // تعليق في نهاية السطر (بعد مسافة + #) خارج علامات الاقتباس
            if (val.Length > 0 && val[0] is not ('"' or '\'')) { var h = val.IndexOf(" #", StringComparison.Ordinal); if (h >= 0) val = val[..h].TrimEnd(); }
            if (val.Length >= 2 && (val[0] == '"' && val[^1] == '"' || val[0] == '\'' && val[^1] == '\'')) val = val[1..^1];
            c._v[line[..i].Trim()] = val;
        }
        return c;
    }

    public string Str(string key, string def = "") =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } e ? e : _v.TryGetValue(key, out var v) && v.Length > 0 ? v : def;

    public bool Bool(string key, bool def = false) => Str(key).ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => def,
    };

    public int Int(string key, int def) => int.TryParse(Str(key), out var n) ? n : def;

    /// <summary>يحدّث قيمة في الملف (يحافظ على باقي الأسطر والتعليقات).</summary>
    public void Set(string key, string value)
    {
        _v[key] = value;
        try
        {
            var lines = File.Exists(Path) ? File.ReadAllLines(Path, Encoding.UTF8).ToList() : new List<string>();
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) lines[idx] = key + "=" + value; else lines.Add(key + "=" + value);
            File.WriteAllLines(Path, lines, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>مفتاح الوكيل: 32 بايت بصيغة hex (64 حرفاً) أو base64.</summary>
    public byte[]? AgentKey()
    {
        var s = Str("RAQEEB_AGENT_KEY").Trim();
        if (s.Length == 0) return null;
        try
        {
            if (s.Length == 64 && s.All(Uri.IsHexDigit)) return Convert.FromHexString(s);
            var b = Convert.FromBase64String(s);
            return b.Length == 32 ? b : null;
        }
        catch { return null; }
    }

    /// <summary>أخطاء الإعداد التي تمنع التشغيل (فارغة = سليم).</summary>
    public List<string> Problems()
    {
        var p = new List<string>();
        var url = Str("RAQEEB_RELAY_URL");
        if (url.Length == 0 || url.Contains("your-relay.example.com")) p.Add("RAQEEB_RELAY_URL: اكتب عنوان خادم الويب.");
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) p.Add("RAQEEB_RELAY_URL ليس رابط http(s) صالحاً.");
        if (Str("RAQEEB_WEB_TOKEN").Length < 16) p.Add("RAQEEB_WEB_TOKEN فارغ أو قصير.");
        if (AgentKey() is null) p.Add("RAQEEB_AGENT_KEY يجب أن يكون 32 بايت (64 حرف hex أو base64).");
        var pin = Str("RAQEEB_PIN");
        if (pin.Length is > 0 and < 4) p.Add("RAQEEB_PIN أقل من 4 خانات.");
        return p;
    }

    public AgentOptions ToOptions()
    {
        var o = new AgentOptions
        {
            Enabled = Bool("RAQEEB_ENABLED", true),
            DeviceId = Str("RAQEEB_DEVICE_ID", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)))[..12].ToLowerInvariant()),
            DeviceName = Str("RAQEEB_DEVICE_NAME", Environment.MachineName),
            AllowActions = Bool("RAQEEB_ALLOW_ACTIONS", true),
            AllowScreenshot = Bool("RAQEEB_ALLOW_SCREENSHOT", true),
            AllowShell = Bool("RAQEEB_ALLOW_SHELL"),
            AllowInput = Bool("RAQEEB_ALLOW_INPUT", true),
            AllowFiles = Bool("RAQEEB_ALLOW_FILES", true),
            AllowUnlock = Bool("RAQEEB_ALLOW_UNLOCK"),
            FileRoots = Str("RAQEEB_FILE_ROOTS"),
            NotifyRemote = Bool("RAQEEB_NOTIFY_TELEGRAM", true),
            AwakeReminderMinutes = Int("RAQEEB_AWAKE_REMINDER_MIN", 30),
            AwakeMaxHours = Int("RAQEEB_AWAKE_MAX_HOURS", 8),
            OutboxEnabled = Bool("RAQEEB_OUTBOX"),
        };
        var pin = Str("RAQEEB_PIN");
        if (pin.Length >= 4) (o.PinSalt, o.PinHash) = AgentCrypto.HashPin(pin);
        return o;
    }

    private static void WriteTemplate(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("env.example")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        var text = r.ReadToEnd();
        string Hex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        text = text.Replace("RAQEEB_WEB_TOKEN=\n", "RAQEEB_WEB_TOKEN=" + Hex() + "\n").Replace("RAQEEB_WEB_TOKEN=\r\n", "RAQEEB_WEB_TOKEN=" + Hex() + "\r\n")
                   .Replace("RAQEEB_AGENT_KEY=\n", "RAQEEB_AGENT_KEY=" + Hex() + "\n").Replace("RAQEEB_AGENT_KEY=\r\n", "RAQEEB_AGENT_KEY=" + Hex() + "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
