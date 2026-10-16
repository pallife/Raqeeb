using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json.Nodes;

namespace Raqeeb.Helper;

/// <summary>
/// الأوضاع:
///   --service --sid S-1-5-…   تشغيل كخدمة (يستدعيها مدير الخدمات)
///   --inject  &lt;pipe&gt;          عملية مؤقتة على سطح winlogon (تُشغَّل من الخدمة)
///   --install --sid S-1-5-…    تثبيت (يتطلب مسؤولاً): نسخ إلى Program Files + إنشاء الخدمة وتشغيلها
///   --uninstall                إزالة
/// </summary>
internal static class Program
{
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Raqeeb");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "RaqeebHelper.exe");

    private static string? Arg(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--service"))
            {
                var sid = Arg(args, "--sid") ?? throw new ArgumentException("--sid مطلوب");
                ServiceBase.Run(new HelperService(new SecurityIdentifier(sid)));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--inject") return Inject(args[1]);
            if (args.Contains("--install")) return Install(Arg(args, "--sid") ?? WindowsIdentity.GetCurrent().User!.Value);
            if (args.Contains("--uninstall")) return Uninstall();
            if (args.Contains("--version")) { Console.WriteLine(Protocol.Version); return 0; }
            Console.WriteLine("RaqeebHelper — يُشغَّل من برنامج رقيب.");
            return 0;
        }
        catch (Exception ex) { Log("fatal: " + ex.Message); Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static void Log(string s)
    {
        try { Directory.CreateDirectory(InstallDir); File.AppendAllText(Path.Combine(InstallDir, "helper.log"), $"{DateTime.Now:s} {s}\n"); } catch { }
    }

    // ---------- العملية المؤقتة على Winlogon
    private static int Inject(string pipe)
    {
        using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut);
        c.Connect(5000);
        var line = HelperService.ReadLine(c, CancellationToken.None).GetAwaiter().GetResult();
        var job = JsonNode.Parse(line) as JsonObject ?? new JsonObject();
        var res = new JsonObject { ["v"] = Protocol.Version, ["desktop"] = Injector.DesktopName() };
        var w = job["w"]?.GetValue<int>() ?? 960; var q = job["q"]?.GetValue<int>() ?? 55;
        switch (job["op"]?.GetValue<string>())
        {
            case "state":
                res["ok"] = true; res["onWinlogon"] = string.Equals(res["desktop"]!.GetValue<string>(), "Winlogon", StringComparison.OrdinalIgnoreCase);
                break;
            case "snap":
                {
                    var j = Injector.Snap(w, q);
                    res["ok"] = j is not null; if (j is not null) res["jpeg"] = Convert.ToBase64String(j); else res["msg"] = "تعذّر الالتقاط";
                    break;
                }
            case "input":
                {
                    try
                    {
                        // على شاشة القفل: الشاشة الأساسية فقط (بلا VIRTUALDESK) كما تلتقطها Snap
                        res["did"] = Raqeeb.InputInjector.Execute(job["args"] as JsonObject ?? new JsonObject(), virtualDesk: false);
                        res["ok"] = true;
                    }
                    catch (Exception ex) { res["ok"] = false; res["msg"] = ex.Message; }
                    break;
                }
            case "unlock":
                {
                    var secret = job["secret"]?.GetValue<string>() ?? "";
                    res["typed"] = Injector.Unlock(secret);
                    secret = "";
                    Thread.Sleep(1500);
                    var j = Injector.Snap(w, q);
                    res["ok"] = true; if (j is not null) res["jpeg"] = Convert.ToBase64String(j);
                    break;
                }
            default: res["ok"] = false; res["msg"] = "أمر غير معروف"; break;
        }
        var bytes = Encoding.UTF8.GetBytes(res.ToJsonString() + "\n");
        c.Write(bytes); c.Flush();
        return 0;
    }

    // ---------- التثبيت (مسؤول)
    private static int Sc(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
        Log($"sc {args.Split(' ')[0]} → {p.ExitCode}");
        return p.ExitCode;
    }

    private static int Install(string userSid)
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) { Console.Error.WriteLine("يتطلب صلاحية مسؤول"); return 5; }
        _ = new SecurityIdentifier(userSid); // يتحقق من الصيغة
        Directory.CreateDirectory(InstallDir);
        var self = Environment.ProcessPath!;
        Sc($"stop {Protocol.ServiceName}");
        Thread.Sleep(800);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) File.Copy(self, InstalledExe, true);
        var bin = $"\\\"{InstalledExe}\\\" --service --sid {userSid}";
        var exists = Sc($"query {Protocol.ServiceName}") == 0;
        var rc = exists
            ? Sc($"config {Protocol.ServiceName} binPath= \"{bin}\" start= auto obj= LocalSystem")
            : Sc($"create {Protocol.ServiceName} binPath= \"{bin}\" start= auto obj= LocalSystem DisplayName= \"Raqeeb Helper\"");
        if (rc != 0) return rc;
        Sc($"description {Protocol.ServiceName} \"يفتح قفل الشاشة بطلب مصرَّح من رقيب (جوالك). يمكن إزالته من التطبيق.\"");
        Sc($"failure {Protocol.ServiceName} reset= 86400 actions= restart/5000/restart/5000/\"\"/0");
        rc = Sc($"start {Protocol.ServiceName}");
        return rc is 0 or 1056 ? 0 : rc; // 1056: يعمل بالفعل
    }

    private static int Uninstall()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) { Console.Error.WriteLine("يتطلب صلاحية مسؤول"); return 5; }
        Sc($"stop {Protocol.ServiceName}"); Thread.Sleep(800);
        Sc($"delete {Protocol.ServiceName}");
        // لا نحذف الملف الجاري من نفسه؛ يُحذف المجلد بعد خروج هذه العملية
        try
        {
            var cmd = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
        return 0;
    }
}
