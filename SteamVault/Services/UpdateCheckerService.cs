using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SteamVault.Services;

public sealed record UpdateInfo(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string ReleaseNotes,
    string? DownloadUrl
);

public sealed class UpdateCheckerService
{
    public const string CurrentVersion = "1.6.0";
    public const string RepoOwner = "upwekee";
    public const string RepoName = "SilverManager";

    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SilverManager", CurrentVersion));
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var json = await http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var link) ? link.GetString() ?? "" : "";
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var dl))
                    {
                        downloadUrl = dl.GetString();
                        if (downloadUrl?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                            break;
                    }
                }
            }

            var cleanTag = tagName.TrimStart('v', 'V').Trim();
            var hasUpdate = IsNewerVersion(cleanTag, CurrentVersion);

            return new UpdateInfo(
                HasUpdate: hasUpdate,
                CurrentVersion: CurrentVersion,
                LatestVersion: string.IsNullOrEmpty(cleanTag) ? CurrentVersion : cleanTag,
                ReleaseUrl: string.IsNullOrEmpty(htmlUrl) ? $"https://github.com/{RepoOwner}/{RepoName}/releases" : htmlUrl,
                ReleaseNotes: body,
                DownloadUrl: downloadUrl
            );
        }
        catch (Exception ex)
        {
            return new UpdateInfo(
                HasUpdate: false,
                CurrentVersion: CurrentVersion,
                LatestVersion: CurrentVersion,
                ReleaseUrl: $"https://github.com/{RepoOwner}/{RepoName}/releases",
                ReleaseNotes: ex.Message,
                DownloadUrl: null
            );
        }
    }

    private static bool IsNewerVersion(string latestTag, string currentVer)
    {
        if (Version.TryParse(latestTag, out var latest) && Version.TryParse(currentVer, out var current))
        {
            return latest > current;
        }
        return false;
    }

    public static void OpenReleaseUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                url = $"https://github.com/{RepoOwner}/{RepoName}/releases";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public async Task DownloadAndInstallUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new ArgumentException("No download URL provided for update.");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SilverManager", CurrentVersion));

        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var tempPath = Path.Combine(Path.GetTempPath(), "SilverManager_update.exe");

        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
        {
            var buffer = new byte[8192];
            long totalRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                if (totalBytes > 0)
                {
                    progress?.Report((double)totalRead / totalBytes * 100.0);
                }
            }
        }

        ApplyUpdateAndRestart(tempPath);
    }

    private static void ApplyUpdateAndRestart(string downloadedTempPath)
    {
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(currentExe))
            currentExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SilverManager.exe");

        var pid = Process.GetCurrentProcess().Id;
        var batPath = Path.Combine(Path.GetTempPath(), "silvermanager_updater.bat");

        var script = $@"@echo off
:repeat
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I /N ""{pid}"" >NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /t 1 /nobreak >NUL
    goto repeat
)
copy /y ""{downloadedTempPath}"" ""{currentExe}""
del ""{downloadedTempPath}""
start """" ""{currentExe}""
del ""%~f0""
";
        File.WriteAllText(batPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);
        Environment.Exit(0);
    }
}
