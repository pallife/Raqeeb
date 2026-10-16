using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Raqeeb;

/// <summary>ما يحتاجه الوكيل من سطح المكتب — قابل للاستبدال في الاختبارات فلا تتحرّك فأرتك ولا يُفتح شيء أثناءها.</summary>
public interface IAgentDesktop
{
    /// <summary>حقن مدخل (فأرة/كيبورد) على سطح المستخدم الحالي.</summary>
    void Input(JsonObject args);
    /// <summary>يمنع (أو يعيد) إطفاء الشاشة والسكون.</summary>
    void SetAwake(bool on);
    /// <summary>يفتح ملفاً/رابطاً بالبرنامج الافتراضي في جلسة المستخدم.</summary>
    void Open(string target);
}

public sealed class WindowsAgentDesktop : IAgentDesktop
{
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 1, ES_DISPLAY_REQUIRED = 2;
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private readonly object _l = new();
    private Thread? _t; private ManualResetEventSlim? _stop;

    public void Input(JsonObject args) => InputInjector.Execute(args, virtualDesk: true);

    /// <summary>الحالة مرتبطة بالخيط الذي استدعاها، لذا خيط مخصّص يعيش ما دام المنع قائماً (وينتهي بانتهاء التطبيق).</summary>
    public void SetAwake(bool on)
    {
        lock (_l)
        {
            if (on)
            {
                if (_t is not null) return;
                var stop = _stop = new ManualResetEventSlim(false);
                _t = new Thread(() =>
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
                    stop.Wait();
                    SetThreadExecutionState(ES_CONTINUOUS);
                }) { IsBackground = true, Name = "cp-awake" };
                _t.Start();
            }
            else { _stop?.Set(); _t = null; _stop = null; }
        }
    }

    public void Open(string target)
    {
        var p = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        p?.Dispose();
    }
}

/// <summary>سياسة الملفات: مجلدات مسموحة فقط، لا روابط تحويل، لا ملفات حساسة، لا فتح ملفات تنفيذية.</summary>
public static class AgentFiles
{
    public static readonly string[] DefaultRootNames = { "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music" };
    public const int ReadChunk = 1_500_000;
    public const long TelegramMax = 49L * 1024 * 1024;
    public const long PutMax = 2L * 1024 * 1024 * 1024;
    public const long FetchMax = 1L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> SensitiveDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".ssh", ".aws", ".gnupg", ".kube", ".azure", "AppData" };
    private static readonly HashSet<string> SensitiveFiles = new(StringComparer.OrdinalIgnoreCase)
        { "id_rsa", "id_ed25519", "id_ecdsa", ".env", ".npmrc", ".netrc", "Login Data", "Cookies", "Local State", "Web Data", "wallet.dat", "known_hosts" };
    private static readonly HashSet<string> SensitiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".pfx", ".p12", ".pem", ".key", ".ppk", ".cpvault", ".kdbx", ".gpg", ".asc" };
    private static readonly HashSet<string> ExecExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".mst", ".scr", ".pif",
        ".lnk", ".url", ".reg", ".hta", ".cpl", ".jar", ".dll", ".sys", ".appx", ".msix", ".application", ".gadget", ".inf", ".scf", ".chm", ".iso", ".vhd", ".vhdx", ".ocx", ".drv", ".msc",
    };

    public static List<string> Roots(string custom)
    {
        var res = new List<string>();
        foreach (var part in (custom ?? "").Split(new[] { ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (!Path.IsPathFullyQualified(part) || part.StartsWith(@"\\")) continue;
                var f = Path.GetFullPath(part).TrimEnd('\\');
                if (f.Length <= 3) continue; // لا نقبل جذر قرص كاملاً
                if (Directory.Exists(f)) res.Add(f);
            }
            catch { }
        }
        if (res.Count > 0) return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var n in DefaultRootNames)
        {
            var d = Path.Combine(home, n);
            if (Directory.Exists(d)) res.Add(d);
        }
        return res;
    }

    public static string Inbox()
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Raqeeb-Inbox");
        Directory.CreateDirectory(d);
        return d;
    }

    public static bool IsSensitive(string full)
    {
        var name = Path.GetFileName(full);
        if (SensitiveFiles.Contains(name) || SensitiveExt.Contains(Path.GetExtension(name))) return true;
        foreach (var seg in full.Split('\\', '/'))
            if (SensitiveDirs.Contains(seg)) return true;
        return false;
    }

    public static bool IsExecutable(string path) => ExecExt.Contains(Path.GetExtension(path));

    /// <summary>يعيد المسار الكامل إن كان داخل جذر مسموح وآمناً، وإلا يرمي برسالة مفهومة.</summary>
    public static string Resolve(string raw, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 1000 || raw.Contains('\0')) throw new InvalidOperationException("مسار غير صالح");
        if (raw.StartsWith(@"\\") || raw.IndexOf(':', Math.Min(2, raw.Length)) >= 0) throw new InvalidOperationException("مسار غير مسموح (شبكة/تدفقات بديلة)");
        var full = Path.GetFullPath(raw).TrimEnd('\\');
        var root = roots.FirstOrDefault(r => full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("المسار خارج المجلدات المسموحة");
        var cur = full;
        while (cur.Length > root.Length)
        {
            try
            {
                if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("روابط التحويل غير مسموحة");
            }
            catch (IOException) { }
            cur = Path.GetDirectoryName(cur) ?? root;
        }
        if (IsSensitive(full.Substring(root.Length))) throw new InvalidOperationException("ملف حساس محجوب");
        return full;
    }

    public static string SafeName(string name)
    {
        name = Path.GetFileName((name ?? "").Replace('/', '\\'));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimStart('.');
        if (name.Length == 0) name = "file";
        if (name.Length > 120) name = name[..100] + Path.GetExtension(name);
        return name;
    }

    public static string UniquePath(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return p;
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++) { p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
        return Path.Combine(dir, stem + "-" + Guid.NewGuid().ToString("N")[..6] + ext);
    }

    /// <summary>منع الوصول للشبكة الداخلية عند التنزيل (SSRF): لا loopback/خاص/link-local.</summary>
    public static bool IsPublicAddress(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (a.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = a.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] == 0) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        if (b[0] >= 224) return false;
        return true;
    }

    public static bool HostIsPublic(string host)
    {
        try
        {
            if (IPAddress.TryParse(host, out var ip)) return IsPublicAddress(ip);
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
            var all = Dns.GetHostAddresses(host);
            return all.Length > 0 && all.All(IsPublicAddress);
        }
        catch { return false; }
    }
}

public sealed record FetchJob(string Id, string Url, string Name, string State, long Bytes, long Total, string Error, string Path);

public sealed partial class AgentService
{
    /// <summary>سطح المكتب (إدخال/منع سكون/فتح). null ← تُرفض هذه الأوامر.</summary>
    public IAgentDesktop? Desktop { get; set; }
    /// <summary>يُستدعى بعد تغيير الصلاحيات من الجوال (ليحفظها التطبيق ويطبّق الصندوق).</summary>
    public Action? CapsChanged { get; set; }
    /// <summary>الإرسال السريع: رقم تسلسل الحافظة (يتغيّر عند كل نسخ) ← لمعرفة أن Ctrl+C نسخ شيئاً فعلاً.</summary>
    public Func<uint>? ClipboardSeq { get; set; }
    /// <summary>الإرسال السريع: يرسل محتوى الحافظة إلى تلغرام ← نص النتيجة.</summary>
    public Func<CancellationToken, Task<string>>? ClipboardToTelegram { get; set; }
    /// <summary>غطاء الخصوصية على شاشة الجهاز (on, color) ← true إن كان مستثنى من الالتقاط.</summary>
    public Func<bool, string, bool>? PrivacyScreen { get; set; }
    private bool _privacyOn;
    /// <summary>إرسال ملف إلى تلغرام عبر المرحّل (path, caption) ← نص النتيجة.</summary>
    public Func<string, string, CancellationToken, Task<string>>? TelegramFile { get; set; }
    /// <summary>للاختبارات: بديل لفحص النطاق العام وللمعالج.</summary>
    public Func<string, bool> FetchHostCheck { get; set; } = AgentFiles.HostIsPublic;
    public Func<HttpMessageHandler>? FetchHandler { get; set; }
    /// <summary>مجلد استقبال التنزيلات (للاختبارات).</summary>
    public Func<string>? InboxDir { get; set; }
    public int AwakeTickMs { get; set; } = 30_000;
    public int TelegramRetryDelayMs { get; set; } = 20_000;

    private static readonly string[] FileOps = { "fs_roots", "fs_list", "fs_find", "fs_read", "fs_put", "fs_open", "fs_telegram", "fetch", "fetch_status" };
    private readonly object _awakeLock = new();
    private bool _awakeOn; private DateTimeOffset _awakeSince, _awakeUntil, _awakeRemind; private CancellationTokenSource? _awakeCts;
    private DateTimeOffset _lastInputNote, _lastReadNote;
    private DateTimeOffset _lastInputAudit;
    private readonly Dictionary<string, FetchJob> _jobs = new();
    private readonly Dictionary<string, FileStream> _puts = new();

    private IReadOnlyList<string> Roots() => AgentFiles.Roots(_opt.FileRoots);
    private string Inbox() => InboxDir?.Invoke() ?? AgentFiles.Inbox();

    // ---------------- منع السكون
    private JsonObject AwakeInfo()
    {
        lock (_awakeLock)
            return new JsonObject
            {
                ["on"] = _awakeOn, ["since"] = _awakeOn ? _awakeSince.ToString("o") : null, ["until"] = _awakeOn ? _awakeUntil.ToString("o") : null,
                ["remindMin"] = Math.Clamp(_opt.AwakeReminderMinutes, 5, 240),
            };
    }

    private JsonNode SetAwake(bool on, int hours)
    {
        if (Desktop is null) throw new InvalidOperationException("غير مدعوم هنا");
        lock (_awakeLock)
        {
            if (on)
            {
                var h = Math.Clamp(hours <= 0 ? Math.Max(1, _opt.AwakeMaxHours) : hours, 1, 24);
                Desktop.SetAwake(true);
                _awakeOn = true; _awakeSince = _now(); _awakeUntil = _now().AddHours(h); _awakeRemind = _now();
                _awakeCts?.Cancel(); _awakeCts = new CancellationTokenSource();
                var ct = _awakeCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(AwakeTickMs, ct); } catch { return; }
                        AwakeTick();
                    }
                }, ct);
                return new JsonObject { ["done"] = $"الشاشة والجهاز لن ينامَا حتى {h} ساعة أو حتى تُلغي — ستصلك تذكيرات", ["awake"] = AwakeInfo() };
            }
            Desktop.SetAwake(false);
            var was = _awakeOn; _awakeOn = false; _awakeCts?.Cancel(); _awakeCts = null;
            return new JsonObject { ["done"] = was ? "عاد سلوك الطاقة الافتراضي (يمكن للشاشة أن تنطفئ والجهاز أن ينام)" : "منع السكون لم يكن مفعّلاً", ["awake"] = AwakeInfo() };
        }
    }

    /// <summary>خطوة دورية: تذكير بالبقاء مفعّلاً، وإيقاف تلقائي عند انتهاء المدة. عامّة للاختبارات.</summary>
    public void AwakeTick()
    {
        string? note = null;
        lock (_awakeLock)
        {
            if (!_awakeOn) return;
            var now = _now();
            if (now >= _awakeUntil)
            {
                Desktop?.SetAwake(false); _awakeOn = false; _awakeCts?.Cancel(); _awakeCts = null;
                note = $"✅ «{Environment.MachineName}»: انتهت مدة منع السكون فأُعيد السلوك الافتراضي تلقائياً.";
            }
            else if (now - _awakeRemind >= TimeSpan.FromMinutes(Math.Clamp(_opt.AwakeReminderMinutes, 5, 240)))
            {
                _awakeRemind = now;
                var mins = (int)(now - _awakeSince).TotalMinutes;
                string power = "";
                try { var s = _sampler.Sample(); if (s.OnAcPower == false && s.BatteryPercent is { } b) power = $" ⚠ على البطارية ({b}%) وتستهلك أسرع."; } catch { }
                note = $"☕ «{Environment.MachineName}»: منع السكون مفعّل منذ {mins / 60}س {mins % 60}د (الشاشة لن تنطفئ).{power} يُلغى تلقائياً بعد {(int)(_awakeUntil - now).TotalMinutes} دقيقة. ألغِه من «جهازي» ← «منع السكون».";
            }
        }
        if (note is not null) Note(note);
    }

    /// <summary>إيقاف كل شيء عند إغلاق الوكيل: لا يبقى منع سكون معلّقاً.</summary>
    public void Shutdown()
    {
        if (_privacyOn) { try { PrivacyScreen?.Invoke(false, ""); } catch { } _privacyOn = false; }
        lock (_awakeLock) { if (_awakeOn) { try { Desktop?.SetAwake(false); } catch { } _awakeOn = false; } _awakeCts?.Cancel(); }
        lock (_puts) { foreach (var f in _puts.Values) f.Dispose(); _puts.Clear(); }
    }

    // ---------------- إدخال
    private async Task<JsonNode> InputAsync(JsonObject a)
    {
        var kind = a["kind"]?.GetValue<string>() ?? "";
        var now = _now();
        if (now - _lastInputNote > TimeSpan.FromMinutes(5)) Note($"🖱 بدأ تحكم بفأرة/كيبورد «{Environment.MachineName}» من الجوال.");
        _lastInputNote = now;
        if (kind != "move" && now - _lastInputAudit > TimeSpan.FromSeconds(3))
        {
            _lastInputAudit = now;
            Audit?.Invoke("إدخال عن بُعد: " + kind + (kind == "text" ? $" ({(a["text"]?.GetValue<string>() ?? "").Length} حرفاً)" : kind == "key" ? " " + (a["name"]?.GetValue<string>() ?? "") : ""));
        }
        // على شاشة القفل: عبر المساعد (SYSTEM على Winlogon). وإلا مباشرة في جلسة المستخدم.
        if (await Task.Run(_sys.IsLocked))
        {
            if (_helper is null || !_helper.Installed) throw new InvalidOperationException("الشاشة مقفلة — الإدخال عليها يحتاج «مساعد الفتح» المثبّت");
            var r = await _helper.InputAsync(a);
            if (!r.Ok) throw new InvalidOperationException(r.Message);
            return new JsonObject { ["done"] = "ok", ["locked"] = true };
        }
        if (Desktop is null) throw new InvalidOperationException("الإدخال غير مدعوم هنا");
        await Task.Run(() => Desktop.Input(a));
        return new JsonObject { ["done"] = "ok", ["locked"] = false };
    }

    // ---------------- ملفات
    private static JsonObject Entry(FileSystemInfo i) => new()
    {
        ["name"] = i.Name, ["dir"] = i is DirectoryInfo,
        ["size"] = i is FileInfo f ? f.Length : 0, ["mtime"] = new DateTimeOffset(i.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
        ["exec"] = i is FileInfo && AgentFiles.IsExecutable(i.Name),
    };

    private async Task<JsonNode?> ExtraActionAsync(string op, JsonObject a)
    {
        switch (op)
        {
            case "input": return await InputAsync(a);
            case "set_caps":
            {
                // يُسمح بتفعيل/تعطيل التحكم بالفأرة والملفات وصندوق الإرسال فقط (بالـPIN). القفل/الشل/اللقطة تبقى من التطبيق.
                if (a["input"] is JsonValue vi && vi.TryGetValue<bool>(out var bi)) _opt.AllowInput = bi;
                if (a["files"] is JsonValue vf && vf.TryGetValue<bool>(out var bf)) _opt.AllowFiles = bf;
                if (a["outbox"] is JsonValue vo && vo.TryGetValue<bool>(out var bo)) _opt.OutboxEnabled = bo;
                try { CapsChanged?.Invoke(); } catch { }
                Note($"⚙ غُيّرت صلاحيات التحكم على «{Environment.MachineName}» من الجوال: فأرة={_opt.AllowInput} ملفات={_opt.AllowFiles} صندوق={_opt.OutboxEnabled}");
                return new JsonObject { ["input"] = _opt.AllowInput, ["files"] = _opt.AllowFiles, ["outbox"] = _opt.OutboxEnabled };
            }
            case "privacy":
            {
                if (PrivacyScreen is null) throw new InvalidOperationException("غطاء الشاشة غير متاح في هذا الإصدار");
                var on = a["on"] is JsonValue vp && vp.TryGetValue<bool>(out var bp) && bp;
                var col = a["color"]?.GetValue<string>() ?? "#000000";
                if (!System.Text.RegularExpressions.Regex.IsMatch(col, "^#[0-9a-fA-F]{6}$")) col = "#000000";
                var excluded = PrivacyScreen(on, col);
                _privacyOn = on;
                Note(on ? $"🕶 غُطّيت شاشة «{Environment.MachineName}» من الجوال (للإزالة محلياً: Ctrl+Alt+Shift+P)." : $"🕶 أُزيل غطاء شاشة «{Environment.MachineName}».");
                return new JsonObject { ["on"] = on, ["excluded"] = excluded };
            }
            case "quick_send":
            {
                if (Desktop is null || ClipboardToTelegram is null) throw new InvalidOperationException("الإرسال السريع غير مهيّأ (اربط البوت وفعّل نسخة الويب)");
                var seq0 = ClipboardSeq?.Invoke() ?? 0;
                Desktop.Input(new JsonObject { ["kind"] = "key", ["name"] = "c", ["mods"] = new JsonArray("ctrl") });
                var changed = false;
                for (var i = 0; i < 12 && !changed; i++) { await Task.Delay(120); changed = (ClipboardSeq?.Invoke() ?? 0) != seq0; }
                if (!changed) return new JsonObject { ["done"] = "لا شيء محدّد لنسخه — لم يُرسل شيء", ["sent"] = false };
                // الرفع قد يطول (ملفات كبيرة) فلا نُبقي أمر الجوال معلّقاً: يعود فوراً والنتيجة/الفشل تصل تلغرام.
                var send = ClipboardToTelegram;
                var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var r = await send(CancellationToken.None);
                        Note($"✈ إرسال سريع من «{Environment.MachineName}»: {r}");
                        started.TrySetResult(r);
                    }
                    catch (Exception ex)
                    {
                        Note($"⚠ فشل الإرسال السريع من «{Environment.MachineName}»: {ex.Message}");
                        started.TrySetResult("⚠ " + ex.Message);
                    }
                });
                // انتظر قليلاً: الأخطاء السريعة (بوت غير مربوط، لا شيء منسوخ) تظهر على الجوال مباشرة
                var done = await Task.WhenAny(started.Task, Task.Delay(2500));
                var msg = done == started.Task ? await started.Task : "جارٍ الإرسال إلى تلغرام… (ستصلك رسالة عند الانتهاء)";
                var ok = !(msg?.StartsWith("⚠") ?? false);
                return new JsonObject { ["done"] = msg, ["sent"] = ok };
            }
            case "awake_on": return SetAwake(true, a["hours"]?.GetValue<int>() ?? 0);
            case "fs_roots":
                return new JsonObject
                {
                    ["roots"] = new JsonArray(Roots().Select(r => (JsonNode)new JsonObject { ["path"] = r, ["name"] = Path.GetFileName(r) }).ToArray()),
                    ["inbox"] = Inbox(),
                };
            case "fs_list":
                {
                    var roots = Roots();
                    var p = a["path"]?.GetValue<string>() ?? "";
                    if (p.Length == 0) return new JsonObject { ["path"] = "", ["items"] = new JsonArray(roots.Select(r => (JsonNode)new JsonObject { ["name"] = Path.GetFileName(r), ["dir"] = true, ["size"] = 0, ["mtime"] = 0, ["exec"] = false, ["full"] = r }).ToArray()) };
                    var full = AgentFiles.Resolve(p, roots);
                    var d = new DirectoryInfo(full);
                    if (!d.Exists) throw new InvalidOperationException("المجلد غير موجود");
                    var items = new List<JsonNode>();
                    foreach (var i in d.EnumerateFileSystemInfos().OrderByDescending(x => x is DirectoryInfo).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if ((i.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (AgentFiles.IsSensitive(i.Name)) continue;
                        items.Add(Entry(i));
                        if (items.Count >= 400) break;
                    }
                    var parent = roots.Any(r => r.Equals(full, StringComparison.OrdinalIgnoreCase)) ? "" : Path.GetDirectoryName(full) ?? "";
                    return new JsonObject { ["path"] = full, ["parent"] = parent, ["items"] = new JsonArray(items.ToArray()) };
                }
            case "fs_find":
                {
                    var q = (a["q"]?.GetValue<string>() ?? "").Trim();
                    if (q.Length < 2 || q.Length > 80) throw new InvalidOperationException("اكتب حرفين على الأقل للبحث");
                    var found = new List<JsonNode>(); var sw = Stopwatch.StartNew();
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden };
                    await Task.Run(() =>
                    {
                        foreach (var r in Roots())
                        {
                            try
                            {
                                foreach (var i in new DirectoryInfo(r).EnumerateFileSystemInfos("*" + q.Replace("*", "").Replace("?", "") + "*", opts))
                                {
                                    if (AgentFiles.IsSensitive(i.FullName.Substring(r.Length))) continue;
                                    var e = Entry(i); e["full"] = i.FullName; found.Add(e);
                                    if (found.Count >= 60 || sw.ElapsedMilliseconds > 6000) return;
                                }
                            }
                            catch { }
                        }
                    });
                    return new JsonObject { ["items"] = new JsonArray(found.ToArray()), ["truncated"] = found.Count >= 60 || sw.ElapsedMilliseconds > 6000 };
                }
            case "fs_read":
                {
                    var full = AgentFiles.Resolve(a["path"]?.GetValue<string>() ?? "", Roots());
                    var fi = new FileInfo(full);
                    if (!fi.Exists) throw new InvalidOperationException("الملف غير موجود");
                    var off = Math.Max(0, a["offset"]?.GetValue<long>() ?? 0);
                    var len = (int)Math.Clamp(a["len"]?.GetValue<long>() ?? AgentFiles.ReadChunk, 1, AgentFiles.ReadChunk);
                    byte[] buf;
                    using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Seek(off, SeekOrigin.Begin);
                        buf = new byte[len]; var n = 0;
                        while (n < len) { var r = await fs.ReadAsync(buf.AsMemory(n, len - n)); if (r == 0) break; n += r; }
                        Array.Resize(ref buf, n);
                    }
                    if (off == 0 && _now() - _lastReadNote > TimeSpan.FromMinutes(2)) { _lastReadNote = _now(); Note($"📥 تنزيل ملف من «{Environment.MachineName}» إلى الجوال: {fi.Name}"); }
                    return new JsonObject { ["name"] = fi.Name, ["size"] = fi.Length, ["offset"] = off, ["data"] = Convert.ToBase64String(buf), ["eof"] = off + buf.Length >= fi.Length };
                }
            case "fs_put":
                {
                    var name = AgentFiles.SafeName(a["name"]?.GetValue<string>() ?? "");
                    if (AgentFiles.IsExecutable(name)) throw new InvalidOperationException("أنواع الملفات التنفيذية غير مسموحة");
                    var key = a["upload"]?.GetValue<string>() ?? "";
                    if (key.Length is < 8 or > 64) throw new InvalidOperationException("معرّف رفع غير صالح");
                    var off = a["offset"]?.GetValue<long>() ?? 0;
                    var bytes = Convert.FromBase64String(a["data"]?.GetValue<string>() ?? "");
                    if (bytes.Length > AgentFiles.ReadChunk + 1024) throw new InvalidOperationException("الجزء كبير");
                    FileStream fs;
                    lock (_puts)
                    {
                        if (!_puts.TryGetValue(key, out fs!))
                        {
                            if (off != 0) throw new InvalidOperationException("بداية رفع غير صحيحة");
                            if (_puts.Count >= 3) throw new InvalidOperationException("رفوعات كثيرة متزامنة");
                            fs = new FileStream(AgentFiles.UniquePath(Inbox(), name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            _puts[key] = fs;
                            Note($"📤 رفع ملف من الجوال إلى «{Environment.MachineName}»: {name}");
                        }
                    }
                    if (fs.Position != off) throw new InvalidOperationException("ترتيب أجزاء الرفع غير صحيح");
                    if (fs.Position + bytes.Length > AgentFiles.PutMax) throw new InvalidOperationException("الملف أكبر من الحد");
                    await fs.WriteAsync(bytes);
                    var done = a["done"]?.GetValue<bool>() == true;
                    string path = fs.Name; long size = fs.Length;
                    if (done) { lock (_puts) _puts.Remove(key); await fs.DisposeAsync(); }
                    return new JsonObject { ["done"] = done ? "اكتمل الرفع إلى " + path : "تم الجزء", ["size"] = size, ["complete"] = done };
                }
            case "fs_open":
                {
                    if (Desktop is null) throw new InvalidOperationException("غير مدعوم هنا");
                    var target = a["path"]?.GetValue<string>() ?? "";
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Uri.TryCreate(target, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || target.Length > 2000) throw new InvalidOperationException("رابط غير صالح");
                        Desktop.Open(u.AbsoluteUri);
                        Note($"🌐 فُتح رابط على «{Environment.MachineName}» من الجوال: {u.Host}");
                        return new JsonObject { ["done"] = "فُتح الرابط على الجهاز" };
                    }
                    var full = AgentFiles.Resolve(target, Roots());
                    if (!File.Exists(full) && !Directory.Exists(full)) throw new InvalidOperationException("غير موجود");
                    if (File.Exists(full) && AgentFiles.IsExecutable(full)) throw new InvalidOperationException("لا يُفتح ملف تنفيذي/سكربت عن بُعد");
                    Desktop.Open(full);
                    Note($"📂 فُتح على «{Environment.MachineName}» من الجوال: {Path.GetFileName(full)}");
                    return new JsonObject { ["done"] = "فُتح على الجهاز: " + Path.GetFileName(full) };
                }
            case "fs_telegram":
                {
                    if (TelegramFile is null) throw new InvalidOperationException("إرسال تلغرام غير مهيّأ (اربط البوت من «الأمان»)");
                    var full = AgentFiles.Resolve(a["path"]?.GetValue<string>() ?? "", Roots());
                    if (!File.Exists(full) && !Directory.Exists(full)) throw new InvalidOperationException("غير موجود");
                    var cap = (a["caption"]?.GetValue<string>() ?? "").Trim();
                    // ملف كبير ← يُجزَّأ؛ مجلد ← يُضغط ثم يُرسل (الملفات الحساسة تُستثنى من الأرشيف)
                    var r = await TelegramOutbox.SendAsync(full, cap.Length > 200 ? cap[..200] : cap, TelegramFile, null, CancellationToken.None, null, TelegramRetryDelayMs);
                    Note($"✈ أُرسل «{Path.GetFileName(full)}» إلى تلغرام من «{Environment.MachineName}».");
                    return new JsonObject { ["done"] = r };
                }
            case "fetch":
                return StartFetch(a["url"]?.GetValue<string>() ?? "", a["name"]?.GetValue<string>() ?? "");
            case "fetch_status":
                lock (_jobs) return new JsonObject { ["jobs"] = new JsonArray(_jobs.Values.OrderByDescending(j => j.Id).Take(10).Select(j => (JsonNode)new JsonObject
                    { ["id"] = j.Id, ["url"] = j.Url, ["name"] = j.Name, ["state"] = j.State, ["bytes"] = j.Bytes, ["total"] = j.Total, ["error"] = j.Error, ["path"] = j.Path }).ToArray()) };
        }
        return null;
    }

    // ---------------- تنزيل رابط إلى اللابتوب (بالخلفية)
    private JsonNode StartFetch(string url, string name)
    {
        if (url.Length is 0 or > 2000 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("رابط http/https فقط");
        if (!FetchHostCheck(uri.Host)) throw new InvalidOperationException("العنوان داخلي/محلي — محجوب");
        lock (_jobs) if (_jobs.Values.Count(j => j.State == "running") >= 2) throw new InvalidOperationException("تنزيلان قيد التنفيذ بالفعل");
        var id = DateTime.UtcNow.ToString("HHmmss") + Guid.NewGuid().ToString("N")[..4];
        var nm = AgentFiles.SafeName(name.Length > 0 ? name : Path.GetFileName(uri.AbsolutePath));
        if (nm == "file" && name.Length == 0) nm = "download-" + id;
        if (AgentFiles.IsExecutable(nm)) nm += ".download"; // لا يُحفظ بامتداد قابل للتشغيل
        void Set(FetchJob j) { lock (_jobs) { _jobs[id] = j; if (_jobs.Count > 30) _jobs.Remove(_jobs.Keys.Order().First()); } }
        Set(new FetchJob(id, uri.AbsoluteUri, nm, "running", 0, 0, "", ""));
        Note($"⬇ بدأ تنزيل على «{Environment.MachineName}» من الجوال: {uri.Host}/{nm}");
        _ = Task.Run(async () =>
        {
            string? tmp = null;
            try
            {
                using var http = new HttpClient(FetchHandler?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromMinutes(30) };
                var cur = uri;
                HttpResponseMessage? res = null;
                for (var hop = 0; hop < 5; hop++)
                {
                    res?.Dispose();
                    if (!FetchHostCheck(cur.Host)) throw new InvalidOperationException("إعادة توجيه إلى عنوان داخلي — محجوبة");
                    res = await http.GetAsync(cur, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc) { cur = loc.IsAbsoluteUri ? loc : new Uri(cur, loc); if (cur.Scheme is not ("http" or "https")) throw new InvalidOperationException("بروتوكول غير مسموح"); continue; }
                    break;
                }
                using (res!)
                {
                    res!.EnsureSuccessStatusCode();
                    var total = res.Content.Headers.ContentLength ?? 0;
                    if (total > AgentFiles.FetchMax) throw new InvalidOperationException("الملف أكبر من 1GB");
                    var dir = Inbox();
                    var finalPath = AgentFiles.UniquePath(dir, nm);
                    tmp = finalPath + ".part";
                    long got = 0;
                    await using (var src = await res.Content.ReadAsStreamAsync())
                    await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920]; int n; var tick = Stopwatch.StartNew();
                        while ((n = await src.ReadAsync(buf)) > 0)
                        {
                            got += n;
                            if (got > AgentFiles.FetchMax) throw new InvalidOperationException("الملف أكبر من 1GB");
                            await dst.WriteAsync(buf.AsMemory(0, n));
                            if (tick.ElapsedMilliseconds > 700) { tick.Restart(); Set(new FetchJob(id, uri.AbsoluteUri, nm, "running", got, total, "", "")); }
                        }
                    }
                    File.Move(tmp, finalPath); tmp = null;
                    Set(new FetchJob(id, uri.AbsoluteUri, nm, "done", got, got, "", finalPath));
                    Note($"✅ اكتمل التنزيل على «{Environment.MachineName}»: {Path.GetFileName(finalPath)} ({got / 1048576} MB)");
                }
            }
            catch (Exception ex)
            {
                Set(new FetchJob(id, uri.AbsoluteUri, nm, "error", 0, 0, ex.Message, ""));
                Note($"❌ فشل التنزيل على «{Environment.MachineName}»: {ex.Message}");
                if (tmp is not null) try { File.Delete(tmp); } catch { }
            }
        });
        return new JsonObject { ["done"] = "بدأ التنزيل إلى مجلد Raqeeb-Inbox داخل التنزيلات", ["job"] = id };
    }
}
