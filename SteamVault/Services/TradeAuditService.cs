using System.Collections.ObjectModel;
using System.Text.Json;
using SteamVault.Models;

namespace SteamVault.Services;

public sealed class TradeAuditService
{
    private readonly string _path;
    public ObservableCollection<AuditEntry> Entries { get; } = new();

    public TradeAuditService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamVault");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "audit.json");
        Load();
    }

    public void Add(string kind, string account, string detail, string? offerId = null, decimal? value = null)
    {
        var e = new AuditEntry
        {
            Kind = kind,
            Account = account,
            Detail = detail,
            OfferId = offerId,
            ValueUsd = value
        };
        Entries.Insert(0, e);
        while (Entries.Count > 500) Entries.RemoveAt(Entries.Count - 1);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(_path)) ?? [];
            foreach (var e in list.Take(200)) Entries.Add(e);
        }
        catch { /* */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Entries.Take(300).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* */ }
    }

    public string ExportCsv()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("time,kind,account,detail,offerId,valueUsd");
        foreach (var e in Entries)
        {
            sb.AppendLine($"\"{e.Time:o}\",\"{e.Kind}\",\"{e.Account}\",\"{Escape(e.Detail)}\",\"{e.OfferId}\",{e.ValueUsd}");
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\"", "\"\"");
}
