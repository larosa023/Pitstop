using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pitstop.Native;

namespace Pitstop.Services;

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public long VramBytes { get; init; }
    /// <summary>NVIDIA-style version, e.g. "566.36". Null when no driver is installed.</summary>
    public string? DriverVersion { get; init; }
    public string WindowsDriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    /// <summary>NVIDIA's build branch, e.g. "r565_90".</summary>
    public string BuildBranch { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public bool IsNotebook { get; init; }

    /// <summary>"GeForce RTX 4090" (no "NVIDIA" prefix), for matching NVIDIA's product list.</summary>
    public string ProductName => Name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? Name[7..].Trim() : Name.Trim();
}

/// <summary>Finds the NVIDIA card and its installed driver: NvAPI first, the registry as a fallback.</summary>
public static class GpuService
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static GpuInfo? Detect()
    {
        var reg = FromRegistry();

        string? nvName = null, nvVersion = null, branch = "";
        long vram = 0;
        if (NvApi.Initialize())
        {
            try
            {
                lock (NvApi.Lock)
                {
                    var gpus = NvApi.EnumPhysicalGpus();
                    if (gpus.Count > 0)
                    {
                        nvName = NvApi.GetFullName(gpus[0]);
                        vram = NvApi.GetFrameBufferBytes(gpus[0]);
                    }
                    var (v, b) = NvApi.GetDriverAndBranch();
                    nvVersion = Versions.FromNvApi(v);
                    branch = b;
                }
            }
            catch (Exception ex)
            {
                Log.Error("nvapi detect", ex);
            }
        }

        if (reg is null && nvName is null) return null;

        var name = nvName ?? reg?.Name ?? "NVIDIA graphics card";
        if (!name.StartsWith("NVIDIA", StringComparison.OrdinalIgnoreCase)) name = "NVIDIA " + name;
        return new GpuInfo
        {
            Name = name,
            VramBytes = vram > 0 ? vram : reg?.VramBytes ?? 0,
            DriverVersion = nvVersion ?? reg?.DriverVersion,
            WindowsDriverVersion = reg?.WindowsDriverVersion ?? "",
            DriverDate = reg?.DriverDate,
            BuildBranch = branch ?? "",
            DeviceId = reg?.DeviceId ?? "",
            IsNotebook = name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) || HasBattery(),
        };
    }

    /// <summary>Just the installed driver version, cheaply (used after an install).</summary>
    public static string? InstalledVersionFromRegistry() => FromRegistry()?.DriverVersion;

    private static GpuInfo? FromRegistry()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return null;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)).OrderBy(n => n))
            {
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k is null) continue;
                    var match = k.GetValue("MatchingDeviceId") as string ?? "";
                    var provider = k.GetValue("ProviderName") as string ?? "";
                    if (!match.Contains("ven_10de", StringComparison.OrdinalIgnoreCase) &&
                        !provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;

                    var winVer = k.GetValue("DriverVersion") as string ?? "";
                    long vram = 0;
                    switch (k.GetValue("HardwareInformation.qwMemorySize"))
                    {
                        case long l: vram = l; break;
                        case byte[] b when b.Length >= 8: vram = BitConverter.ToInt64(b, 0); break;
                    }
                    if (vram <= 0)
                    {
                        switch (k.GetValue("HardwareInformation.MemorySize"))
                        {
                            case int i: vram = (uint)i; break;
                            case byte[] b when b.Length >= 4: vram = BitConverter.ToUInt32(b, 0); break;
                        }
                    }
                    DateTime? date = null;
                    if (k.GetValue("DriverDate") is string ds &&
                        DateTime.TryParseExact(ds, new[] { "M-d-yyyy", "MM-dd-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        date = d;

                    return new GpuInfo
                    {
                        Name = k.GetValue("DriverDesc") as string ?? "NVIDIA graphics card",
                        VramBytes = vram,
                        WindowsDriverVersion = winVer,
                        DriverVersion = Versions.FromWindowsDriverVersion(winVer),
                        DriverDate = date,
                        DeviceId = match,
                    };
                }
                catch
                {
                    // some adapter subkeys are locked down; skip them
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("registry detect", ex);
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool HasBattery()
    {
        try
        {
            return GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;
        }
        catch
        {
            return false;
        }
    }
}
