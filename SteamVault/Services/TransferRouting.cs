using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Pure routing helpers for multi-warehouse / group transfers — easy to unit-test.
/// </summary>
public static class TransferRouting
{
    /// <summary>
    /// Resolve where an account should send offers.
    /// Priority: global warehouse → group warehouse / group trade URL → default/main sink → pasted URL.
    /// </summary>
    public static string ResolveRoute(
        SteamAccount account,
        SteamAccount? globalWarehouse,
        IEnumerable<AccountGroup> groups,
        IEnumerable<SteamAccount> allAccounts,
        string? tradeUrl,
        string? defaultTradeUrl,
        bool mainSinkMode,
        bool routeTradesByGroup)
    {
        if (globalWarehouse != null)
            return globalWarehouse.OwnTradeUrl ?? "";

        var route = mainSinkMode && !string.IsNullOrWhiteSpace(defaultTradeUrl)
            ? defaultTradeUrl
            : tradeUrl;

        if (routeTradesByGroup && !string.IsNullOrWhiteSpace(account.GroupName))
        {
            var group = groups.FirstOrDefault(g =>
                g.Name.Equals(account.GroupName, StringComparison.OrdinalIgnoreCase));
            var groupWarehouse = !string.IsNullOrWhiteSpace(group?.WarehouseAccountId)
                ? allAccounts.FirstOrDefault(a => a.Id == group!.WarehouseAccountId)
                : null;
            if (groupWarehouse?.HasOwnTradeUrl == true)
                route = groupWarehouse.OwnTradeUrl;
            else if (!string.IsNullOrWhiteSpace(group?.TradeUrl))
                route = group.TradeUrl;
        }

        return route ?? "";
    }
}
