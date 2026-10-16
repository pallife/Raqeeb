using System.Text.Json;
using System.Text.Json.Nodes;

namespace Raqeeb.Helper;

/// <summary>بروتوكول الأنبوب بين التطبيق (مستخدم عادي) والخدمة: سطر JSON واحد لكل طلب وسطر للرد.</summary>
public static class Protocol
{
    public const string PipeName = "RaqeebHelper";
    public const string ServiceName = "RaqeebHelper";
    public const int Version = 1;
    public const int MaxLine = 64 * 1024;

    public static readonly string[] Ops = { "ping", "state", "unlock", "snap", "input" };

    public static string Reply(bool ok, string? msg = null, Action<JsonObject>? more = null)
    {
        var o = new JsonObject { ["ok"] = ok, ["v"] = Version };
        if (msg is not null) o["msg"] = msg;
        more?.Invoke(o);
        return o.ToJsonString();
    }

    /// <summary>يتحقق من طلب الأنبوب ويعيد (op, secret, width, quality) أو رسالة خطأ.</summary>
    public static (string Op, string Secret, int Width, int Quality, JsonObject Args)? Parse(string line, out string error)
    {
        error = "";
        if (line.Length is 0 or > MaxLine) { error = "طلب فارغ أو كبير"; return null; }
        JsonNode? n;
        try { n = JsonNode.Parse(line); } catch (JsonException) { error = "JSON غير صالح"; return null; }
        if (n is not JsonObject o) { error = "JSON غير صالح"; return null; }
        var op = o["op"]?.GetValue<string>() ?? "";
        if (Array.IndexOf(Ops, op) < 0) { error = "أمر غير معروف"; return null; }
        var secret = o["secret"]?.GetValue<string>() ?? "";
        if (op == "unlock" && (secret.Length == 0 || secret.Length > 256)) { error = "كلمة السر فارغة أو أطول من 256"; return null; }
        var w = Math.Clamp(o["w"]?.GetValue<int>() ?? 960, 320, 1920);
        var q = Math.Clamp(o["q"]?.GetValue<int>() ?? 55, 25, 90);
        var args = o["args"] as JsonObject ?? new JsonObject();
        if (op == "input" && args["kind"] is null) { error = "نوع الإدخال مفقود"; return null; }
        return (op, secret, w, q, args);
    }
}
