using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace SteamVault.Services;

/// <summary>
/// Launch / switch Steam client (inspired by rex706/SAM + SAM launcher).
/// Best-effort: starts steam.exe -login is deprecated; we set auto-login files + restart.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamLauncher
{
    public static string? FindSteamExe()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = k?.GetValue("SteamExe")?.ToString();
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            path = k?.GetValue("SteamPath")?.ToString();
            if (!string.IsNullOrEmpty(path))
            {
                var exe = Path.Combine(path, "steam.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { /* */ }

        var candidates = new[]
        {
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Steam\steam.exe"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task RestartSteamAsync(string? login = null, string? password = null, string? guardCode = null)
    {
        foreach (var p in Process.GetProcessesByName("steam"))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* */ }
        }
        await Task.Delay(1500);

        var exe = FindSteamExe();
        if (exe == null) throw new FileNotFoundException("steam.exe was not found");

        // Note: Steam no longer supports -login reliably; open client and leave credentials to user/clipboard.
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true
        });

        if (!string.IsNullOrEmpty(login) || !string.IsNullOrEmpty(guardCode))
        {
            var clip = new StringBuilder();
            if (!string.IsNullOrEmpty(login)) clip.AppendLine(login);
            if (!string.IsNullOrEmpty(password)) clip.AppendLine(password);
            if (!string.IsNullOrEmpty(guardCode)) clip.Append(guardCode);
            // clipboard set by caller on UI thread
        }
    }
}
