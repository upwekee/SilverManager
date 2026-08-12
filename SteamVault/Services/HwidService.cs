using System.Management;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// HWID profile generate / read / apply (educational).
/// Profile generation inspired by luminary-cloud/steam-account-manager hwid_gen.
/// Full WMI-hook DLL injection is C++-only in SAM; here we apply OS-level MachineGuid + ComputerName
/// for Steam client launch isolation, plus store rich spoof profiles for comparison.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HwidService
{
    private static readonly string[] Gpus =
    [
        "NVIDIA GeForce RTX 3060|10DE|2503|12288",
        "NVIDIA GeForce RTX 3070|10DE|2484|8192",
        "NVIDIA GeForce RTX 4060|10DE|2882|8192",
        "NVIDIA GeForce RTX 4070|10DE|2786|12288",
        "AMD Radeon RX 6700 XT|1002|73DF|12288",
        "AMD Radeon RX 7600|1002|7480|8192",
        "Intel Arc A770|8086|56A0|16384"
    ];

    private static readonly string[] Boards =
    [
        "ASUS|ROG STRIX B550-F GAMING",
        "MSI|MAG B660 TOMAHAWK",
        "Gigabyte|B650 AORUS ELITE",
        "ASRock|B450M Pro4",
        "ASUS|TUF GAMING Z790-PLUS"
    ];

    private static readonly int[] RamMb = [8192, 16384, 32768, 65536];
    private static readonly (int w, int h, int r)[] Monitors = [(1920, 1080, 60), (1920, 1080, 144), (2560, 1440, 165), (2560, 1440, 144)];
    private static readonly string[] Sound = ["Realtek High Definition Audio", "NVIDIA High Definition Audio", "USB Audio Device"];
    private static readonly string[] Displays = ["Generic PnP Monitor", "Dell S2721DGF", "LG 27GL850", "ASUS VG279QM"];
    private static readonly string[] SsdSizes = ["512GB", "1TB", "2TB"];
    private static readonly string[] HddSizes = ["1TB", "2TB", "0"];

    private HwidApplyState? _applied;

    public HwidProfile ReadRealHardware()
    {
        var p = new HwidProfile
        {
            MachineGuid = ReadMachineGuid() ?? "(unknown)",
            PcName = Environment.MachineName,
            MacAddress = ReadPrimaryMac() ?? "(unknown)",
            DiskSerial = ReadDiskSerial() ?? "(unknown)",
            RamMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)),
        };

        try
        {
            using var searcher = new ManagementObjectSearcher("select Name, AdapterRAM, PNPDeviceID from Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                p.GpuName = o["Name"]?.ToString() ?? "";
                if (o["AdapterRAM"] != null && ulong.TryParse(o["AdapterRAM"].ToString(), out var ram))
                    p.GpuVramMb = (int)(ram / (1024 * 1024));
                break;
            }
        }
        catch { /* */ }

        try
        {
            using var searcher = new ManagementObjectSearcher("select Product, Manufacturer from Win32_BaseBoard");
            foreach (var o in searcher.Get())
            {
                p.BoardModel = o["Product"]?.ToString() ?? "";
                p.BoardManufacturer = o["Manufacturer"]?.ToString() ?? "";
                break;
            }
        }
        catch { /* */ }

        return p;
    }

    public HwidProfile GenerateProfile()
    {
        var gpu = Pick(Gpus).Split('|');
        var board = Pick(Boards).Split('|');
        var mon = Pick(Monitors);
        var p = new HwidProfile
        {
            MachineGuid = Guid.NewGuid().ToString(),
            MacAddress = MakeMac(),
            DiskSerial = MakeDiskSerial(),
            PcName = MakePcName(),
            GpuName = gpu[0],
            GpuVendorId = gpu[1],
            GpuDeviceId = gpu[2],
            GpuVramMb = int.Parse(gpu[3]),
            BoardManufacturer = board[0],
            BoardModel = board[1],
            RamMb = Pick(RamMb),
            MonitorWidth = mon.w,
            MonitorHeight = mon.h,
            MonitorRefresh = mon.r,
            StorageSsds = 1,
            StorageSsdSize = Pick(SsdSizes),
            StorageHdds = Pick(HddSizes) == "0" ? 0 : 1,
            StorageHddSize = Pick(HddSizes),
            SoundCard = Pick(Sound),
            DisplayModel = Pick(Displays),
            Enabled = true
        };
        return p;
    }

    public List<HwidCompareRow> Compare(HwidProfile? real, HwidProfile? spoof)
    {
        real ??= new HwidProfile();
        spoof ??= new HwidProfile();
        return
        [
            new() { Component = "MachineGuid", Real = real.MachineGuid, Spoofed = spoof.MachineGuid },
            new() { Component = "PC Name", Real = real.PcName, Spoofed = spoof.PcName },
            new() { Component = "MAC", Real = real.MacAddress, Spoofed = spoof.MacAddress },
            new() { Component = "Disk serial", Real = real.DiskSerial, Spoofed = spoof.DiskSerial },
            new() { Component = "GPU", Real = real.GpuName, Spoofed = spoof.GpuName },
            new() { Component = "Board", Real = $"{real.BoardManufacturer} {real.BoardModel}".Trim(), Spoofed = $"{spoof.BoardManufacturer} {spoof.BoardModel}".Trim() },
            new() { Component = "RAM MB", Real = real.RamMb.ToString(), Spoofed = spoof.RamMb.ToString() },
            new() { Component = "Monitor", Real = $"{real.MonitorWidth}x{real.MonitorHeight}@{real.MonitorRefresh}", Spoofed = $"{spoof.MonitorWidth}x{spoof.MonitorHeight}@{spoof.MonitorRefresh}" },
            new() { Component = "Sound", Real = real.SoundCard, Spoofed = spoof.SoundCard },
        ];
    }

    /// <summary>Always writes active profile JSON. Registry spoof only with admin.</summary>
    public void SaveActiveProfile(HwidProfile profile)
    {
        try
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SteamVault", "active_hwid_profile.json");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            File.WriteAllText(profilePath, System.Text.Json.JsonSerializer.Serialize(profile,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Apply OS-level identity (MachineGuid + ComputerName) when admin.
    /// Without admin: only saves profile JSON — no exception spam.
    /// </summary>
    /// <returns>true if registry was written; false if skipped/soft-fail</returns>
    public bool TryApplyForLaunch(HwidProfile profile, out string note)
    {
        SaveActiveProfile(profile);

        if (!IsAdmin())
        {
            note = "profile saved · registry skip (run as Admin for MachineGuid/PC name)";
            return false;
        }

        try
        {
            if (_applied != null) Restore();

            var state = new HwidApplyState
            {
                PreviousGuid = ReadMachineGuid(),
                PreviousName = Environment.MachineName
            };

            if (profile.SpoofMachineGuid && !string.IsNullOrWhiteSpace(profile.MachineGuid))
                WriteMachineGuid(profile.MachineGuid);

            if (profile.SpoofPcName && !string.IsNullOrWhiteSpace(profile.PcName))
                WriteComputerName(profile.PcName);

            _applied = state;
            note = "registry applied";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            note = "registry denied (not elevated)";
            return false;
        }
        catch (Exception ex)
        {
            note = "registry fail: " + ex.Message;
            return false;
        }
    }

    /// <summary>Legacy: soft-apply (no throw on no-admin).</summary>
    public void ApplyForLaunch(HwidProfile profile) =>
        TryApplyForLaunch(profile, out _);

    public void Restore()
    {
        if (_applied == null) return;
        try
        {
            if (!string.IsNullOrEmpty(_applied.PreviousGuid))
                WriteMachineGuid(_applied.PreviousGuid);
            if (!string.IsNullOrEmpty(_applied.PreviousName))
                WriteComputerName(_applied.PreviousName);
        }
        catch { /* best effort */ }
        _applied = null;
    }

    public bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch { return null; }
    }

    private static void WriteMachineGuid(string guid)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: true)
                        ?? throw new UnauthorizedAccessException("Administrator rights are required for MachineGuid");
        key.SetValue("MachineGuid", guid);
    }

    private static void WriteComputerName(string name)
    {
        // Active computer name (requires reboot for full effect; session name still old)
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", writable: true);
        key?.SetValue("ComputerName", name);
        using var key2 = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName", writable: true);
        key2?.SetValue("ComputerName", name);
    }

    private static string? ReadPrimaryMac()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "select MACAddress from Win32_NetworkAdapterConfiguration where IPEnabled=true");
            foreach (var o in searcher.Get())
            {
                var mac = o["MACAddress"]?.ToString();
                if (!string.IsNullOrEmpty(mac)) return mac.Replace(':', '-');
            }
        }
        catch { /* */ }
        return null;
    }

    private static string? ReadDiskSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("select SerialNumber from Win32_DiskDrive");
            foreach (var o in searcher.Get())
            {
                var s = o["SerialNumber"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { /* */ }
        return null;
    }

    private static string MakeMac()
    {
        var b = RandomNumberGenerator.GetBytes(6);
        b[0] = (byte)((b[0] & 0xFC) | 0x02);
        return string.Join('-', b.Select(x => x.ToString("X2")));
    }

    private static string MakeDiskSerial()
    {
        var b = RandomNumberGenerator.GetBytes(8);
        var hex = Convert.ToHexString(b);
        return $"0000_0000_0000_0001_{hex[..4]}_{hex[4..8]}_{hex[8..12]}_{hex[12..16]}.";
    }

    private static string MakePcName()
    {
        const string alnum = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var b = RandomNumberGenerator.GetBytes(7);
        var sb = new StringBuilder("DESKTOP-");
        foreach (var x in b) sb.Append(alnum[x % alnum.Length]);
        return sb.ToString();
    }

    private static T Pick<T>(T[] arr)
    {
        var b = RandomNumberGenerator.GetBytes(4);
        var v = BitConverter.ToUInt32(b);
        return arr[v % (uint)arr.Length];
    }

    private static T Pick<T>(IReadOnlyList<T> arr) => Pick(arr.ToArray());

    private sealed class HwidApplyState
    {
        public string? PreviousGuid { get; set; }
        public string? PreviousName { get; set; }
    }
}
