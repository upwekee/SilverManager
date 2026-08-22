using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Services;

public partial class AppSettings : ObservableObject
{
    private static readonly string PathFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteamVault", "settings.json");

    /// <summary>UI language selected on the first launch: "en" or "ru".</summary>
    [ObservableProperty] private string _language = "en";
    [ObservableProperty] private bool _hasSelectedLanguage;
    [ObservableProperty] private string _steamWebApiKey = "";
    [ObservableProperty] private bool _backgroundRefreshEnabled = true;
    [ObservableProperty] private int _backgroundRefreshMinutes = 30;
    /// <summary>If true, run a full ban review on launch. Default off — user starts on Overview.</summary>
    [ObservableProperty] private bool _refreshOnStartup;
    [ObservableProperty] private bool _notifyOnBanChange = true;
    /// <summary>Default true: apply permanent per-account HWID before login/trade/launch.</summary>
    [ObservableProperty] private bool _alwaysSpoofHwid = true;
    [ObservableProperty] private bool _includeCs2GcpdInReview = true;
    [ObservableProperty] private int _guardCodeRefreshSeconds = 1;

    // Smart transfer rules
    [ObservableProperty] private decimal _minPriceToSend;
    [ObservableProperty] private decimal _maxPriceToSend = 999999;
    [ObservableProperty] private bool _excludeSouvenirs = true;
    [ObservableProperty] private bool _excludeStatTrak;
    [ObservableProperty] private int _maxItemsPerOffer = 50;
    [ObservableProperty] private bool _safeModeDryRun;
    [ObservableProperty] private decimal _sessionValueLimit = 5000;
    [ObservableProperty] private string _trustedPartnerSteam64 = "";
    [ObservableProperty] private bool _autoAcceptEmptyIncoming;
    [ObservableProperty] private bool _autoConfirmMarket = true;
    [ObservableProperty] private bool _autoConfirmTrustedTrades = true;
    [ObservableProperty] private bool _autoConfirmAllTrades;
    [ObservableProperty] private int _autoConfirmIntervalSeconds = 45;
    [ObservableProperty] private string _webhookUrl = "";
    [ObservableProperty] private string _defaultTradeUrl = "";
    /// <summary>When enabled, transfers route each selected account through its group's warehouse / trade URL.</summary>
    [ObservableProperty] private bool _routeTradesByGroup = true;
    [ObservableProperty] private string _accountSearch = "";
    [ObservableProperty] private bool _showAdvancedByDefault;
    /// <summary>Inventory layout: true = card grid, false = virtualized list (better for 1000+ items).</summary>
    [ObservableProperty] private bool _inventoryLayoutGrid;
    /// <summary>Accounts page: groups/selection panel expanded.</summary>
    [ObservableProperty] private bool _accountGroupsPanelExpanded = true;

    /// <summary>
    /// Off by default: items Steam will never let move are excluded from the inventory grid
    /// and from every portfolio total, because counting them inflates a number the user
    /// cannot act on. Items on a timed hold still count — they unlock on their own.
    /// </summary>
    [ObservableProperty] private bool _countNonTradable;

    /// <summary>Route every transfer to the account flagged as the warehouse.</summary>
    [ObservableProperty] private bool _sendToWarehouse;

    // Main sink
    [ObservableProperty] private bool _mainSinkMode;
    [ObservableProperty] private decimal _mainSinkMinPrice = 0.03m;

    // Global default proxy (fallback if account.Proxy empty)
    [ObservableProperty] private string _defaultProxy = "";
    [ObservableProperty] private string _savedProxyBulkText = "";

    // Auto-sell junk
    [ObservableProperty] private decimal _junkMaxPriceUsd = 0.10m;
    [ObservableProperty] private int _junkMaxListingsPerAccount = 25;
    [ObservableProperty] private bool _autoSellAfterInventory;

    // SFX (Botanica V4)
    [ObservableProperty] private bool _soundEnabled = true;
    /// <summary>0..100 UI percent → player volume</summary>
    [ObservableProperty] private int _soundVolumePercent = 22;

    // Inventory cache / drain
    [ObservableProperty] private int _inventoryCacheHours = 12;
    [ObservableProperty] private bool _preferInventoryCache = true;
    [ObservableProperty] private bool _skipTradeHoldItems = true;
    [ObservableProperty] private bool _autoReplaceDeadProxy = true;
    [ObservableProperty] private bool _webhookOnlyFailsAndSummary = true;
    /// <summary>Base pause between trade ops. Higher = safer vs "too many requests".</summary>
    [ObservableProperty] private int _baseTradeDelayMs = 2500;
    /// <summary>Extra pause between accounts in transfer queue (ms).</summary>
    [ObservableProperty] private int _betweenAccountsDelayMs = 3500;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var json = File.ReadAllText(PathFile);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { /* defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
    }
}
