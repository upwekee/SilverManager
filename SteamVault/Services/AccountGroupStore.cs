using System.Collections.ObjectModel;
using System.Text.Json;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>Persists named account groups separately from secrets in accounts.json.</summary>
public sealed class AccountGroupStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public ObservableCollection<AccountGroup> Groups { get; } = new();

    public AccountGroupStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamVault");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "account-groups.json");
        Load();
    }

    public void Load()
    {
        Groups.Clear();
        try
        {
            if (!File.Exists(_path)) return;
            var groups = JsonSerializer.Deserialize<List<AccountGroup>>(File.ReadAllText(_path), Options) ?? [];
            var migrated = false;
            var index = 0;
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
            {
                // Groups written by pre-monochrome builds carry a random hue. Deserialization
                // overwrites the model default, so the stale colour would survive forever —
                // put it back on the ramp here, once, and persist the correction.
                if (!AccountGroup.IsMonochrome(group.Color))
                {
                    group.Color = AccountGroup.DotColor(index);
                    migrated = true;
                }
                Groups.Add(group);
                index++;
            }
            if (migrated) Save();
        }
        catch { /* retain an empty usable store */ }
    }

    public void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Groups, Options)); }
        catch { /* settings persistence must never block trading */ }
    }
}
