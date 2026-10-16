using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Raqeeb;

/// <summary>اتصال شبكي حيّ مع صاحبه من البرامج.</summary>
public record NetConn(string Proto, string LocalAddr, int LocalPort, string RemoteAddr, int RemotePort, string State,
    int Pid, string Process, string? ProcessPath)
{
    public bool IsListen => State == "LISTEN" || (Proto == "UDP");
}

/// <summary>نتيجة تحليل اتصال: من هو الطرف الآخر؟ ماذا يحدث؟ وهل هو خطير؟</summary>
public record NetFinding(NetConn Conn, string Direction, string Risk, string Who, string What, string Advice, string RemoteName);

/// <summary>قراءة جدول اتصالات TCP/UDP مع معرّف البرنامج المالك عبر iphlpapi (بلا صلاحيات مدير).</summary>
public static class NetworkMonitor
{
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int cls, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int cls, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

    private const int AF_INET = 2, AF_INET6 = 23, TCP_OWNER_PID_ALL = 5, UDP_OWNER_PID = 1;
    private static readonly string[] States = { "", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };

    private static byte[] ReadTable(Func<IntPtr, int, (uint Code, int Need)> call)
    {
        var (_, need) = call(IntPtr.Zero, 0);
        var size = Math.Max(need, 4096) + 4096;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var (code, n) = call(buf, size);
                if (code == 0) { var bytes = new byte[size]; Marshal.Copy(buf, bytes, 0, size); return bytes; }
                if (code != 122) return Array.Empty<byte>(); // ERROR_INSUFFICIENT_BUFFER
                size = Math.Max(n, size) + 8192;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return Array.Empty<byte>();
    }

    private static int Port(byte[] b, int off) => (b[off] << 8) | b[off + 1];

    public static List<NetConn> Snapshot()
    {
        var names = new Dictionary<int, string>();
        try { foreach (var p in Process.GetProcesses()) { names[p.Id] = p.ProcessName; p.Dispose(); } } catch { }
        var paths = new Dictionary<int, string?>();
        string? PathOf(int pid)
        {
            if (pid <= 4) return null;
            if (paths.TryGetValue(pid, out var c)) return c;
            string? res = null;
            var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h != IntPtr.Zero)
            {
                try { var sb = new StringBuilder(1024); var n = sb.Capacity; if (QueryFullProcessImageName(h, 0, sb, ref n)) res = sb.ToString(); }
                finally { CloseHandle(h); }
            }
            return paths[pid] = res;
        }
        string NameOf(int pid) => pid == 0 ? "System Idle" : pid == 4 ? "System" : names.TryGetValue(pid, out var n) ? n : $"pid-{pid}";

        var res = new List<NetConn>();
        foreach (var af in new[] { AF_INET, AF_INET6 })
        {
            // TCP
            var tcp = ReadTable((p, s) => { var sz = s; var r = GetExtendedTcpTable(p, ref sz, false, af, TCP_OWNER_PID_ALL, 0); return (r, sz); });
            if (tcp.Length >= 4)
            {
                var count = BitConverter.ToInt32(tcp, 0);
                var rowSize = af == AF_INET ? 24 : 56;
                for (var i = 0; i < count && 4 + (i + 1) * rowSize <= tcp.Length; i++)
                {
                    var o = 4 + i * rowSize;
                    if (af == AF_INET)
                    {
                        var state = BitConverter.ToInt32(tcp, o);
                        var la = new IPAddress(BitConverter.ToUInt32(tcp, o + 4)).ToString();
                        var lp = Port(tcp, o + 8);
                        var ra = new IPAddress(BitConverter.ToUInt32(tcp, o + 12)).ToString();
                        var rp = Port(tcp, o + 16);
                        var pid = BitConverter.ToInt32(tcp, o + 20);
                        res.Add(new NetConn("TCP", la, lp, ra, rp, state is >= 0 and < 13 ? States[state] : state.ToString(), pid, NameOf(pid), PathOf(pid)));
                    }
                    else
                    {
                        var la = new IPAddress(tcp.AsSpan(o, 16).ToArray()).ToString();
                        var lp = Port(tcp, o + 20);
                        var ra = new IPAddress(tcp.AsSpan(o + 24, 16).ToArray()).ToString();
                        var rp = Port(tcp, o + 44);
                        var state = BitConverter.ToInt32(tcp, o + 48);
                        var pid = BitConverter.ToInt32(tcp, o + 52);
                        res.Add(new NetConn("TCP", la, lp, ra, rp, state is >= 0 and < 13 ? States[state] : state.ToString(), pid, NameOf(pid), PathOf(pid)));
                    }
                }
            }
            // UDP (نقاط الاستماع فقط)
            var udp = ReadTable((p, s) => { var sz = s; var r = GetExtendedUdpTable(p, ref sz, false, af, UDP_OWNER_PID, 0); return (r, sz); });
            if (udp.Length >= 4)
            {
                var count = BitConverter.ToInt32(udp, 0);
                var rowSize = af == AF_INET ? 12 : 28;
                for (var i = 0; i < count && 4 + (i + 1) * rowSize <= udp.Length; i++)
                {
                    var o = 4 + i * rowSize;
                    if (af == AF_INET)
                        res.Add(new NetConn("UDP", new IPAddress(BitConverter.ToUInt32(udp, o)).ToString(), Port(udp, o + 4), "", 0, "UDP", BitConverter.ToInt32(udp, o + 8), NameOf(BitConverter.ToInt32(udp, o + 8)), PathOf(BitConverter.ToInt32(udp, o + 8))));
                    else
                        res.Add(new NetConn("UDP", new IPAddress(udp.AsSpan(o, 16).ToArray()).ToString(), Port(udp, o + 20), "", 0, "UDP", BitConverter.ToInt32(udp, o + 24), NameOf(BitConverter.ToInt32(udp, o + 24)), PathOf(BitConverter.ToInt32(udp, o + 24))));
                }
            }
        }
        return res;
    }
}

/// <summary>تحليل الاتصالات وشرحها بالعربية: لمن؟ ماذا؟ هل هو خطير؟ — منطق نقيّ بلا شبكة قابل للاختبار.</summary>
public static class NetExplain
{
    public static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) return false;
            var b6 = a.GetAddressBytes();
            if ((b6[0] & 0xFE) == 0xFC) return false; // ULA
            if (a.IsIPv4MappedToIPv6) return IsPublic(a.MapToIPv4().ToString());
            return true;
        }
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] >= 224 || b[0] == 0 || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    private static bool IsLocalOrLan(string ip) => !IsPublic(ip);
    private static bool IsAllInterfaces(string ip) => ip is "0.0.0.0" or "::";

    // أطراف معروفة بحسب اسم المضيف العكسي (لاحقة النطاق)
    private static readonly (string Suffix, string Who)[] Providers =
    {
        ("microsoft.com", "مايكروسوفت (تحديثات/خدمات ويندوز)"), ("windows.com", "مايكروسوفت (ويندوز)"), ("msedge.net", "مايكروسوفت (Edge)"),
        ("azure.com", "مايكروسوفت Azure"), ("office.com", "مايكروسوفت Office"), ("live.com", "مايكروسوفت (حساب)"), ("bing.com", "Bing"),
        ("1e100.net", "Google"), ("google.com", "Google"), ("googleusercontent.com", "Google (محتوى/سحابة)"), ("gstatic.com", "Google"), ("googleapis.com", "Google API"),
        ("cloudflare.com", "Cloudflare (شبكة توصيل)"), ("cloudfront.net", "Amazon CloudFront"), ("amazonaws.com", "Amazon AWS"), ("akamaitechnologies.com", "Akamai (CDN)"),
        ("akamai.net", "Akamai (CDN)"), ("fastly.net", "Fastly (CDN)"), ("github.com", "GitHub"), ("githubusercontent.com", "GitHub"), ("anthropic.com", "Anthropic (Claude)"),
        ("claude.ai", "Claude"), ("telegram.org", "تلغرام"), ("t.me", "تلغرام"), ("whatsapp.net", "WhatsApp"), ("facebook.com", "Meta"), ("fbcdn.net", "Meta"),
        ("apple.com", "Apple"), ("adobe.com", "Adobe"), ("adobe.io", "Adobe"), ("mozilla.com", "Mozilla"), ("mozilla.net", "Mozilla"), ("sslip.io", "سيرفرك (sslip.io)"),
        ("spotify.com", "Spotify"), ("youtube.com", "YouTube"), ("ytimg.com", "YouTube"), ("dropbox.com", "Dropbox"), ("zoom.us", "Zoom"), ("slack.com", "Slack"),
        ("npmjs.org", "npm"), ("python.org", "Python"), ("nuget.org", "NuGet"), ("docker.io", "Docker"), ("brave.com", "Brave"), ("opera.com", "Opera"),
    };

    private static readonly Dictionary<string, string> ProcDesc = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "متصفح Chrome", ["msedge"] = "متصفح Edge", ["firefox"] = "متصفح Firefox", ["brave"] = "متصفح Brave", ["opera"] = "متصفح Opera",
        ["svchost"] = "خدمات نظام ويندوز", ["system"] = "نواة النظام", ["services"] = "مدير خدمات ويندوز", ["lsass"] = "أمان ويندوز (المصادقة)",
        ["wininit"] = "نظام ويندوز", ["spoolsv"] = "طباعة", ["searchhost"] = "بحث ويندوز", ["onedrive"] = "OneDrive",
        ["raqeeb"] = "رقيب (هذا البرنامج)", ["claude"] = "Claude Code", ["node"] = "Node.js (بيئة تطوير)", ["python"] = "Python",
        ["code"] = "VS Code", ["ssh"] = "اتصال SSH", ["git"] = "Git", ["teams"] = "Microsoft Teams", ["telegram"] = "تلغرام", ["whatsapp"] = "WhatsApp",
        ["spotify"] = "Spotify", ["discord"] = "Discord", ["dropbox"] = "Dropbox", ["steam"] = "Steam", ["explorer"] = "مستكشف ويندوز",
        ["winget"] = "مدير حزم ويندوز", ["dotnet"] = ".NET", ["antigravity"] = "Antigravity", ["syncthing"] = "Syncthing",
    };

    private static readonly Dictionary<int, string> PortDesc = new()
    {
        [20] = "FTP (غير مشفّر)", [21] = "FTP (غير مشفّر)", [22] = "SSH (إدارة مشفّرة)", [23] = "Telnet (غير مشفّر — خطير)", [25] = "بريد SMTP",
        [53] = "DNS (ترجمة الأسماء)", [67] = "DHCP", [80] = "HTTP (ويب غير مشفّر)", [110] = "بريد POP3", [123] = "ضبط الوقت NTP", [135] = "RPC ويندوز",
        [137] = "NetBIOS", [139] = "مشاركة ملفات NetBIOS", [143] = "بريد IMAP", [443] = "HTTPS (ويب مشفّر)", [445] = "مشاركة ملفات SMB",
        [465] = "بريد SMTPS", [587] = "بريد SMTP", [993] = "بريد IMAPS", [995] = "بريد POP3S", [1900] = "اكتشاف الأجهزة SSDP", [3306] = "قاعدة MySQL",
        [3389] = "سطح المكتب البعيد RDP", [5353] = "اكتشاف mDNS", [5432] = "قاعدة PostgreSQL", [5985] = "إدارة WinRM", [5986] = "إدارة WinRM مشفّرة",
        [6379] = "Redis", [8080] = "ويب بديل", [8443] = "ويب مشفّر بديل", [27017] = "قاعدة MongoDB", [5228] = "إشعارات Google", [5222] = "XMPP/تلغرام",
    };

    private static readonly HashSet<int> AdminPorts = new() { 22, 23, 135, 139, 445, 3389, 5985, 5986, 5900 };
    private static readonly HashSet<int> SuspiciousRemotePorts = new() { 4444, 31337, 1337, 12345, 54321, 6666, 6667, 6697, 9001, 9030, 9050, 9051 };
    private static readonly string[] SuspiciousPathParts =
        { @"\appdata\local\temp\", @"\downloads\", @"\users\public\", @"\$recycle.bin\", @"\windows\temp\", @"\programdata\" };

    public static string DescribeProcess(string name) =>
        ProcDesc.TryGetValue(name, out var d) ? d : $"برنامج «{name}»";

    public static string DescribePort(int port) => PortDesc.TryGetValue(port, out var d) ? d : (port >= 49152 ? "منفذ مؤقت" : $"منفذ {port}");

    private static bool IsSystemPath(string? p) =>
        p is not null && (p.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                          || p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase));

    private static bool IsRiskyPath(string? p) => p is not null && SuspiciousPathParts.Any(s => p.Contains(s, StringComparison.OrdinalIgnoreCase)) && !IsSystemPath(p);

    public static string ProviderOf(string? dnsName)
    {
        if (string.IsNullOrEmpty(dnsName)) return "";
        foreach (var (suf, who) in Providers)
            if (dnsName.Equals(suf, StringComparison.OrdinalIgnoreCase) || dnsName.EndsWith("." + suf, StringComparison.OrdinalIgnoreCase)) return who;
        return "";
    }

    public static List<NetFinding> Analyze(IEnumerable<NetConn> conns, Func<string, string?> reverseDns)
    {
        var list = conns.ToList();
        var listenPorts = list.Where(c => c.IsListen).Select(c => c.LocalPort).ToHashSet();
        var perProc = list.Where(c => c.State == "ESTABLISHED" && IsPublic(c.RemoteAddr)).GroupBy(c => c.Pid).ToDictionary(g => g.Key, g => g.Count());
        var res = new List<NetFinding>();
        foreach (var c in list)
        {
            if (c.State is "TIME_WAIT" or "CLOSE_WAIT" or "FIN_WAIT1" or "FIN_WAIT2" or "LAST_ACK" or "CLOSING" or "CLOSED" or "DELETE_TCB") continue;
            var proc = DescribeProcess(c.Process);
            var known = ProcDesc.ContainsKey(c.Process);
            var sys = IsSystemPath(c.ProcessPath);

            if (c.IsListen)
            {
                var all = IsAllInterfaces(c.LocalAddr) || IsPublic(c.LocalAddr) || (!IsLocalOrLan(c.LocalAddr) && c.LocalAddr != "127.0.0.1");
                var loop = c.LocalAddr is "127.0.0.1" or "::1";
                var risk = "low"; var advice = "طبيعي: خدمة محلية تستمع داخل جهازك فقط.";
                var who = loop ? "هذا الجهاز نفسه فقط" : "أي جهاز يصل إلى شبكتك";
                var what = $"{proc} يستمع على المنفذ {c.LocalPort} ({DescribePort(c.LocalPort)}).";
                if (!loop)
                {
                    var sysProc = c.Process.Equals("System", StringComparison.OrdinalIgnoreCase) || c.Process.Equals("svchost", StringComparison.OrdinalIgnoreCase) || c.Process.Equals("lsass", StringComparison.OrdinalIgnoreCase);
                    if (AdminPorts.Contains(c.LocalPort) && sysProc && c.LocalPort is 135 or 139 or 445 or 5985 or 5986 && (sys || c.Pid <= 4))
                    {
                        risk = "medium";
                        advice = $"طبيعي في ويندوز ({DescribePort(c.LocalPort)}) لكنه يجب ألا يكون متاحاً من الإنترنت: تأكد أن الشبكة معرّفة «خاصة» وأن جدار حماية ويندوز مفعّل وأن الراوتر لا يفتح هذا المنفذ للخارج. إن كنت لا تشارك ملفات فعطّل «مشاركة الملفات والطابعات».";
                    }
                    else if (AdminPorts.Contains(c.LocalPort)) { risk = "high"; advice = $"المنفذ {c.LocalPort} ({DescribePort(c.LocalPort)}) مفتوح لكل الشبكة — إن لم تكن تحتاجه عطّل الخدمة أو اغلقه بجدار الحماية، خاصة إن كان جهازك متصلاً مباشرة بالإنترنت."; }
                    else if (all && !sys && !known) { risk = "medium"; advice = "برنامج غير معروف يفتح منفذاً للشبكة. تأكد أنك تعرفه؛ وإلا أغلق البرنامج أو امنعه من جدار الحماية."; }
                    else { advice = "منفذ مفتوح للشبكة المحلية — شائع لبرامج المشاركة والاكتشاف، ولا خطر إن كان البرنامج مألوفاً."; }
                }
                res.Add(new NetFinding(c, "listen", risk, who, what, advice, ""));
                continue;
            }

            var remotePublic = IsPublic(c.RemoteAddr);
            var inbound = listenPorts.Contains(c.LocalPort) && c.LocalPort < 49152;
            var dir = inbound ? "in" : "out";
            string? dns = remotePublic ? reverseDns(c.RemoteAddr) : null;
            var provider = ProviderOf(dns);
            var who2 = !remotePublic
                ? (c.RemoteAddr is "127.0.0.1" or "::1" ? "هذا الجهاز نفسه (اتصال داخلي بين برامجك)" : "جهاز في شبكتك المحلية")
                : provider.Length > 0 ? provider : dns is { Length: > 0 } ? $"خادم «{dns}»" : "خادم على الإنترنت بلا اسم معروف";
            var portName = DescribePort(inbound ? c.LocalPort : c.RemotePort);
            var what2 = inbound
                ? $"{proc} يستقبل اتصالاً واردًا على المنفذ {c.LocalPort} ({portName}) من {c.RemoteAddr}."
                : $"{proc} يتصل بـ {who2} ({c.RemoteAddr}:{c.RemotePort}) عبر {portName}.";
            var risk2 = "low"; var adv = "اتصال اعتيادي.";

            if (!remotePublic) { adv = "اتصال محلي/شبكة منزلية — لا يخرج إلى الإنترنت."; }
            else if (inbound && AdminPorts.Contains(c.LocalPort))
            { risk2 = "high"; adv = $"اتصال وارد من الإنترنت إلى خدمة إدارة ({portName}). إن لم تكن أنت أو شخص تعرفه فاقطعه فوراً وأغلق الخدمة وغيّر كلمات السر."; }
            else if (SuspiciousRemotePorts.Contains(c.RemotePort))
            { risk2 = "high"; adv = $"المنفذ البعيد {c.RemotePort} يُستعمل عادةً من برامج التحكم عن بُعد/Tor/IRC. تحقّق من البرنامج «{c.Process}» فوراً."; }
            else if (IsRiskyPath(c.ProcessPath))
            { risk2 = "high"; adv = $"البرنامج يعمل من مجلد مؤقت/تنزيلات ({c.ProcessPath}) ويتصل بالإنترنت — نمط شائع للبرمجيات الخبيثة. افحصه بمضاد الفيروسات."; }
            else if (provider.Length == 0 && !known && !sys)
            { risk2 = "medium"; adv = $"برنامج غير معروف يتصل بخادم غير معروف. إن لم تتوقعه، ابحث عن «{c.Process}» أو أغلقه وراقب."; }
            else if (provider.Length == 0 && !known && dns is null && perProc.TryGetValue(c.Pid, out var n) && n > 25)
            { risk2 = "medium"; adv = $"«{c.Process}» يفتح {n} اتصالاً بعناوين بلا أسماء — سلوك قد يكون مزامنة ثقيلة أو أداة غير مرغوبة."; }
            else if (provider.Length > 0) adv = "اتصال بمزوّد معروف — اعتيادي.";
            else if (known || sys) adv = "برنامج معروف — الاتصال متوقع.";

            res.Add(new NetFinding(c, dir, risk2, who2, what2, adv, dns ?? ""));
        }
        return res.OrderBy(f => f.Risk switch { "high" => 0, "medium" => 1, _ => 2 }).ThenBy(f => f.Conn.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>ذاكرة مؤقتة للبحث العكسي في DNS مع مهلة قصيرة كي لا تتجمّد الواجهة.</summary>
public sealed class ReverseDnsCache
{
    private readonly Dictionary<string, string?> _cache = new();
    private readonly object _gate = new();

    public string? TryGet(string ip) { lock (_gate) return _cache.TryGetValue(ip, out var v) ? v : null; }

    public async Task ResolveAllAsync(IEnumerable<string> ips, int max = 80, int timeoutMs = 1500)
    {
        var todo = ips.Distinct().Where(ip => { lock (_gate) return !_cache.ContainsKey(ip); }).Take(max).ToList();
        using var sem = new SemaphoreSlim(12);
        await Task.WhenAll(todo.Select(async ip =>
        {
            await sem.WaitAsync();
            try
            {
                string? name = null;
                try
                {
                    var t = Dns.GetHostEntryAsync(ip);
                    if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t) name = t.Result.HostName;
                }
                catch { }
                lock (_gate) _cache[ip] = name;
            }
            finally { sem.Release(); }
        }));
    }
}
