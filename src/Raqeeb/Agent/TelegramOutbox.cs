using System.IO.Compression;
using System.Text;

namespace Raqeeb;

/// <summary>جزء جاهز للإرسال إلى تلغرام: ملف أصلي أو مؤقت (مضغوط/مجزّأ) يُحذف بعد الإرسال.</summary>
public sealed record OutboxPart(string Path, string Name, bool Temp);

/// <summary>
/// «وضع الإرسال الكامل» إلى بوت تلغرام: ملف عادي، ملف كبير يُجزَّأ (حد البوتات 50MB)، أو مجلد يُضغط ثم يُرسل.
/// منطق بحت (بلا واجهة ولا شبكة) — الإرسال الفعلي يمرّ عبر مفوّض (مرحّل الويب الذي يملك توكن البوت).
/// </summary>
public static class TelegramOutbox
{
    public const long SingleMax = 49L * 1024 * 1024;
    public const long PartSize = 45L * 1024 * 1024;
    public const long MaxTotal = 2L * 1024 * 1024 * 1024;

    /// <summary>يجهّز الأجزاء. يرمي برسالة مفهومة إن كان المصدر غير موجود أو أكبر من الحد.</summary>
    public static List<OutboxPart> Prepare(string path, string tempDir, long singleMax = SingleMax, long partSize = PartSize, long maxTotal = MaxTotal)
    {
        Directory.CreateDirectory(tempDir);
        if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(path);
            var zip = System.IO.Path.Combine(tempDir, AgentFiles.SafeName(dir.Name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip");
            long total = 0; var count = 0;
            using (var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                foreach (var f in dir.EnumerateFiles("*", opts))
                {
                    var rel = System.IO.Path.GetRelativePath(dir.FullName, f.FullName);
                    if (AgentFiles.IsSensitive(rel)) continue;
                    total += f.Length;
                    if (total > maxTotal) { try { File.Delete(zip); } catch { } throw new InvalidOperationException("المجلد أكبر من الحد المسموح للإرسال"); }
                    try
                    {
                        var e = za.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Fastest);
                        e.LastWriteTime = f.LastWriteTime;
                        using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var dst = e.Open();
                        src.CopyTo(dst);
                        count++;
                    }
                    catch (IOException) { /* ملف مقفل: يُتخطّى */ }
                }
            }
            var zlen = new FileInfo(zip).Length;
            if (count == 0 || zlen == 0) { File.Delete(zip); throw new InvalidOperationException("المجلد فارغ"); }
            if (zlen <= singleMax) return new List<OutboxPart> { new(zip, System.IO.Path.GetFileName(zip), true) };
            var parts = Split(zip, tempDir, partSize);
            try { File.Delete(zip); } catch { }
            return parts;
        }
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new InvalidOperationException("غير موجود: " + path);
        if (fi.Length > maxTotal) throw new InvalidOperationException("الملف أكبر من الحد المسموح للإرسال");
        if (fi.Length == 0) throw new InvalidOperationException("الملف فارغ");
        if (fi.Length <= singleMax) return new List<OutboxPart> { new(fi.FullName, fi.Name, false) };
        return Split(fi.FullName, tempDir, partSize);
    }

    /// <summary>تجزئة بسيطة بالتسلسل: name.part001, name.part002… (ضمّها بـ copy /b أو cat).</summary>
    private static List<OutboxPart> Split(string file, string tempDir, long partSize)
    {
        var res = new List<OutboxPart>();
        var name = System.IO.Path.GetFileName(file);
        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[1 << 20]; var idx = 1;
        while (src.Position < src.Length)
        {
            var p = System.IO.Path.Combine(tempDir, $"{name}.part{idx:000}");
            long written = 0;
            using (var o = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (written < partSize)
                {
                    var n = src.Read(buf, 0, (int)Math.Min(buf.Length, partSize - written));
                    if (n == 0) break;
                    o.Write(buf, 0, n); written += n;
                }
            }
            res.Add(new OutboxPart(p, System.IO.Path.GetFileName(p), true));
            idx++;
        }
        return res;
    }

    /// <summary>يرسل مساراً (ملف/مجلد) كاملاً. يعيد ملخصاً نصياً. يعيد المحاولة عند تجاوز معدّل المرحّل.</summary>
    public static async Task<string> SendAsync(string path, string caption, Func<string, string, CancellationToken, Task<string>> send,
        string? tempDir = null, CancellationToken ct = default, Action<string>? progress = null, int retryDelayMs = 20_000,
        long singleMax = SingleMax, long partSize = PartSize)
    {
        tempDir ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cp-tg-" + Guid.NewGuid().ToString("N")[..8]);
        var parts = await Task.Run(() => Prepare(path, tempDir, singleMax, partSize), ct);
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var cap = parts.Count > 1 ? $"{caption} [{i + 1}/{parts.Count}]".Trim() : caption;
                progress?.Invoke($"✈ {parts[i].Name} ({i + 1}/{parts.Count})");
                for (var attempt = 0; ; attempt++)
                {
                    try { await send(parts[i].Path, cap, ct); break; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("انتظر") && attempt < 5)
                    { await Task.Delay(retryDelayMs, ct); }
                }
            }
            return parts.Count == 1 ? $"أُرسل: {parts[0].Name}" : $"أُرسل {parts.Count} أجزاء: {System.IO.Path.GetFileNameWithoutExtension(parts[0].Name)}";
        }
        finally
        {
            foreach (var p in parts) if (p.Temp) { try { File.Delete(p.Path); } catch { } }
            if (Directory.Exists(tempDir)) { try { if (!Directory.EnumerateFileSystemEntries(tempDir).Any()) Directory.Delete(tempDir); } catch { } }
        }
    }
}

/// <summary>
/// مجلد «صندوق الإرسال»: أي ملف/مجلد يُلقى فيه يُرسَل إلى تلغرام ثم يُنقل إلى sent\ (أو failed\ بعد 3 إخفاقات).
/// مسح دوري (لا FileSystemWatcher) كي لا نقرأ ملفاً ما زال يُنسخ.
/// </summary>
public sealed class OutboxWatcher : IDisposable
{
    private readonly string _folder;
    private readonly Func<string, string, CancellationToken, Task<string>> _send;
    private readonly Dictionary<string, int> _fails = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    public Action<string>? Log { get; set; }
    public int SettleMs { get; set; } = 3000;
    public int IntervalMs { get; set; } = 10_000;
    public long SingleMax { get; set; } = TelegramOutbox.SingleMax;
    public long PartSize { get; set; } = TelegramOutbox.PartSize;
    public int RetryDelayMs { get; set; } = 20_000;

    public OutboxWatcher(string folder, Func<string, string, CancellationToken, Task<string>> send)
    { _folder = folder; _send = send; Directory.CreateDirectory(folder); }

    public static string DefaultFolder() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raqeeb-Outbox");

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource(); var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await ScanOnceAsync(ct); } catch (OperationCanceledException) { return; } catch (Exception ex) { Log?.Invoke("خطأ صندوق الإرسال: " + ex.Message); }
                try { await Task.Delay(IntervalMs, ct); } catch { return; }
            }
        }, ct);
    }

    public void Dispose() { _cts?.Cancel(); _cts = null; }

    /// <summary>مسحة واحدة (عامّة للاختبارات). تعيد عدد العناصر المرسلة.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        var sent = 0;
        var sentDir = System.IO.Path.Combine(_folder, "sent"); var failedDir = System.IO.Path.Combine(_folder, "failed");
        foreach (var entry in Directory.EnumerateFileSystemEntries(_folder).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(entry);
            if (name.Equals("sent", StringComparison.OrdinalIgnoreCase) || name.Equals("failed", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("~$") || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var attr = File.GetAttributes(entry);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
            // مستقر؟ (لم يُعدَّل منذ SettleMs)
            var last = Directory.Exists(entry) ? LastWriteDeep(entry) : File.GetLastWriteTimeUtc(entry);
            if ((DateTime.UtcNow - last).TotalMilliseconds < SettleMs) continue;
            try
            {
                var r = await TelegramOutbox.SendAsync(entry, "", _send, null, ct, null, RetryDelayMs, SingleMax, PartSize);
                Directory.CreateDirectory(sentDir);
                Move(entry, System.IO.Path.Combine(sentDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + name));
                _fails.Remove(entry); sent++;
                Log?.Invoke("✈ " + r);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _fails[entry] = _fails.GetValueOrDefault(entry) + 1;
                Log?.Invoke($"⚠ فشل إرسال «{name}»: {ex.Message} (محاولة {_fails[entry]}/3)");
                if (_fails[entry] >= 3)
                {
                    try { Directory.CreateDirectory(failedDir); Move(entry, System.IO.Path.Combine(failedDir, name)); _fails.Remove(entry); } catch { }
                }
            }
        }
        return sent;
    }

    private static DateTime LastWriteDeep(string dir)
    {
        var t = Directory.GetLastWriteTimeUtc(dir);
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) if (f.LastWriteTimeUtc > t) t = f.LastWriteTimeUtc; } catch { }
        return t;
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }
}
