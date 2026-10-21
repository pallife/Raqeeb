using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raqeeb;

/// <summary>إعدادات التحكم عن بُعد بهذا الجهاز من الجوال. كل شيء مغلق افتراضياً.</summary>
public class AgentOptions
{
    public bool Enabled { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string DeviceName { get; set; } = "";
    /// <summary>تفعيل الإجراءات (قفل، سكون، إيقاف، إعادة تشغيل، إنهاء برنامج). تتطلب PIN.</summary>
    public bool AllowActions { get; set; } = true;
    public bool AllowScreenshot { get; set; }
    /// <summary>تنفيذ أوامر PowerShell — الأخطر، مغلق افتراضياً، ويتطلب PIN وكل أمر يُسجَّل.</summary>
    public bool AllowShell { get; set; }
    /// <summary>استمرار الاستماع بعد قفل الخزنة (مفتاح الوكيل فقط يبقى في الذاكرة).</summary>
    public bool KeepWhileLocked { get; set; } = true;
    public string PinSalt { get; set; } = "";
    public string PinHash { get; set; } = "";
    /// <summary>السماح بفتح قفل الشاشة من الجوال (يحتاج «مساعد الفتح» المثبّت بصلاحية مسؤول). مغلق افتراضياً.</summary>
    public bool AllowUnlock { get; set; }
    /// <summary>
    /// سرّ الدخول إلى ويندوز (كلمة السر أو PIN الدخول) للفتح بضغطة واحدة. يبقى داخل الخزنة المشفّرة فقط؛
    /// يقرؤه المتصفح بعد فك الخزنة ويرسله مشفّراً مع الأمر — الوكيل لا يحتفظ بنسخة منه.
    /// </summary>
    public string UnlockSecret { get; set; } = "";
    /// <summary>إرسال إشعار تلغرام (عبر خادم الويب) عند الأوامر الحساسة: فتح القفل، الإيقاف، PowerShell، بدء معاينة الشاشة.</summary>
    public bool NotifyRemote { get; set; } = true;
    /// <summary>التحكم بالفأرة والكيبورد من الجوال (يعادل التحكم الكامل بالجهاز). مغلق افتراضياً.</summary>
    public bool AllowInput { get; set; }
    /// <summary>تصفّح مجلدات مسموحة، تنزيل/رفع ملفات، فتحها، إرسالها لتلغرام، وتنزيل روابط إلى الجهاز. مغلق افتراضياً.</summary>
    public bool AllowFiles { get; set; }
    /// <summary>مجلدات مسموحة إضافية/بديلة مفصولة بـ ; (فارغ = سطح المكتب، المستندات، التنزيلات، الصور، الفيديو، الموسيقى).</summary>
    public string FileRoots { get; set; } = "";
    /// <summary>كل كم دقيقة يذكّرك إشعار تلغرام بأن «منع السكون» مفعّل.</summary>
    public int AwakeReminderMinutes { get; set; } = 30;
    /// <summary>أقصى مدة افتراضية لمنع السكون (ساعات) قبل الإلغاء التلقائي.</summary>
    public int AwakeMaxHours { get; set; } = 8;
    /// <summary>صندوق الإرسال: أي ملف/مجلد يُلقى في «المستندات\Raqeeb-Outbox» يُرسَل تلقائياً إلى بوت تلغرام.</summary>
    public bool OutboxEnabled { get; set; }
}

/// <summary>نتيجة محاولة فتح القفل من المساعد.</summary>
public sealed record UnlockResult(bool Ok, string Message, byte[]? Jpeg);

/// <summary>مساعد الفتح (خدمة LocalSystem منفصلة): يكتب السر على شاشة القفل ويلتقط الشاشة المقفلة.</summary>
public interface IUnlockHelper
{
    bool Installed { get; }
    Task<UnlockResult> UnlockAsync(string secret, int snapWidth, CancellationToken ct = default);
    Task<byte[]?> SnapAsync(int width, int quality, CancellationToken ct = default);
    /// <summary>إدخال فأرة/كيبورد على شاشة القفل عبر المساعد.</summary>
    Task<UnlockResult> InputAsync(JsonObject args, CancellationToken ct = default) => Task.FromResult(new UnlockResult(false, "غير مدعوم", null));
}

public static class AgentCrypto
{
    /// <summary>مفتاح الوكيل مشتق من مفتاح الخزنة: يملكه التطبيق والمتصفح فقط، لا الخادم.</summary>
    public static byte[] DeriveKey(byte[] vaultKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, vaultKey, 32, info: Encoding.ASCII.GetBytes("control-panel/agent/v1"));

    /// <summary>nonce(12) + نص مشفّر + وسم(16). AAD يربط الرسالة بمعرّفها واتجاهها (أمر/ردّ).</summary>
    public static byte[] Seal(byte[] key, byte[] plain, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var g = new AesGcm(key, 16);
        g.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        var o = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(o, 0); cipher.CopyTo(o, 12); tag.CopyTo(o, 12 + cipher.Length);
        return o;
    }

    public static byte[]? Open(byte[] key, byte[] sealedBytes, string aad)
    {
        if (sealedBytes.Length < 12 + 16) return null;
        try
        {
            var n = sealedBytes.AsSpan(0, 12); var c = sealedBytes.AsSpan(12, sealedBytes.Length - 28); var t = sealedBytes.AsSpan(sealedBytes.Length - 16, 16);
            var plain = new byte[c.Length];
            using var g = new AesGcm(key, 16);
            g.Decrypt(n, c, t, plain, Encoding.UTF8.GetBytes(aad));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    public static (string Salt, string Hash) HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32)));
    }

    public static bool CheckPin(string pin, string salt, string hash)
    {
        if (salt.Length == 0 || hash.Length == 0 || string.IsNullOrEmpty(pin)) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(salt), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(hash));
    }
}

/// <summary>ما يحتاجه الوكيل من التطبيق (واجهة WPF تنفّذه).</summary>
public interface IAgentHost
{
    string Version { get; }
    Task<byte[]?> ScreenshotJpegAsync(int maxWidth, int quality = 60);
    Task NotifyAsync(string title, string text);
}

/// <summary>العمليات على النظام — قابلة للاستبدال في الاختبارات فلا يُغلق جهازك أثناء الاختبار.</summary>
public interface IAgentSystem
{
    void Run(string file, string args);
    void Suspend(bool hibernate);
    List<(int Pid, string Name, long WorkingSet)> Processes();
    bool Kill(int pid, string expectedName);
    Task<(int Code, string Output)> PowerShellAsync(string script, int timeoutSec);
    /// <summary>هل شاشة المستخدم مقفلة (سطح الإدخال ليس سطح المستخدم)؟</summary>
    bool IsLocked();
}

public sealed class WindowsAgentSystem : IAgentSystem
{
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeup);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr h);
    /// <summary>OpenInputDesktop يفشل حين يكون سطح الإدخال هو Winlogon (شاشة القفل/UAC) — الطريقة المعتمدة لكشف القفل.</summary>
    public bool IsLocked()
    {
        var h = OpenInputDesktop(0, false, 0x0100 /*DESKTOP_SWITCHDESKTOP*/);
        if (h == IntPtr.Zero) return true;
        CloseDesktop(h);
        // الشاشة مطفأة بعد القفل: قد ينجح OpenInputDesktop رغم القفل؛ LogonUI يعمل ما دامت شاشة القفل قائمة.
        try
        {
            var me = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("LogonUI"))
                using (p) if (p.SessionId == me) return true;
        }
        catch { }
        return false;
    }
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "system", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "explorer", "dwm", "raqeeb", "fontdrvhost", "registry" };

    public void Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false };
        Process.Start(psi);
    }
    public void Suspend(bool hibernate) => SetSuspendState(hibernate, false, false);
    public List<(int, string, long)> Processes()
    {
        var res = new List<(int, string, long)>();
        foreach (var p in Process.GetProcesses())
        {
            try { res.Add((p.Id, p.ProcessName, p.WorkingSet64)); } catch { }
            finally { p.Dispose(); }
        }
        return res;
    }
    public bool Kill(int pid, string expectedName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Equals(expectedName, StringComparison.OrdinalIgnoreCase) || Protected.Contains(p.ProcessName)) return false;
            p.Kill(true); return true;
        }
        catch { return false; }
    }
    public async Task<(int, string)> PowerShellAsync(string script, int timeoutSec)
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return (-1, "انتهت المهلة (" + timeoutSec + "ث)"); }
        var text = (await o) + (await e);
        return (p.ExitCode, text.Length > 64 * 1024 ? text[..(64 * 1024)] + "\n… (اقتُطع)" : text);
    }
}

/// <summary>
/// يستقبل أوامر مشفّرة من الجوال عبر الخادم (الذي لا يفك شيئاً) وينفّذ مجموعة محدودة بعد التحقق.
/// الطبقات: ① مفتاح الوكيل (GCM يثبت أن المرسل يملك الخزنة) ② وقت الأمر ومنع إعادة التشغيل ③ بوابة الإجراءات والـPIN ④ قفل بعد 5 أخطاء.
/// </summary>
public sealed partial class AgentService
{
    private readonly byte[] _key; private readonly AgentOptions _opt; private readonly IAgentHost _host; private readonly IAgentSystem _sys;
    private readonly Func<DateTimeOffset> _now;
    private readonly Queue<string> _seen = new(); private readonly HashSet<string> _seenSet = new();
    private int _pinFails; private DateTimeOffset _lockedUntil;
    private readonly DeviceSampler _sampler = new();
    private readonly IUnlockHelper? _helper;
    private int _unlockFails; private DateTimeOffset _unlockBlockedUntil, _lastShotNote;
    public Action<string>? Audit { get; set; }
    /// <summary>إشعار خارجي (تلغرام عبر المرحّل) للأوامر الحساسة. الأخطاء تُبتلع.</summary>
    public Func<string, Task>? Notifier { get; set; }
    /// <summary>فاصل فحص «هل فُتحت الشاشة؟» بعد كتابة السر (للاختبارات).</summary>
    public int UnlockPollMs { get; set; } = 500;

    public AgentService(byte[] agentKey, AgentOptions opt, IAgentHost host, IAgentSystem sys, Func<DateTimeOffset>? now = null, IUnlockHelper? helper = null)
    { _key = agentKey; _opt = opt; _host = host; _sys = sys; _now = now ?? (() => DateTimeOffset.UtcNow); _helper = helper; }

    private void Note(string text)
    {
        if (!_opt.NotifyRemote || Notifier is null) return;
        var n = Notifier;
        _ = Task.Run(async () => { try { await n(text); } catch { } });
    }

    private static readonly string[] SafeOps = { "ping", "status", "processes", "connections", "awake_off" };
    private static readonly string[] ActionOps = { "lock", "sleep", "hibernate", "shutdown", "restart", "cancel_shutdown", "kill", "notify", "awake_on" };
    private static readonly string[] NoteOps = { "shutdown", "restart", "shell", "hibernate" };

    /// <summary>يعيد رداً مشفّراً، أو null إن لم يكن الأمر موثوقاً (يُهمل بصمت).</summary>
    public async Task<byte[]?> HandleAsync(string id, byte[] sealedCmd)
    {
        var plain = AgentCrypto.Open(_key, sealedCmd, "cmd:" + id);
        if (plain is null) return null; // ليس من صاحب الخزنة
        JsonNode? n; try { n = JsonNode.Parse(plain); } catch { return null; }
        if (n is not JsonObject cmd) return null;
        var ts = cmd["ts"]?.GetValue<long>() ?? 0;
        if (Math.Abs(_now().ToUnixTimeSeconds() - ts) > 120) return Reply(id, false, "انتهت صلاحية الأمر (فرق ساعة الجهازين)");
        if (!_seenSet.Add(id)) return null;
        _seen.Enqueue(id); if (_seen.Count > 500) _seenSet.Remove(_seen.Dequeue());

        var op = cmd["op"]?.GetValue<string>() ?? "";
        var args = cmd["args"] as JsonObject ?? new JsonObject();
        var pin = cmd["pin"]?.GetValue<string>() ?? "";
        if (!_opt.Enabled) return Reply(id, false, "التحكم عن بُعد معطّل في التطبيق");

        try
        {
            if (SafeOps.Contains(op)) return Reply(id, true, data: await ReadOpAsync(op));

            var tier2 = op is "screenshot" or "shell" or "unlock" or "input" or "set_caps" or "quick_send" or "privacy" || FileOps.Contains(op);
            if (!ActionOps.Contains(op) && !tier2) return Reply(id, false, "أمر غير معروف");
            if (op == "shell" && !_opt.AllowShell) return Reply(id, false, "أوامر PowerShell معطّلة في إعدادات التطبيق");
            if (op == "screenshot" && !_opt.AllowScreenshot) return Reply(id, false, "لقطة الشاشة معطّلة في إعدادات التطبيق");
            if (op == "unlock" && !_opt.AllowUnlock) return Reply(id, false, "فتح القفل عن بُعد معطّل في إعدادات التطبيق");
            if (op == "input" && !_opt.AllowInput) return Reply(id, false, "التحكم بالفأرة والكيبورد معطّل في إعدادات التطبيق");
            if ((op is "quick_send" or "privacy") && !_opt.AllowInput) return Reply(id, false, "التحكم بالفأرة والكيبورد معطّل في إعدادات التطبيق");
            if (op == "quick_send" && !_opt.AllowFiles) return Reply(id, false, "إدارة الملفات عن بُعد معطّلة في إعدادات التطبيق");
            if (FileOps.Contains(op) && !_opt.AllowFiles) return Reply(id, false, "إدارة الملفات عن بُعد معطّلة في إعدادات التطبيق");
            if (!tier2 && !_opt.AllowActions) return Reply(id, false, "الإجراءات معطّلة في إعدادات التطبيق");
            if (_opt.PinHash.Length == 0) return Reply(id, false, "اضبط رمز PIN من التطبيق أولاً (الإجراءات تتطلب PIN)");
            if (_now() < _lockedUntil) return Reply(id, false, $"قُفل الإدخال حتى {_lockedUntil.ToLocalTime():HH:mm} بعد محاولات خاطئة");
            if (!AgentCrypto.CheckPin(pin, _opt.PinSalt, _opt.PinHash))
            {
                if (++_pinFails >= 5) { _lockedUntil = _now().AddMinutes(15); _pinFails = 0; Audit?.Invoke("قُفل الوكيل 15 دقيقة بعد 5 رموز PIN خاطئة"); Note($"🚨 «{Environment.MachineName}»: 5 رموز PIN خاطئة من الجوال — قُفل التحكم 15 دقيقة."); }
                return Reply(id, false, "رمز PIN غير صحيح");
            }
            _pinFails = 0;
            var data = await ExtraActionAsync(op, args) ?? await ActionAsync(op, args);
            if (NoteOps.Contains(op)) Note($"⚠ أمر عن بُعد على «{Environment.MachineName}»: {op}");
            if (op != "input") Audit?.Invoke($"أمر عن بُعد: {op}" + (op.StartsWith("fs_") ? " — " + (args["path"]?.GetValue<string>() ?? args["q"]?.GetValue<string>() ?? "") : op == "fetch" ? " — " + (args["url"]?.GetValue<string>() ?? "") : "") +(op == "shell" ? " — " + (args["cmd"]?.GetValue<string>() ?? "")[..Math.Min(200, (args["cmd"]?.GetValue<string>() ?? "").Length)] : ""));
            return Reply(id, true, data: data);
        }
        catch (Exception ex) { return Reply(id, false, ex.Message); }
    }

    private byte[] Reply(string id, bool ok, string? error = null, JsonNode? data = null)
    {
        var o = new JsonObject { ["ok"] = ok };
        if (error is not null) o["error"] = error;
        if (data is not null) o["data"] = data;
        return AgentCrypto.Seal(_key, Encoding.UTF8.GetBytes(o.ToJsonString()), "res:" + id);
    }

    private async Task<JsonNode> ReadOpAsync(string op)
    {
        switch (op)
        {
            case "awake_off":
                return SetAwake(false, 0);
            case "ping":
                return new JsonObject { ["pong"] = true, ["version"] = _host.Version, ["machine"] = Environment.MachineName };
            case "status":
                {
                    var s = await Task.Run(() => _sampler.Sample());
                    var o = new JsonObject
                    {
                        ["machine"] = s.Machine, ["user"] = s.User, ["os"] = s.Os, ["version"] = _host.Version, ["cores"] = s.Cores,
                        ["cpu"] = Math.Round(s.CpuPercent, 1), ["ramTotal"] = s.RamTotal, ["ramAvailable"] = s.RamAvailable,
                        ["uptimeSec"] = (long)s.Uptime.TotalSeconds, ["battery"] = s.BatteryPercent, ["ac"] = s.OnAcPower,
                        ["deviceClass"] = DeviceSampler.Classify(s.RamTotal, s.Cores).ToString(),
                        ["disks"] = new JsonArray(s.Disks.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["total"] = d.TotalBytes, ["free"] = d.FreeBytes }).ToArray()),
                        ["advice"] = new JsonArray(DeviceSampler.Advise(s).Select(a => (JsonNode)new JsonObject { ["sev"] = a.Severity, ["title"] = a.Title, ["detail"] = a.Detail }).ToArray()),
                        ["locked"] = await Task.Run(_sys.IsLocked),
                        ["caps"] = new JsonObject
                        {
                            ["actions"] = _opt.AllowActions, ["screenshot"] = _opt.AllowScreenshot, ["shell"] = _opt.AllowShell, ["pin"] = _opt.PinHash.Length > 0,
                            ["unlock"] = _opt.AllowUnlock, ["unlockReady"] = _helper?.Installed == true,
                            ["input"] = _opt.AllowInput && Desktop is not null, ["files"] = _opt.AllowFiles, ["telegram"] = TelegramFile is not null, ["quick"] = ClipboardToTelegram is not null, ["privacy"] = PrivacyScreen is not null, ["privacyOn"] = _privacyOn,
                        },
                        ["awake"] = AwakeInfo(),
                    };
                    return o;
                }
            case "processes":
                {
                    var top = _sys.Processes().OrderByDescending(p => p.WorkingSet).Take(25)
                        .Select(p => (JsonNode)new JsonObject { ["pid"] = p.Pid, ["name"] = p.Name, ["mem"] = p.WorkingSet }).ToArray();
                    return new JsonObject { ["items"] = new JsonArray(top), ["count"] = _sys.Processes().Count };
                }
            case "connections":
                {
                    var conns = await Task.Run(NetworkMonitor.Snapshot);
                    var f = NetExplain.Analyze(conns, _ => null);
                    var flagged = f.Where(x => x.Risk != "low").Take(30).Select(x => (JsonNode)new JsonObject
                    { ["process"] = x.Conn.Process, ["remote"] = $"{x.Conn.RemoteAddr}:{x.Conn.RemotePort}", ["risk"] = x.Risk, ["what"] = x.What, ["advice"] = x.Advice }).ToArray();
                    return new JsonObject
                    {
                        ["total"] = f.Count, ["high"] = f.Count(x => x.Risk == "high"), ["medium"] = f.Count(x => x.Risk == "medium"),
                        ["established"] = f.Count(x => x.Conn.State == "ESTABLISHED"), ["listening"] = f.Count(x => x.Direction == "listen"),
                        ["flagged"] = new JsonArray(flagged),
                    };
                }
        }
        throw new InvalidOperationException("أمر غير معروف");
    }

    private async Task<JsonNode> ActionAsync(string op, JsonObject a)
    {
        switch (op)
        {
            case "lock": _sys.Run("rundll32.exe", "user32.dll,LockWorkStation"); return new JsonObject { ["done"] = "قُفلت الشاشة" };
            case "sleep": _sys.Suspend(false); return new JsonObject { ["done"] = "الجهاز ينتقل للسكون" };
            case "hibernate": _sys.Suspend(true); return new JsonObject { ["done"] = "الجهاز يدخل السبات" };
            case "shutdown":
            case "restart":
                {
                    var delay = Math.Clamp(a["delay"]?.GetValue<int>() ?? 30, 5, 600);
                    _sys.Run("shutdown.exe", $"{(op == "shutdown" ? "/s" : "/r")} /t {delay} /c \"أمر عن بُعد من رقيب\"");
                    return new JsonObject { ["done"] = $"سيُ{(op == "shutdown" ? "طفأ" : "عاد تشغيل")} الجهاز بعد {delay} ثانية — يمكن الإلغاء بـ cancel_shutdown", ["delay"] = delay };
                }
            case "cancel_shutdown": _sys.Run("shutdown.exe", "/a"); return new JsonObject { ["done"] = "أُلغي الإيقاف المجدول" };
            case "kill":
                {
                    var pid = a["pid"]?.GetValue<int>() ?? 0; var name = a["name"]?.GetValue<string>() ?? "";
                    if (pid <= 4 || name.Length == 0) throw new InvalidOperationException("حدّد pid والاسم");
                    return _sys.Kill(pid, name) ? new JsonObject { ["done"] = $"أُنهي {name} ({pid})" } : throw new InvalidOperationException("تعذّر الإنهاء (محمي أو تغيّر الاسم)");
                }
            case "notify":
                await _host.NotifyAsync("رسالة من جوالك", (a["text"]?.GetValue<string>() ?? "")[..Math.Min(300, (a["text"]?.GetValue<string>() ?? "").Length)]);
                return new JsonObject { ["done"] = "ظهرت الرسالة على الشاشة" };
            case "screenshot":
                {
                    var width = Math.Clamp(a["width"]?.GetValue<int>() ?? 1280, 320, 1920);
                    var quality = Math.Clamp(a["q"]?.GetValue<int>() ?? 60, 25, 90);
                    var since = a["since"]?.GetValue<string>() ?? "";
                    var locked = _sys.IsLocked();
                    byte[]? bytes;
                    if (locked) bytes = _helper?.Installed == true ? await _helper.SnapAsync(width, quality) : null;
                    else bytes = await _host.ScreenshotJpegAsync(width, quality);
                    if (bytes is null) throw new InvalidOperationException(locked ? "الشاشة مقفلة — ثبّت «مساعد الفتح» من التطبيق لتراها" : "تعذّر التقاط الشاشة");
                    if (_now() - _lastShotNote > TimeSpan.FromMinutes(5)) Note($"👁 بدأت معاينة شاشة «{Environment.MachineName}» من الجوال.");
                    _lastShotNote = _now();
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))[..16];
                    if (since.Length > 0 && since == hash) return new JsonObject { ["same"] = true, ["locked"] = locked, ["h"] = hash };
                    return new JsonObject { ["jpeg"] = Convert.ToBase64String(bytes), ["bytes"] = bytes.Length, ["locked"] = locked, ["h"] = hash };
                }
            case "unlock":
                {
                    if (_helper is null || !_helper.Installed) throw new InvalidOperationException("«مساعد الفتح» غير مثبّت على الجهاز — ثبّته من التطبيق (التحكم من الجوال)");
                    if (_now() < _unlockBlockedUntil) throw new InvalidOperationException($"الفتح موقوف حتى {_unlockBlockedUntil.ToLocalTime():HH:mm} بعد محاولات فاشلة");
                    if (!_sys.IsLocked()) return new JsonObject { ["done"] = "الشاشة مفتوحة أصلاً", ["unlocked"] = true };
                    var secret = a["secret"]?.GetValue<string>() ?? "";
                    if (secret.Length == 0 || secret.Length > 256) throw new InvalidOperationException("اكتب كلمة سر ويندوز (أو احفظها في الخزنة للفتح بضغطة)");
                    var r = await _helper.UnlockAsync(secret, 640);
                    secret = "";
                    var unlocked = false;
                    for (var i = 0; i < 14 && r.Ok; i++) { await Task.Delay(UnlockPollMs); if (!_sys.IsLocked()) { unlocked = true; break; } }
                    if (unlocked) _unlockFails = 0;
                    else if (++_unlockFails >= 3) { _unlockBlockedUntil = _now().AddMinutes(10); _unlockFails = 0; }
                    Note(unlocked ? $"🔓 فُتح قفل «{Environment.MachineName}» من الجوال." : $"❌ فشلت محاولة فتح قفل «{Environment.MachineName}» من الجوال ({r.Message}).");
                    var o = new JsonObject { ["unlocked"] = unlocked, ["done"] = unlocked ? "فُتح القفل" : "لم يُفتح القفل: " + (r.Ok ? "تأكّد من كلمة السر ومن طريقة الدخول الافتراضية (كلمة سر/PIN)" : r.Message) };
                    if (!unlocked && r.Jpeg is { } jp) o["jpeg"] = Convert.ToBase64String(jp);
                    if (!unlocked && !r.Ok) throw new InvalidOperationException(o["done"]!.GetValue<string>());
                    return o;
                }
            case "shell":
                {
                    var script = a["cmd"]?.GetValue<string>() ?? "";
                    if (script.Length == 0 || script.Length > 4000) throw new InvalidOperationException("الأمر فارغ أو طويل");
                    var (code, text) = await _sys.PowerShellAsync(script, Math.Clamp(a["timeout"]?.GetValue<int>() ?? 30, 3, 120));
                    return new JsonObject { ["exit"] = code, ["output"] = text };
                }
        }
        throw new InvalidOperationException("أمر غير معروف");
    }
}

/// <summary>اتصال WebSocket صادر إلى خادم الويب: ينتظر الأوامر ويردّ. يعيد الاتصال تلقائياً.</summary>
public sealed class AgentClient : IDisposable
{
    private readonly Uri _wsBase; private readonly Func<string> _token; private readonly AgentOptions _opt; private readonly AgentService _svc;
    private CancellationTokenSource? _cts; private Task? _loop;
    // ClientWebSocket لا يقبل إرسالين متزامنين: ردّ لقطة كبير + ردّ ضغطة مفتاح + نبض كانت تتصادم فيضيع الردّ (والجوال ينتظر 40ث)
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private async Task SendLockedAsync(ClientWebSocket ws, byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(data, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }
    public bool Connected { get; private set; }
    public string Status { get; private set; } = "متوقف";
    public event Action? Changed;

    public AgentClient(string baseUrl, Func<string> token, AgentOptions opt, AgentService svc)
    {
        var u = new Uri(baseUrl.TrimEnd('/') + "/");
        _wsBase = new Uri($"{(u.Scheme == "https" ? "wss" : "ws")}://{u.Authority}{u.AbsolutePath}agent/ws");
        _token = token; _opt = opt; _svc = svc;
    }

    public void Start() { if (_loop is null) { _cts = new(); _loop = Task.Run(() => LoopAsync(_cts.Token)); } }
    public async Task StopAsync() { _cts?.Cancel(); if (_loop is not null) { try { await _loop; } catch { } } _loop = null; Connected = false; Set("متوقف"); }
    public void Dispose() => _cts?.Cancel();
    private void Set(string s, bool? c = null) { Status = s; if (c is { } v) Connected = v; Changed?.Invoke(); }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                var q = $"?t={_token()}&d={Uri.EscapeDataString(_opt.DeviceId)}&n={Uri.EscapeDataString(string.IsNullOrWhiteSpace(_opt.DeviceName) ? Environment.MachineName : _opt.DeviceName)}";
                await ws.ConnectAsync(new Uri(_wsBase + q), ct);
                Set("متصل — بانتظار أوامر الجوال", true); backoff = 2;
                var buf = new byte[4 * 1024 * 1024];
                var hb = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        try { await SendLockedAsync(ws, Encoding.UTF8.GetBytes("{\"hb\":1}"), ct); } catch { return; }
                        try { await Task.Delay(20000, ct); } catch { return; }
                    }
                }, ct);
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var total = 0; ValueWebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(buf.AsMemory(total), ct); total += r.Count; } while (!r.EndOfMessage && total < buf.Length);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    try
                    {
                        using var d = JsonDocument.Parse(buf.AsMemory(0, total));
                        if (!d.RootElement.TryGetProperty("id", out var idEl) || !d.RootElement.TryGetProperty("ct", out var ctEl)) continue;
                        var id = idEl.GetString() ?? ""; var sealedCmd = Convert.FromBase64String(ctEl.GetString() ?? "");
                        _ = Task.Run(async () =>
                        {
                            var reply = await _svc.HandleAsync(id, sealedCmd);
                            if (reply is null || ws.State != WebSocketState.Open) return;
                            var json = JsonSerializer.SerializeToUtf8Bytes(new { id, ct = Convert.ToBase64String(reply) });
                            try { await SendLockedAsync(ws, json, ct); } catch (Exception ex) { Set($"تعذّر إرسال ردّ: {ex.Message}"); try { ws.Abort(); } catch { } }
                        }, ct);
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Set($"انقطع: {ex.Message} — إعادة المحاولة بعد {backoff}ث", false);
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); } catch { break; }
                backoff = Math.Min(backoff * 2, 60);
            }
            Set("غير متصل", false);
        }
    }
}
