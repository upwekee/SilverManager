using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamVault.Models;

namespace SteamVault.Services;

public sealed class AccountStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new FlexibleStringConverter(),
            new FlexibleUlongConverter()
        }
    };

    public ObservableCollection<SteamAccount> Accounts { get; } = new();

    public AccountStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamVault");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "accounts.json");
        Load();
    }

    public void Load()
    {
        Accounts.Clear();
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<StoredAccount>>(json, JsonOpts) ?? [];
            foreach (var s in list)
            {
                var acc = new SteamAccount
                {
                    Id = s.Id,
                    Login = s.Login,
                    Password = s.Password,
                    SharedSecret = s.SharedSecret,
                    IdentitySecret = s.IdentitySecret,
                    DeviceId = s.DeviceId,
                    SteamId64 = s.SteamId64,
                    PersonaName = s.PersonaName,
                    AvatarUrl = s.AvatarUrl,
                    HasMaFile = !string.IsNullOrEmpty(s.SharedSecret),
                    InventoryCount = s.InventoryCount,
                    InventoryValue = s.InventoryValue,
                    InventoryScanned = s.InventoryScanned || s.InventoryCount > 0,
                    TrustLabel = s.TrustLabel ?? "green",
                    Notes = s.Notes,
                    GroupName = s.GroupName,
                    Review = s.Review,
                    Hwid = s.Hwid,
                    IsMarkedBanned = s.IsMarkedBanned,
                    BanReason = s.BanReason,
                    BannedAt = s.BannedAt,
                    FailStreak = s.FailStreak,
                    Proxy = s.Proxy,
                    MarketApiKey = s.MarketApiKey,
                    IsWarehouse = s.IsWarehouse,
                    OwnTradeUrl = s.OwnTradeUrl,
                    MaFilePath = s.MaFilePath
                };
                acc.Review?.RecomputeBadge();
                acc.OnReviewChanged();
                Accounts.Add(acc);
            }
        }
        catch
        {
            // corrupt store — start empty
        }
    }

    public void Save()
    {
        var list = Accounts.Select(a => new StoredAccount
        {
            Id = a.Id,
            Login = a.Login,
            Password = a.Password,
            SharedSecret = a.SharedSecret,
            IdentitySecret = a.IdentitySecret,
            DeviceId = a.DeviceId,
            SteamId64 = a.SteamId64,
            PersonaName = a.PersonaName,
            AvatarUrl = a.AvatarUrl,
            InventoryCount = a.InventoryCount,
            InventoryValue = a.InventoryValue,
            InventoryScanned = a.InventoryScanned,
            TrustLabel = a.TrustLabel,
            Notes = a.Notes,
            GroupName = a.GroupName,
            Review = a.Review,
            Hwid = a.Hwid,
            IsMarkedBanned = a.IsMarkedBanned,
            BanReason = a.BanReason,
            BannedAt = a.BannedAt,
            FailStreak = a.FailStreak,
            Proxy = a.Proxy,
            MarketApiKey = a.MarketApiKey,
            IsWarehouse = a.IsWarehouse,
            OwnTradeUrl = a.OwnTradeUrl,
            MaFilePath = a.MaFilePath
        }).ToList();
        File.WriteAllText(_path, JsonSerializer.Serialize(list, JsonOpts));
    }

    public ImportResult Import(string loginsPath, string? maFilesDir)
    {
        if (!File.Exists(loginsPath))
            throw new FileNotFoundException("Login file was not found", loginsPath);

        var pairs = ParseLogins(loginsPath);
        if (pairs.Count == 0)
            throw new InvalidOperationException("No login:password pairs were found");

        var maMap = FindMaFiles(maFilesDir);
        var imported = 0;
        var updated = 0;
        var withoutMa = 0;
        var hwid = new HwidService();

        foreach (var (login, password) in pairs)
        {
            var existing = Accounts.FirstOrDefault(a =>
                a.Login.Equals(login, StringComparison.OrdinalIgnoreCase));

            if (!maMap.TryGetValue(login.ToLowerInvariant(), out var ma) && existing != null && !string.IsNullOrEmpty(existing.SteamId64))
            {
                maMap.TryGetValue(existing.SteamId64.ToLowerInvariant(), out ma);
            }

            if (existing != null)
            {
                existing.Password = password;
                if (ma != null)
                {
                    existing.SharedSecret = ma.SharedSecret;
                    existing.IdentitySecret = ma.IdentitySecret;
                    existing.DeviceId = ma.DeviceId;
                    existing.HasMaFile = true;
                    existing.MaFilePath = ma.Path;
                    if (string.IsNullOrEmpty(existing.SteamId64) && ma.Session?.SteamId > 0)
                        existing.SteamId64 = ma.Session.SteamId.ToString();
                }
                // Permanent HWID: only create if missing — never rotate on re-import
                if (existing.Hwid == null)
                {
                    existing.Hwid = hwid.GenerateProfile();
                    existing.Hwid.Enabled = true;
                }
                updated++;
            }
            else
            {
                var profile = hwid.GenerateProfile();
                profile.Enabled = true;
                var acc = new SteamAccount
                {
                    Login = login,
                    Password = password,
                    SharedSecret = ma?.SharedSecret,
                    IdentitySecret = ma?.IdentitySecret,
                    DeviceId = ma?.DeviceId,
                    SteamId64 = ma?.Session?.SteamId > 0 ? ma.Session.SteamId.ToString() : ma?.SteamId,
                    HasMaFile = ma != null,
                    MaFilePath = ma?.Path,
                    Hwid = profile,
                    Review = new AccountReview()
                };
                if (!acc.HasMaFile) withoutMa++;
                Accounts.Add(acc);
                imported++;
            }
        }

        Save();
        return new ImportResult(imported, updated, withoutMa, maMap.Count, Accounts.Count);
    }

    private static List<(string login, string password)> ParseLogins(string path)
    {
        var result = new List<(string, string)>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            var m = Regex.Match(line, @"^([^:;\t\s]+)[:;\t](.+)$");
            if (!m.Success) continue;
            result.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        }
        return result;
    }

    private static Dictionary<string, MaFile> FindMaFiles(string? dir)
    {
        var map = new Dictionary<string, MaFile>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return map;

        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".mafile", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var text = File.ReadAllText(file);
                var ma = MaFile.Parse(text);
                if (ma == null || string.IsNullOrEmpty(ma.SharedSecret)) continue;
                ma.Path = file;

                var names = new List<string?>
                {
                    ma.AccountName,
                    ma.Session?.SteamLogin,
                    Path.GetFileNameWithoutExtension(file),
                    ma.SteamId,
                    ma.Session?.SteamId > 0 ? ma.Session.SteamId.ToString() : null
                };

                foreach (var name in names)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var key = name.Trim().ToLowerInvariant();
                        map[key] = ma;
                    }
                }
            }
            catch
            {
                // skip corrupt mafile
            }
        }
        return map;
    }

    private sealed class StoredAccount
    {
        public string Id { get; set; } = "";
        public string Login { get; set; } = "";
        public string Password { get; set; } = "";
        public string? SharedSecret { get; set; }
        public string? IdentitySecret { get; set; }
        public string? DeviceId { get; set; }
        public string? SteamId64 { get; set; }
        public string? PersonaName { get; set; }
        public string? AvatarUrl { get; set; }
        public int InventoryCount { get; set; }
        public decimal InventoryValue { get; set; }
        public bool InventoryScanned { get; set; }
        public string? TrustLabel { get; set; }
        public string? Notes { get; set; }
        public string? GroupName { get; set; }
        public AccountReview? Review { get; set; }
        public HwidProfile? Hwid { get; set; }
        public bool IsMarkedBanned { get; set; }
        public string? BanReason { get; set; }
        public DateTime? BannedAt { get; set; }
        public int FailStreak { get; set; }
        public string? Proxy { get; set; }
        public string? MarketApiKey { get; set; }
        public bool IsWarehouse { get; set; }
        public string? OwnTradeUrl { get; set; }
        public string? MaFilePath { get; set; }
    }
}

// helper used by ViewModel
public static class AccountStoreExtensions
{
    public static void RemoveWhere(this System.Collections.ObjectModel.ObservableCollection<SteamAccount> col, Func<SteamAccount, bool> pred)
    {
        for (var i = col.Count - 1; i >= 0; i--)
            if (pred(col[i])) col.RemoveAt(i);
    }
}

public readonly record struct ImportResult(
    int Imported,
    int Updated,
    int WithoutMa,
    int MaFilesFound,
    int Total);
