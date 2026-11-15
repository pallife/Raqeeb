using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Raqeeb;

/// <summary>جلسات تيرمينال حقيقية عبر ConPTY (PowerShell/cmd/claude). المخرجات تُخزَّن في ذاكرة حلقية ويقرأها الجوال بمؤشر.</summary>
public sealed class TerminalManager : IDisposable
{
    private const int MaxSessions = 4;
    private const int RingBytes = 256 * 1024;
    private readonly object _lock = new();
    private readonly Dictionary<string, PtySession> _s = new();

    public string Open(string shell, int cols, int rows, string? cwd)
    {
        lock (_lock)
        {
            foreach (var k in _s.Where(p => p.Value.Exited && DateTime.UtcNow - p.Value.LastUse > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToList()) { _s[k].Dispose(); _s.Remove(k); }
            if (_s.Count >= MaxSessions) throw new InvalidOperationException("وصلت الحد الأقصى من جلسات التيرمينال (4)");
            var cmd = shell switch
            {
                "cmd" => "cmd.exe",
                "pwsh" => "pwsh.exe -NoLogo",
                "claude" => "powershell.exe -NoLogo -NoExit -Command claude",
                _ => "powershell.exe -NoLogo",
            };
            var id = Guid.NewGuid().ToString("N")[..10];
            _s[id] = new PtySession(cmd, (short)Math.Clamp(cols, 20, 300), (short)Math.Clamp(rows, 5, 100), cwd);
            return id;
        }
    }

    public PtySession Get(string id)
    {
        lock (_lock) return _s.TryGetValue(id, out var v) ? v : throw new InvalidOperationException("جلسة التيرمينال غير موجودة");
    }

    public IEnumerable<object> List()
    {
        lock (_lock) return _s.Select(p => (object)new { id = p.Key, exited = p.Value.Exited, title = p.Value.Cmd }).ToList();
    }

    public void Close(string id)
    {
        lock (_lock) { if (_s.Remove(id, out var v)) v.Dispose(); }
    }

    public void Dispose() { lock (_lock) { foreach (var v in _s.Values) v.Dispose(); _s.Clear(); } }

    public sealed class PtySession : IDisposable
    {
        public string Cmd { get; }
        public DateTime LastUse { get; private set; } = DateTime.UtcNow;
        public bool Exited { get; private set; }
        private readonly IntPtr _hpc;
        private readonly SafeFileHandle _inWrite, _outRead;
        private readonly IntPtr _proc, _thread;
        private readonly byte[] _ring = new byte[RingBytes];
        private long _total; // إجمالي البايتات المكتوبة منذ البداية
        private readonly object _rl = new();
        private readonly object _wl = new();
        private FileStream _in = null!;

        public PtySession(string cmd, short cols, short rows, string? cwd)
        {
            Cmd = cmd;
            if (!NativeMethods.CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0) || !NativeMethods.CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0))
                throw new Win32Exception();
            var hr = NativeMethods.CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _hpc);
            if (hr != 0) throw new InvalidOperationException("ConPTY غير مدعوم (يحتاج ويندوز 10 1809+)");
            inRead.Dispose(); outWrite.Dispose();

            var size = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            var attrs = Marshal.AllocHGlobal(size);
            NativeMethods.InitializeProcThreadAttributeList(attrs, 1, 0, ref size);
            NativeMethods.UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x00020016, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero);
            var si = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() }, lpAttributeList = attrs };
            var dir = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!NativeMethods.CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, dir, ref si, out var pi))
                throw new Win32Exception();
            _proc = pi.hProcess; _thread = pi.hThread;
            _in = new FileStream(_inWrite, FileAccess.Write, 1, false);
            new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" }.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            try
            {
                using var fs = new FileStream(_outRead, FileAccess.Read, 8192, false);
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    lock (_rl)
                    {
                        for (var i = 0; i < n; i++) _ring[(_total + i) % RingBytes] = buf[i];
                        _total += n;
                    }
                }
            }
            catch { }
            Exited = true;
        }

        /// <summary>يعيد البايتات منذ المؤشر (base64) والمؤشر الجديد. إن تجاوز المؤشر الذاكرة الحلقية يبدأ من أقدم المتاح.</summary>
        public (string b64, long next, bool reset) Read(long since)
        {
            LastUse = DateTime.UtcNow;
            lock (_rl)
            {
                var start = Math.Max(since, _total - RingBytes);
                var reset = start > since;
                var len = (int)(_total - start);
                var o = new byte[len];
                for (var i = 0; i < len; i++) o[i] = _ring[(start + i) % RingBytes];
                return (Convert.ToBase64String(o), _total, reset);
            }
        }

        public void Write(string text)
        {
            LastUse = DateTime.UtcNow;
            if (Exited) throw new InvalidOperationException("انتهت الجلسة");
            var b = Encoding.UTF8.GetBytes(text);
            lock (_wl) { _in.Write(b, 0, b.Length); _in.Flush(); }
        }

        public void Resize(short cols, short rows) => NativeMethods.ResizePseudoConsole(_hpc, new COORD { X = Math.Clamp(cols, (short)20, (short)300), Y = Math.Clamp(rows, (short)5, (short)100) });

        public void Dispose()
        {
            try { NativeMethods.TerminateProcess(_proc, 0); } catch { }
            try { NativeMethods.ClosePseudoConsole(_hpc); } catch { }
            try { _inWrite.Dispose(); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO { public int cb; public IntPtr lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CreatePipe(out SafeFileHandle r, out SafeFileHandle w, IntPtr attrs, int size);
        [DllImport("kernel32.dll")] public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
        [DllImport("kernel32.dll")] public static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
        [DllImport("kernel32.dll")] public static extern void ClosePseudoConsole(IntPtr hpc);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool CreateProcess(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
    }
}

