using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raqeeb;

/// <summary>
/// عميل مساعد الفتح (خدمة RaqeebHelper). الاتصال بأنبوب مسمّى محكوم بـACL لمستخدمك فقط.
/// السرّ يمرّ من الذاكرة إلى الأنبوب مباشرة ولا يُكتب في أي ملف.
/// </summary>
public sealed class UnlockHelperClient : IUnlockHelper
{
    public const string PipeName = "RaqeebHelper";
    private readonly string _pipe;
    private DateTime _checkedAt; private bool _installed;

    public UnlockHelperClient(string pipeName = PipeName) { _pipe = pipeName; }

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Raqeeb");

    /// <summary>هل الخدمة تردّ على ping؟ (يُخزَّن 5 ثوانٍ). لا يحجب خيط الواجهة: الفحص على خيط مستقل بمهلة قصيرة.</summary>
    public bool Installed
    {
        get
        {
            if (DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(5)) return _installed;
            _checkedAt = DateTime.UtcNow;
            try
            {
                var t = Task.Run(() => Call(new JsonObject { ["op"] = "ping" }, 1200, CancellationToken.None));
                _installed = t.Wait(2000) && t.Result?["ok"]?.GetValue<bool>() == true;
            }
            catch { _installed = false; }
            return _installed;
        }
    }

    private async Task<JsonObject?> Call(JsonObject req, int connectMs, CancellationToken ct)
    {
        await using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(connectMs, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");
        await c.WriteAsync(bytes, ct).ConfigureAwait(false); await c.FlushAsync(ct).ConfigureAwait(false);
        var buf = new MemoryStream(); var one = new byte[8192];
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct); to.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var n = await c.ReadAsync(one, to.Token).ConfigureAwait(false);
            if (n == 0) break;
            var nl = Array.IndexOf(one, (byte)'\n', 0, n);
            buf.Write(one, 0, nl >= 0 ? nl : n);
            if (nl >= 0) break;
            if (buf.Length > 8 * 1024 * 1024) throw new InvalidOperationException("ردّ كبير");
        }
        return JsonNode.Parse(buf.ToArray()) as JsonObject;
    }

    public async Task<UnlockResult> UnlockAsync(string secret, int snapWidth, CancellationToken ct = default)
    {
        try
        {
            var r = await Call(new JsonObject { ["op"] = "unlock", ["secret"] = secret, ["w"] = snapWidth, ["q"] = 50 }, 3000, ct);
            if (r is null) return new(false, "ردّ غير صالح", null);
            var ok = r["ok"]?.GetValue<bool>() == true;
            var jpg = r["jpeg"]?.GetValue<string>();
            return new(ok, r["msg"]?.GetValue<string>() ?? (ok ? "أُرسل" : "فشل"), jpg is null ? null : Convert.FromBase64String(jpg));
        }
        catch (Exception ex) { return new(false, ex is TimeoutException ? "المساعد لا يردّ" : ex.Message, null); }
    }

    public async Task<UnlockResult> InputAsync(JsonObject args, CancellationToken ct = default)
    {
        try
        {
            var r = await Call(new JsonObject { ["op"] = "input", ["args"] = args.DeepClone() }, 3000, ct);
            if (r is null) return new(false, "ردّ غير صالح", null);
            var ok = r["ok"]?.GetValue<bool>() == true;
            return new(ok, r["msg"]?.GetValue<string>() ?? (ok ? "تم" : "فشل"), null);
        }
        catch (Exception ex) { return new(false, ex.Message, null); }
    }

    public async Task<byte[]?> SnapAsync(int width, int quality, CancellationToken ct = default)
    {
        try
        {
            var r = await Call(new JsonObject { ["op"] = "snap", ["w"] = width, ["q"] = quality }, 3000, ct);
            var jpg = r?["jpeg"]?.GetValue<string>();
            return jpg is null ? null : Convert.FromBase64String(jpg);
        }
        catch { return null; }
    }
}

/// <summary>يرسل إشعاراً نصياً إلى تلغرام عبر مرحّل الويب (الذي يملك توكن البوت). الأخطاء تُبتلع.</summary>
public static class RelayNotifier
{
    /// <summary>يرفع ملفاً (حتى 49MB) إلى مرحّل الويب ليمرّره إلى محادثة تلغرام المربوطة. يعيد نص النتيجة أو يرمي.</summary>
    public static async Task<string> SendFileAsync(string baseUrl, string webToken, string path, string caption, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var http = handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        var u = new Uri(baseUrl.TrimEnd('/') + "/api/ql/sendfile?name=" + Uri.EscapeDataString(Path.GetFileName(path)) + "&caption=" + Uri.EscapeDataString(caption));
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var req = new HttpRequestMessage(HttpMethod.Post, u) { Content = new StreamContent(fs) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", webToken);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var why = body;
            try { why = JsonNode.Parse(body)?["detail"]?.GetValue<string>() ?? body; } catch { }
            throw new InvalidOperationException("رفض المرحّل: " + (why.Length > 160 ? why[..160] : why));
        }
        return "أُرسل إلى تلغرام: " + Path.GetFileName(path);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<bool> SendAsync(string baseUrl, string webToken, string text, HttpMessageHandler? handler = null)
    {
        try
        {
            var http = handler is null ? Http : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var u = new Uri(baseUrl.TrimEnd('/') + "/api/ql/notify");
            using var req = new HttpRequestMessage(HttpMethod.Post, u) { Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", webToken);
            using var res = await http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
