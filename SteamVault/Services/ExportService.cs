using System.Text;
using SteamVault.Models;

namespace SteamVault.Services;

public static class ExportService
{
    public static string InventoryCsv(IEnumerable<InventoryItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("account,assetId,marketHashName,exterior,rarity,tradable,price,imageUrl");
        foreach (var i in items)
        {
            sb.AppendLine(
                $"\"{Esc(i.AccountLogin)}\",\"{i.AssetId}\",\"{Esc(i.MarketHashName)}\",\"{Esc(i.Exterior)}\",\"{Esc(i.Rarity)}\",{i.Tradable},{i.Price:0.00},\"{i.ImageUrl}\"");
        }
        return sb.ToString();
    }

    public static string AccountsCsv(IEnumerable<SteamAccount> accounts, bool includeSecrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine(includeSecrets
            ? "login,password,steamId64,persona,invCount,invValue,badge,sharedSecret,proxy"
            : "login,steamId64,persona,invCount,invValue,badge,hasMaFile,proxy");
        foreach (var a in accounts)
        {
            if (includeSecrets)
                sb.AppendLine($"\"{Esc(a.Login)}\",\"{Esc(a.Password)}\",\"{a.SteamId64}\",\"{Esc(a.PersonaName ?? "")}\",{a.InventoryCount},{a.InventoryValue:0.00},\"{Esc(a.ReviewBadge)}\",\"{Esc(a.SharedSecret ?? "")}\",\"{Esc(a.Proxy ?? "")}\"");
            else
                sb.AppendLine($"\"{Esc(a.Login)}\",\"{a.SteamId64}\",\"{Esc(a.PersonaName ?? "")}\",{a.InventoryCount},{a.InventoryValue:0.00},\"{Esc(a.ReviewBadge)}\",{a.HasMaFile},\"{Esc(a.Proxy ?? "")}\"");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Clean list: login:password only for non-banned / non-blocked accounts.
    /// </summary>
    public static string CleanLoginPassList(IEnumerable<SteamAccount> accounts, bool includeMaHint = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SteamVault clean list — no VAC/blocked");
        sb.AppendLine($"# exported {DateTime.Now:yyyy-MM-dd HH:mm}");
        foreach (var a in accounts.Where(a => !a.IsBlocked && !a.HasVac && !a.IsMarkedBanned))
        {
            if (string.IsNullOrEmpty(a.Login) || string.IsNullOrEmpty(a.Password)) continue;
            sb.Append(a.Login).Append(':').Append(a.Password);
            if (includeMaHint && a.HasMaFile) sb.Append(" # ma");
            if (!string.IsNullOrEmpty(a.Proxy)) sb.Append(" # proxy=").Append(a.Proxy);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string CleanLoginPassProxyCsv(IEnumerable<SteamAccount> accounts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("login,password,proxy,steamId64");
        foreach (var a in accounts.Where(a => !a.IsBlocked && !a.HasVac && !a.IsMarkedBanned))
        {
            if (string.IsNullOrEmpty(a.Login)) continue;
            sb.AppendLine($"\"{Esc(a.Login)}\",\"{Esc(a.Password)}\",\"{Esc(a.Proxy ?? "")}\",\"{a.SteamId64}\"");
        }
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\"", "\"\"");
}
