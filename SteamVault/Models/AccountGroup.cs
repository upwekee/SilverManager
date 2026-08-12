using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

/// <summary>Named account cohort with its own recipient trade link.</summary>
public partial class AccountGroup : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _tradeUrl = "";
    /// <summary>Optional named warehouse account for this group. Its verified trade URL wins over TradeUrl.</summary>
    [ObservableProperty] private string? _warehouseAccountId;
    [ObservableProperty] private string _proxyAssignmentMode = "Balanced";
    /// <summary>Optional group-level proxy. Used by members without a personal proxy.</summary>
    [ObservableProperty] private string _proxy = "";
    /// <summary>Optional newline-delimited proxy pool for group round-robin assignment.</summary>
    [ObservableProperty] private string _proxyPool = "";
    [ObservableProperty] private string? _color = DotRamp[0];
    [ObservableProperty] private DateTime _createdAt = DateTime.Now;

    /// <summary>
    /// Group dot colours. Monochrome by design: groups are distinguished by luminance
    /// step, not hue, and the range stops well short of the panel so every dot stays
    /// legible. Cycles when there are more groups than steps.
    /// </summary>
    public static readonly string[] DotRamp =
    [
        "#FFFFFF", "#D8D8DE", "#B4B4BC", "#9A9AA2", "#82828B", "#6C6C75"
    ];

    /// <summary>Ramp entry for the n-th group.</summary>
    public static string DotColor(int index) => DotRamp[Math.Abs(index) % DotRamp.Length];

    /// <summary>
    /// True when a hex is neutral (R≈G≈B). Groups saved by pre-monochrome builds carry
    /// random hues; those are migrated back onto the ramp on load rather than leaking a
    /// lone coloured dot into an otherwise hueless UI.
    /// </summary>
    public static bool IsMonochrome(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];      // strip alpha
        if (h.Length != 6) return false;
        try
        {
            var r = Convert.ToInt32(h[..2], 16);
            var g = Convert.ToInt32(h.Substring(2, 2), 16);
            var b = Convert.ToInt32(h.Substring(4, 2), 16);
            var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            return spread <= 14;
        }
        catch { return false; }
    }

    // Calculated in the view model; not persisted separately.
    [ObservableProperty] private int _accountCount;
    [ObservableProperty] private decimal _portfolioUsd;

    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private int _inventoryLoadedCount;
    [ObservableProperty] private int _itemCount;
    [ObservableProperty] private int _caseCount;
    [ObservableProperty] private int _skinCount;
    [ObservableProperty] private int _proxyAssignedCount;
    [ObservableProperty] private int _uniqueProxyCount;
    /// <summary>Resolved warehouse login for UI (not persisted separately).</summary>
    [ObservableProperty] private string? _warehouseLogin;
    /// <summary>True when the group warehouse has a verified trade URL.</summary>
    [ObservableProperty] private bool _warehouseHasTradeUrl;
    /// <summary>Picker selection for the warehouse ComboBox (UI only).</summary>
    [ObservableProperty] private GroupWarehouseOption? _selectedWarehouseOption;

    /// <summary>Member accounts offered as warehouse destinations (rebuilt by ViewModel).</summary>
    public ObservableCollection<GroupWarehouseOption> WarehouseOptions { get; } = new();

    /// <summary>True while this group is the active selection — drives the chip's .active class.</summary>
    [ObservableProperty] private bool _isSelectedGroup;
    /// <summary>True while this group is the target of the proxy panel.</summary>
    [ObservableProperty] private bool _isProxyTarget;
    /// <summary>UI: expanded card shows members, warehouse, trade link. New groups start collapsed.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Short member login list for the collapsed/expanded preview (not persisted).</summary>
    [ObservableProperty] private string _membersPreview = "";

    public string ExpandToggleLabel => IsExpanded
        ? (Services.LocalizationService.Current?.T("Collapse", "Свернуть") ?? "Collapse")
        : (Services.LocalizationService.Current?.T("Expand", "Развернуть") ?? "Expand");

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandToggleLabel));

    /// <summary>Call after language switch so Expand/Collapse text updates.</summary>
    public void NotifyLocalizedLabels() => OnPropertyChanged(nameof(ExpandToggleLabel));

    public string ProxySummary => HasProxyPool
        ? $"pool · {ProxyPool.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} proxies"
        : HasGroupProxy ? "single proxy" : "no proxy";

    public string AccountCountText => $"{AccountCount} account{(AccountCount == 1 ? "" : "s")}";
    public string PortfolioText => $"${PortfolioUsd:0.00}";
    public string DestinationState
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WarehouseAccountId))
            {
                var name = string.IsNullOrWhiteSpace(WarehouseLogin) ? "account" : WarehouseLogin;
                return WarehouseHasTradeUrl
                    ? $"WAREHOUSE → {name}"
                    : $"WAREHOUSE → {name} (need trade link)";
            }
            return string.IsNullOrWhiteSpace(TradeUrl) ? "DESTINATION MISSING" : "DESTINATION READY";
        }
    }
    public string ProxyPolicyText => ProxyAssignmentMode == "Fixed" ? "FIXED" : "BALANCED POOL";
    public string HealthText => AttentionCount > 0 ? $"{AttentionCount} need attention" : ReadyCount > 0 ? $"{ReadyCount} ready" : "Needs inventory";
    public string InventorySummary => $"{ItemCount} items · {CaseCount} cases · {SkinCount} skins";
    public string ProxyCoverage => AccountCount == 0 ? "no accounts" : ProxyAssignedCount == 0 ? "no proxies" : $"{ProxyAssignedCount}/{AccountCount} assigned · {UniqueProxyCount} unique";
    public string ProxyRecommendation
    {
        get
        {
            if (AccountCount <= 5) return "Optional for small groups";
            if (ProxyAssignedCount < AccountCount) return $"Add proxies: {AccountCount - ProxyAssignedCount} accounts uncovered";
            var ratio = UniqueProxyCount == 0 ? AccountCount : (double)AccountCount / UniqueProxyCount;
            if (ratio > 10) return $"Consider more proxies: {ratio:0.#} accounts / proxy";
            return "Proxy coverage looks balanced";
        }
    }

    partial void OnReadyCountChanged(int value) => OnPropertyChanged(nameof(HealthText));
    partial void OnAttentionCountChanged(int value) => OnPropertyChanged(nameof(HealthText));
    partial void OnTradeUrlChanged(string value) => OnPropertyChanged(nameof(DestinationState));
    partial void OnWarehouseAccountIdChanged(string? value) => OnPropertyChanged(nameof(DestinationState));
    partial void OnWarehouseLoginChanged(string? value) => OnPropertyChanged(nameof(DestinationState));
    partial void OnWarehouseHasTradeUrlChanged(bool value) => OnPropertyChanged(nameof(DestinationState));
    partial void OnProxyAssignmentModeChanged(string value) => OnPropertyChanged(nameof(ProxyPolicyText));
    public bool HasGroupProxy => !string.IsNullOrWhiteSpace(Proxy);
    public bool HasProxyPool => !string.IsNullOrWhiteSpace(ProxyPool);
    public string ProxyModeText => HasProxyPool ? "POOL" : HasGroupProxy ? "GROUP" : "PERSONAL";

    partial void OnProxyChanged(string value)
    {
        OnPropertyChanged(nameof(HasGroupProxy));
        OnPropertyChanged(nameof(ProxyModeText));
        OnPropertyChanged(nameof(ProxySummary));
    }
    partial void OnProxyPoolChanged(string value)
    {
        OnPropertyChanged(nameof(HasProxyPool));
        OnPropertyChanged(nameof(ProxyModeText));
        OnPropertyChanged(nameof(ProxySummary));
    }

    partial void OnPortfolioUsdChanged(decimal value) => OnPropertyChanged(nameof(PortfolioText));
    partial void OnItemCountChanged(int value) => OnPropertyChanged(nameof(InventorySummary));
    partial void OnCaseCountChanged(int value) => OnPropertyChanged(nameof(InventorySummary));
    partial void OnSkinCountChanged(int value) => OnPropertyChanged(nameof(InventorySummary));
    partial void OnProxyAssignedCountChanged(int value) { OnPropertyChanged(nameof(ProxyCoverage)); OnPropertyChanged(nameof(ProxyRecommendation)); }
    partial void OnUniqueProxyCountChanged(int value) { OnPropertyChanged(nameof(ProxyCoverage)); OnPropertyChanged(nameof(ProxyRecommendation)); }
    partial void OnAccountCountChanged(int value) { OnPropertyChanged(nameof(AccountCountText)); OnPropertyChanged(nameof(ProxyCoverage)); OnPropertyChanged(nameof(ProxyRecommendation)); }
}
