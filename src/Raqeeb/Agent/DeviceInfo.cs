using System.Runtime.InteropServices;

namespace Raqeeb;

public record DiskInfo(string Name, long TotalBytes, long FreeBytes, string Label)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
}

public record DeviceSnapshot(
    DateTimeOffset At, double CpuPercent, long RamTotal, long RamAvailable, List<DiskInfo> Disks,
    TimeSpan Uptime, int? BatteryPercent, bool? OnAcPower, int Cores, string Machine, string User, string Os)
{
    public double RamPercent => RamTotal <= 0 ? 0 : 100.0 * (RamTotal - RamAvailable) / RamTotal;
}

public enum DeviceClass { Light, Standard, Strong }

public record Advice(string Severity, string Title, string Detail); // Severity: info | warn | bad

/// <summary>قراءة موارد الجهاز دون مكتبات خارجية (P/Invoke خفيف) + تصنيف الجهاز لتتأقلم الواجهة معه.</summary>
public sealed class DeviceSampler
{
    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX s);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    private (ulong Idle, ulong Total)? _prev;

    /// <summary>نسبة استعمال المعالج منذ القراءة السابقة (أول استدعاء يقيس نافذة قصيرة).</summary>
    public double ReadCpu()
    {
        if (!GetSystemTimes(out var i, out var k, out var u)) return 0;
        var idle = i.Value; var total = k.Value + u.Value; // kernel يشمل idle
        if (_prev is null)
        {
            _prev = (idle, total);
            Thread.Sleep(250);
            if (!GetSystemTimes(out i, out k, out u)) return 0;
            idle = i.Value; total = k.Value + u.Value;
        }
        var (pi, pt) = _prev.Value;
        _prev = (idle, total);
        var dt = total - pt; var di = idle - pi;
        return dt == 0 ? 0 : Math.Clamp(100.0 * (dt - di) / dt, 0, 100);
    }

    public DeviceSnapshot Sample()
    {
        long total = 0, avail = 0;
        var ms = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(ms)) { total = (long)ms.ullTotalPhys; avail = (long)ms.ullAvailPhys; }

        var disks = new List<DiskInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                disks.Add(new DiskInfo(d.Name.TrimEnd('\\'), d.TotalSize, d.AvailableFreeSpace, d.VolumeLabel));
            }
            catch { }
        }

        int? battery = null; bool? ac = null;
        if (GetSystemPowerStatus(out var ps))
        {
            if (ps.BatteryLifePercent <= 100 && ps.BatteryFlag != 128) battery = ps.BatteryLifePercent; // 128 = لا بطارية
            ac = ps.ACLineStatus == 1 ? true : ps.ACLineStatus == 0 ? false : null;
        }
        return new DeviceSnapshot(DateTimeOffset.Now, ReadCpu(), total, avail, disks, TimeSpan.FromMilliseconds(Environment.TickCount64),
            battery, ac, Environment.ProcessorCount, Environment.MachineName, Environment.UserName, Environment.OSVersion.VersionString);
    }

    /// <summary>الجهاز الضعيف يحصل على واجهة/مهام أخفّ (فاصل مزامنة أطول، بلا رسوم متحركة، فحوص أقل).</summary>
    public static DeviceClass Classify(long ramTotal, int cores) =>
        ramTotal < 6L << 30 || cores <= 2 ? DeviceClass.Light :
        ramTotal >= 16L << 30 && cores >= 8 ? DeviceClass.Strong : DeviceClass.Standard;

    public static string Describe(DeviceClass c) => c switch
    {
        DeviceClass.Light => "جهاز محدود الموارد — فعّلنا الوضع الخفيف (فحوص أقل، بلا حركة)",
        DeviceClass.Strong => "جهاز قوي — كل الميزات بلا قيود",
        _ => "جهاز متوسط — إعدادات متوازنة",
    };

    /// <summary>أهم ما يستحق انتباهك الآن، مرتّباً بالخطورة. دالة نقية قابلة للاختبار.</summary>
    public static List<Advice> Advise(DeviceSnapshot s)
    {
        var res = new List<Advice>();
        if (s.RamPercent >= 90) res.Add(new("bad", "الذاكرة شبه ممتلئة", $"المستعمل {s.RamPercent:0}% — أغلق برامج ثقيلة أو أعد التشغيل."));
        else if (s.RamPercent >= 80) res.Add(new("warn", "الذاكرة مرتفعة", $"المستعمل {s.RamPercent:0}%."));
        if (s.CpuPercent >= 90) res.Add(new("warn", "المعالج مشغول جداً", $"{s.CpuPercent:0}% الآن — قد يكون نسخ/فحص يعمل."));
        foreach (var d in s.Disks)
        {
            var freeGb = d.FreeBytes / 1073741824.0;
            if (d.UsedPercent >= 95 || freeGb < 5) res.Add(new("bad", $"القرص {d.Name} شبه ممتلئ", $"متبقٍ {freeGb:0.0}GB فقط — النسخ والمزامنة قد تفشل."));
            else if (d.UsedPercent >= 88) res.Add(new("warn", $"القرص {d.Name} يمتلئ", $"متبقٍ {freeGb:0.0}GB ({100 - d.UsedPercent:0}%)."));
        }
        if (s.BatteryPercent is { } b && s.OnAcPower == false)
        {
            if (b <= 15) res.Add(new("bad", "البطارية منخفضة جداً", $"{b}% وغير موصول بالشاحن — اشحن قبل أي نسخة كبيرة."));
            else if (b <= 30) res.Add(new("warn", "البطارية منخفضة", $"{b}% — يُفضّل الشحن قبل النسخ الكبيرة."));
        }
        if (s.Uptime > TimeSpan.FromDays(14)) res.Add(new("info", "الجهاز يعمل منذ مدة طويلة", $"{(int)s.Uptime.TotalDays} يوماً بلا إعادة تشغيل — إعادة التشغيل تنظّف الذاكرة وتُكمل التحديثات."));
        return res.OrderBy(a => a.Severity switch { "bad" => 0, "warn" => 1, _ => 2 }).ToList();
    }
}
