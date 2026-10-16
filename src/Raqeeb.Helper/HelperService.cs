using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json.Nodes;

namespace Raqeeb.Helper;

/// <summary>خدمة LocalSystem: تستقبل طلبات المستخدم المصرَّح (SID محدد) وتنفّذها على سطح Winlogon عبر عملية SYSTEM مؤقتة.</summary>
internal sealed class HelperService : ServiceBase
{
    private readonly SecurityIdentifier _user;
    private CancellationTokenSource? _cts;

    public HelperService(SecurityIdentifier user) { _user = user; ServiceName = Protocol.ServiceName; CanStop = true; }

    protected override void OnStart(string[] args) { _cts = new(); _ = Task.Run(() => Loop(_cts.Token)); }
    protected override void OnStop() { _cts?.Cancel(); }

    private static PipeSecurity Acl(SecurityIdentifier user)
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return ps;
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var srv = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Acl(_user));
                await srv.WaitForConnectionAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(30));
                await Serve(srv, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch { await Task.Delay(500, CancellationToken.None); }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string reply;
        try
        {
            var line = await ReadLine(pipe, ct);
            var req = Protocol.Parse(line, out var err);
            reply = req is null ? Protocol.Reply(false, err) : await Handle(req.Value, ct);
        }
        catch (Exception ex) { reply = Protocol.Reply(false, ex.Message); }
        var bytes = Encoding.UTF8.GetBytes(reply + "\n");
        await pipe.WriteAsync(bytes, ct); await pipe.FlushAsync(ct);
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    internal static async Task<string> ReadLine(Stream s, CancellationToken ct)
    {
        var buf = new byte[Protocol.MaxLine + 1]; var n = 0;
        var one = new byte[1];
        while (n <= Protocol.MaxLine)
        {
            var r = await s.ReadAsync(one, ct);
            if (r == 0 || one[0] == (byte)'\n') break;
            buf[n++] = one[0];
        }
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private async Task<string> Handle((string Op, string Secret, int Width, int Quality, JsonObject Args) q, CancellationToken ct)
    {
        switch (q.Op)
        {
            case "ping": return Protocol.Reply(true, more: o => { o["service"] = "ok"; o["session"] = Native.ActiveSession(); });
            case "state":
                {
                    var j = await RunInSession(new JsonObject { ["op"] = "state" }, ct);
                    return j.ToJsonString();
                }
            case "snap":
                {
                    var j = await RunInSession(new JsonObject { ["op"] = "snap", ["w"] = q.Width, ["q"] = q.Quality }, ct);
                    return j.ToJsonString();
                }
            case "input":
                {
                    var j = await RunInSession(new JsonObject { ["op"] = "input", ["args"] = q.Args.DeepClone(), ["w"] = q.Width, ["q"] = q.Quality }, ct);
                    return j.ToJsonString();
                }
            case "unlock":
                {
                    // الحماية من التخمين في الوكيل (يعرف النجاح عبر حالة القفل) وفي ويندوز نفسه؛ الخدمة تكتب فقط.
                    var j = await RunInSession(new JsonObject { ["op"] = "unlock", ["secret"] = q.Secret, ["w"] = q.Width, ["q"] = q.Quality }, ct);
                    return j.ToJsonString();
                }
        }
        return Protocol.Reply(false, "أمر غير معروف");
    }

    /// <summary>ينشئ عملية بتوكن winlogon في جلسة وحدة التحكم على سطح winlogon ويمرّر لها المهمة عبر أنبوب خاص بـSYSTEM.</summary>
    private async Task<JsonObject> RunInSession(JsonObject job, CancellationToken ct)
    {
        var exe = Environment.ProcessPath!;
        var name = "CPUH_" + Guid.NewGuid().ToString("N");
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var jobPipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 1 << 20, ps);
        var pid = Native.SpawnOnWinlogon($"\"{exe}\" --inject {name}");
        try
        {
            await jobPipe.WaitForConnectionAsync(ct);
            var jb = Encoding.UTF8.GetBytes(job.ToJsonString() + "\n");
            await jobPipe.WriteAsync(jb, ct); await jobPipe.FlushAsync(ct);
            var line = await ReadLine(jobPipe, ct);
            return JsonNode.Parse(line) as JsonObject ?? new JsonObject { ["ok"] = false, ["msg"] = "ردّ غير صالح" };
        }
        finally { try { Process.GetProcessById(pid).Kill(); } catch { } }
    }
}

internal static class Native
{
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr tok, uint access, IntPtr attr, int impLevel, int type, out IntPtr dup);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr tok, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle; public int x, y, xs, ys, xc, yc, fill, flags; public short show, res2; public IntPtr res2p, hIn, hOut, hErr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

    public static int ActiveSession() => (int)WTSGetActiveConsoleSessionId();

    public static int SpawnOnWinlogon(string cmd)
    {
        var sid = WTSGetActiveConsoleSessionId();
        if (sid == 0xFFFFFFFF) throw new InvalidOperationException("لا جلسة نشطة على وحدة التحكم");
        var wl = Process.GetProcessesByName("winlogon").FirstOrDefault(p => (uint)p.SessionId == sid)
                 ?? throw new InvalidOperationException("winlogon غير موجود في الجلسة");
        if (!OpenProcessToken(wl.Handle, 0x0002 | 0x0001 | 0x0008 | 0x0100, out var tok)) throw new Win32Exception();
        try
        {
            // TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY لازمة؛ ثم نسخة أساسية
            if (!DuplicateTokenEx(tok, 0x02000000, IntPtr.Zero, 2, 1, out var dup)) throw new Win32Exception();
            try
            {
                var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\winlogon" };
                if (!CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /*NO_WINDOW*/, IntPtr.Zero, null, ref si, out var pi)) throw new Win32Exception();
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                return pi.pid;
            }
            finally { CloseHandle(dup); }
        }
        finally { CloseHandle(tok); }
    }
}
