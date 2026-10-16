using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace Raqeeb;

internal static class Program
{
    private const string Mutex = "Global\\Raqeeb.SingleInstance";
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    [STAThread]
    private static int Main(string[] args)
    {
        // أوضاع نصية بلا واجهة
        if (args.Contains("--version")) { Console.WriteLine("Raqeeb " + typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }
        if (args.Contains("--check"))
        {
            var cfg = Config.Load(ArgValue(args, "--env"));
            var p = cfg.Problems();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "raqeeb-check.txt"), $"env: {cfg.Path}\n" + (p.Count == 0 ? "OK" : string.Join("\n", p)));
            return p.Count == 0 ? 0 : 2;
        }

        using var mtx = new System.Threading.Mutex(true, Mutex, out var first);
        if (!first) return 0;

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new TrayApp(ArgValue(args, "--env")));
        return 0;
    }

    private static string? ArgValue(string[] a, string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    // ---------- بدء مع ويندوز
    public static bool Autostart
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Raqeeb") is string; }
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Raqeeb", "\"" + Environment.ProcessPath + "\""); else k.DeleteValue("Raqeeb", false);
    }

    // ---------- مساعد القفل (خدمة بصلاحية مسؤول)
    public static string HelperExe => Path.Combine(AppContext.BaseDirectory, "RaqeebHelper.exe");

    /// <summary>يشغّل المساعد مرفوعاً (UAC) بوضع التثبيت/الإزالة. يعيد رسالة النتيجة.</summary>
    public static string RunHelper(bool install)
    {
        if (!File.Exists(HelperExe)) return "RaqeebHelper.exe غير موجود بجانب Raqeeb.exe";
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var psi = new ProcessStartInfo(HelperExe, install ? $"--install --sid {sid}" : "--uninstall") { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi)!;
            p.WaitForExit(60_000);
            return p.ExitCode == 0 ? (install ? "تم تثبيت المساعد." : "أُزيل المساعد.") : $"فشل (رمز {p.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception) { return "أُلغي طلب صلاحية المسؤول."; }
        catch (Exception ex) { return "تعذّر: " + ex.Message; }
    }
}
