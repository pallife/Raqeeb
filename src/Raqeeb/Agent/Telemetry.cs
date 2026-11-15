using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace Raqeeb;

/// <summary>قياسات خفيفة للداشبورد: حرارة، أقراص، شبكة (بايت/ث)، معالج، ذاكرة. تُحسب عند الطلب فقط (لا حلقة خلفية).</summary>
public static class Telemetry
{
    private static long _lastRx, _lastTx; private static DateTime _lastAt;
    private static PerformanceCounter? _cpu;

    public static JsonObject Snapshot()
    {
        var o = new JsonObject();
        try { _cpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total"); o["cpu"] = Math.Round(_cpu.NextValue(), 1); } catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject m in s.Get()) { o["ramTotalKb"] = Convert.ToInt64(m["TotalVisibleMemorySize"]); o["ramFreeKb"] = Convert.ToInt64(m["FreePhysicalMemory"]); }
        } catch { }
        // الحرارة: مناطق ACPI (قد لا تتوفر على كل الأجهزة)
        try
        {
            var temps = new JsonArray();
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementObject m in s.Get())
                temps.Add(new JsonObject { ["name"] = (string?)m["InstanceName"], ["c"] = Math.Round(Convert.ToDouble(m["CurrentTemperature"]) / 10.0 - 273.15, 1) });
            o["temps"] = temps;
        } catch { o["temps"] = new JsonArray(); }
        var disks = new JsonArray();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            disks.Add(new JsonObject { ["name"] = d.Name, ["total"] = d.TotalSize, ["free"] = d.AvailableFreeSpace });
        o["disks"] = disks;
        long rx = 0, tx = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        { var st = n.GetIPv4Statistics(); rx += st.BytesReceived; tx += st.BytesSent; }
        var now = DateTime.UtcNow; var dt = (now - _lastAt).TotalSeconds;
        if (_lastAt != default && dt > 0.2) { o["rxBps"] = (long)((rx - _lastRx) / dt); o["txBps"] = (long)((tx - _lastTx) / dt); }
        _lastRx = rx; _lastTx = tx; _lastAt = now;
        return o;
    }
}

