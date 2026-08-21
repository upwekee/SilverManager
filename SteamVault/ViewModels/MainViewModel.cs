using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamVault.Models;
using SteamVault.Services;

namespace SteamVault.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly AccountStore _store = new();
    private readonly AccountGroupStore _groupStore = new();
    /// <summary>Last saved group name by group Id — used to re-stamp membership on rename.</summary>
    private readonly Dictionary<string, string> _groupNameById = new();
    private readonly SessionManager _sessions = new();
    private readonly PriceService _prices = new();
    private readonly AccountReviewService _reviewSvc = new();
    private readonly HwidService _hwidSvc = new();
    private readonly TradeAuditService _audit = new();
    private readonly WebhookService _webhooks = new();
    private readonly StatsService _stats = new();
    private readonly BackgroundRefreshService _bg;
    private readonly ConfirmationAutoService _autoConf;
    private readonly SoundService _sfx = new();
    private readonly InventoryCacheService _invCache = new();
    private readonly JobController _job = new();
    private readonly DispatcherTimer _guardTimer;
    private readonly DispatcherTimer _statsTimer;
    private decimal _sessionSentValue;
    /// <summary>Dead proxies removed after check — pool for auto-replace.</summary>
    private readonly List<string> _proxyPool = new();
    /// <summary>While true, per-account PropertyChanged must not thrash the UI (scan freeze root cause).</summary>
    private bool _bulkUi;

    public AppSettings Settings { get; }
    public LocalizationService L { get; }
    public UiStrings Ui { get; private set; }
    public bool IsRussian => L.IsRussian;
    public string T(string english, string russian) => L.T(english, russian);
    public bool IsInventoryGrid => Settings.InventoryLayoutGrid;
    public bool IsInventoryList => !Settings.InventoryLayoutGrid;
    public bool ShowInventoryGrid => IsInventoryGrid && FilteredItems.Count > 0;
    public bool ShowInventoryList => IsInventoryList && FilteredItems.Count > 0;
    public bool AccountGroupsPanelExpanded
    {
        get => Settings.AccountGroupsPanelExpanded;
        set
        {
            if (Settings.AccountGroupsPanelExpanded == value) return;
            Settings.AccountGroupsPanelExpanded = value;
            Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccountGroupsPanelToggleLabel));
        }
    }
    public string AccountGroupsPanelToggleLabel => AccountGroupsPanelExpanded ? Ui.LabelCollapse : Ui.LabelExpand;
    public ObservableCollection<SteamAccount> Accounts => _store.Accounts;
    public ObservableCollection<AccountGroup> AccountGroups => _groupStore.Groups;
    /// <summary>Filtered/virtualized account list for left panel (1000+ safe).</summary>
    public ObservableCollection<SteamAccount> FilteredAccounts { get; } = new();
    public ObservableCollection<InventoryItem> Items { get; } = new();
    public ObservableCollection<InventoryItem> FilteredItems { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<HwidCompareRow> HwidCompareRows { get; } = new();
    public ObservableCollection<ConfirmationItem> Confirmations { get; } = new();
    public ObservableCollection<TradeOfferItem> TradeOffers { get; } = new();
    public ObservableCollection<AuditEntry> AuditEntries => _audit.Entries;
    public ObservableCollection<TimelineEntry> Timeline { get; } = new();
    public ObservableCollection<PortfolioItemRow> CaseStats { get; } = new();
    public ObservableCollection<PortfolioItemRow> TopSkinStats { get; } = new();
    /// <summary>Compact top skins for Home dashboard cards (first 8).</summary>
    public ObservableCollection<PortfolioItemRow> HomeTopSkins { get; } = new();
    public ObservableCollection<PortfolioItemRow> TopAggregateStats { get; } = new();
    public ObservableCollection<PortfolioCategoryRow> CategoryStats { get; } = new();
    public ObservableCollection<QualityStatRow> QualityStats { get; } = new();

    [ObservableProperty] private string _searchQuery = "";
    // Off by default: a fresh weekly drop carries a 7-day hold, so hiding held items
    // makes a successful scan look like it found nothing. Transfer paths re-check Tradable.
    [ObservableProperty] private bool _tradableOnly;
    [ObservableProperty] private string _tradeUrl = "";
    [ObservableProperty] private string? _tradePartner;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showImport;
    [ObservableProperty] private string _loginsPath = "";
    [ObservableProperty] private string _maDir = "";
    [ObservableProperty] private string _priceStatus = "prices: —";
    [ObservableProperty] private int _selectedItemCount;
    [ObservableProperty] private int _selectedAccountCount;
    [ObservableProperty] private decimal _selectedValue;
    [ObservableProperty] private decimal _visibleValue;
    [ObservableProperty] private decimal _totalPortfolio;
    [ObservableProperty] private int _onlineCount;
    [ObservableProperty] private int _banCount;
    [ObservableProperty] private int _shellPage; // ShellPage cast
    [ObservableProperty] private string _statusBanner = "SteamVault";
    [ObservableProperty] private double _activityProgress;
    [ObservableProperty] private bool _activityIndeterminate = true;
    [ObservableProperty] private SteamAccount? _focusedAccount;
    [ObservableProperty] private string _bgStatus = "bg: off";
    [ObservableProperty] private string _hwidAdminHint = "";
    /// <summary>True when process runs elevated — Device IDs registry spoof works fully.</summary>
    public bool IsRunningAsAdmin => _hwidSvc.IsAdmin();
    public bool ShowHwidAdminWarning => !IsRunningAsAdmin;
    [ObservableProperty] private HwidProfile? _realHwid;
    [ObservableProperty] private string _pipelinePreview = "";
    [ObservableProperty] private string _accountFilter = "";
    [ObservableProperty] private string _accountQuickFilter = "All";
    [ObservableProperty] private AccountGroup? _selectedAccountGroup;
    [ObservableProperty] private string _transferReview = "Select accounts and inventory items to prepare a transfer review.";
    [ObservableProperty] private bool _showTransferReview;
    [ObservableProperty] private TransferPlan? _pendingTransferPlan;
    [ObservableProperty] private string _transferPlanFilter = "All";
    [ObservableProperty] private string _transferPlanSearch = "";
    [ObservableProperty] private bool _transferPlanApproved;
    public ObservableCollection<TransferPlanAccount> FilteredTransferPlanAccounts { get; } = new();
    public string TransferPlanSummary => PendingTransferPlan?.Summary ?? "Build a transfer plan to validate destinations before sending.";
    public string TransferPlanIssueSummary => PendingTransferPlan == null
        ? ""
        : PendingTransferPlan.Issues.Count == 0
            ? "All included accounts have a valid destination."
            : string.Join(" · ", PendingTransferPlan.Issues.Take(3).Select(x => $"{x.AccountCount} {x.Message}"));
    public bool HasPendingTransferPlan => PendingTransferPlan != null;
    public bool CanSendTransferPlan => PendingTransferPlan is { HasBlockingIssues: false, OfferCount: > 0 } && TransferPlanApproved;
    public bool WarehouseIsConfigured
    {
        get
        {
            var warehouse = WarehouseAccount;
            if (warehouse is not { HasOwnTradeUrl: true }) return false;
            try
            {
                var parsed = SteamSession.ParseTradeUrl(warehouse.OwnTradeUrl!);
                return string.IsNullOrWhiteSpace(warehouse.SteamId64) || parsed.PartnerSteam64 == warehouse.SteamId64;
            }
            catch { return false; }
        }
    }
    public string WarehouseGuardMessage => WarehouseAccount switch
    {
        null => "Choose an account as warehouse, then add its own trade link.",
        { HasOwnTradeUrl: false } => "Warehouse needs its own trade link before it can receive items.",
        _ when !WarehouseIsConfigured => "Warehouse link is invalid or does not match the warehouse SteamID.",
        _ => $"Ready: {WarehouseAccount.Login} receives transfers."
    };

    // ---- Destructive-action confirmation ----
    [ObservableProperty] private bool _showConfirm;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmMessage = "";
    [ObservableProperty] private string _confirmActionText = "Confirm";
    private Action? _confirmAction;
    private bool _drainConfirmed;

    /// <summary>Queues a destructive action behind an explicit confirmation step.</summary>
    private void AskConfirm(string title, string message, string actionText, Action action)
    {
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmActionText = actionText;
        _confirmAction = action;
        ShowConfirm = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        ShowConfirm = false;
        _confirmAction = null;
    }

    [RelayCommand]
    private void AcceptConfirm()
    {
        var action = _confirmAction;
        ShowConfirm = false;
        _confirmAction = null;
        action?.Invoke();
    }

    // ---- Account detail panel ----
    [ObservableProperty] private bool _showAccountPanel;
    [ObservableProperty] private string _editLogin = "";
    [ObservableProperty] private string _editPassword = "";
    [ObservableProperty] private string _editMaFilePath = "";
    [ObservableProperty] private string _editWarehouseTradeUrl = "";
    [ObservableProperty] private bool _editIsWarehouse;
    [ObservableProperty] private string _accountEditStatus = "";

    [RelayCommand]
    private void OpenAccountPanel(SteamAccount? acc)
    {
        acc ??= FocusedAccount;
        if (acc == null) return;
        FocusedAccount = acc;
        EditLogin = acc.Login;
        EditPassword = acc.Password;
        EditMaFilePath = acc.MaFilePath ?? "";
        EditWarehouseTradeUrl = acc.OwnTradeUrl ?? "";
        EditIsWarehouse = acc.IsWarehouse;
        AccountEditStatus = acc.HasMaFile
            ? $"maFile linked{(string.IsNullOrWhiteSpace(acc.MaFilePath) ? "" : ": " + acc.MaFilePath)}"
            : "No maFile linked";
        RebuildHwidCompare(acc);
        ShowAccountPanel = true;
    }

    [RelayCommand]
    private void CloseAccountPanel() => ShowAccountPanel = false;

    /// <summary>Sign in the account being edited and fill the trade-link field automatically.</summary>
    [RelayCommand]
    private async Task FetchEditAccountTradeLinkAsync()
    {
        var acc = FocusedAccount;
        if (acc == null) { AccountEditStatus = "No account open"; return; }
        if (!acc.HasMaFile) { AccountEditStatus = "Link a maFile first — needed to sign in and read the trade URL"; return; }
        if (acc.IsBlocked) { AccountEditStatus = "Blocked accounts cannot fetch a trade link"; return; }

        try
        {
            SetBusy($"{acc.Login}: trade link…");
            AccountEditStatus = "Signing in…";
            var url = await FetchTradeUrlForAccountAsync(acc);
            EditWarehouseTradeUrl = url;
            acc.OwnTradeUrl = url;
            // Keep Transfer paste field in sync when this is the active warehouse.
            if (acc.IsWarehouse || Settings.SendToWarehouse)
                TradeUrl = url;
            foreach (var g in AccountGroups.Where(g => g.WarehouseAccountId == acc.Id))
                g.TradeUrl = url;
            _store.Save();
            _groupStore.Save();
            RefreshWarehouseUi();
            RefreshGroupSummaries();
            AccountEditStatus = "Trade link fetched and filled in";
            Log($"{acc.Login}: trade link → editor field", LogLevel.Success);
            _sfx.Play(Sfx.Success);
        }
        catch (Exception ex)
        {
            AccountEditStatus = "Fetch failed: " + ex.Message;
            Log($"{acc.Login}: trade link fetch failed — {ex.Message}", LogLevel.Error);
        }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task BrowseAccountMaFileAsync()
    {
        var path = await FileDialogs.OpenFileAsync("Steam Desktop Authenticator maFile", ("maFile", new[] { "mafile", "json" }));
        if (path != null) EditMaFilePath = path;
    }

    [RelayCommand]
    private void SaveAccountEdits()
    {
        var acc = FocusedAccount;
        if (acc == null) return;
        var login = EditLogin.Trim();
        if (string.IsNullOrWhiteSpace(login)) { AccountEditStatus = "Login is required"; return; }
        if (Accounts.Any(a => a != acc && a.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
        {
            AccountEditStatus = "Another account already uses this login";
            return;
        }

        acc.Login = login;
        acc.Password = EditPassword;
        acc.OwnTradeUrl = string.IsNullOrWhiteSpace(EditWarehouseTradeUrl) ? null : EditWarehouseTradeUrl.Trim();

        if (!string.IsNullOrWhiteSpace(EditMaFilePath))
        {
            try
            {
                var raw = File.ReadAllText(EditMaFilePath);
                var ma = MaFile.Parse(raw);
                if (ma == null || string.IsNullOrWhiteSpace(ma.SharedSecret) || string.IsNullOrWhiteSpace(ma.IdentitySecret))
                    throw new InvalidOperationException("The selected file is not a valid maFile with shared_secret and identity_secret");
                acc.SharedSecret = ma.SharedSecret;
                acc.IdentitySecret = ma.IdentitySecret;
                acc.DeviceId = ma.DeviceId;
                acc.MaFilePath = EditMaFilePath;
                acc.HasMaFile = true;
                if (string.IsNullOrWhiteSpace(acc.SteamId64))
                    acc.SteamId64 = ma.Session?.SteamId > 0 ? ma.Session.SteamId.ToString() : ma.SteamId;
            }
            catch (Exception ex)
            {
                AccountEditStatus = "maFile was not saved: " + ex.Message;
                return;
            }
        }

        if (EditIsWarehouse)
        {
            foreach (var other in Accounts) other.IsWarehouse = other == acc;
        }
        else if (acc.IsWarehouse)
        {
            acc.IsWarehouse = false;
            Settings.SendToWarehouse = false;
        }

        _store.Save();
        RebuildAccountList();
        RefreshWarehouseUi();
        AccountEditStatus = "Saved";
        Log($"{acc.Login}: account settings saved", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    /// <summary>Delete the account open in the settings panel (with confirm).</summary>
    [RelayCommand]
    private void DeleteAccountFromEditor()
    {
        var acc = FocusedAccount;
        if (acc == null) return;
        ConfirmDeleteAccount(acc);
    }

    /// <summary>Context-menu delete for a row (same confirm as editor).</summary>
    [RelayCommand]
    private void DeleteAccountContext(SteamAccount? acc)
    {
        if (acc == null) return;
        ConfirmDeleteAccount(acc);
    }

    private void ConfirmDeleteAccount(SteamAccount acc)
    {
        AskConfirm(
            T("Delete account", "Удалить аккаунт"),
            T(
                $"Remove «{acc.Login}» from SilverManager? maFiles on disk are not deleted.",
                $"Убрать «{acc.Login}» из SilverManager? maFile на диске не удаляется."),
            T("Delete", "Удалить"),
            () =>
            {
                RemoveSelectedAccountsCore([acc]);
                if (FocusedAccount == acc) CloseAccountPanel();
                RebuildAccountList();
                RecalcDashboardLight();
            });
    }

    /// <summary>Human-readable digest of the smart rules so the filter is never invisible.</summary>
    public string SmartRuleSummary =>
        $"${Settings.MinPriceToSend:0.00}–${Settings.MaxPriceToSend:0.00}"
        + (Settings.ExcludeSouvenirs ? " · no Souvenir" : "")
        + (Settings.ExcludeStatTrak ? " · no StatTrak" : "")
        + (Settings.SkipTradeHoldItems ? " · skip hold" : "")
        + $" · max {Settings.MaxItemsPerOffer}/offer · session cap ${Settings.SessionValueLimit:0}";

    public void RefreshSmartRuleSummary() => OnPropertyChanged(nameof(SmartRuleSummary));
    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _newGroupTradeUrl = "";
    [ObservableProperty] private string _newGroupProxy = "";
    [ObservableProperty] private string _healthSummary = "";
    [ObservableProperty] private bool _pageTransition;
    /// <summary>Secondary nav (proxy, review, audit…) collapsed by default — less noise.</summary>
    [ObservableProperty] private bool _advancedNavOpen;
    /// <summary>Stream-safe: never show 2FA on main UI.</summary>
    [ObservableProperty] private bool _streamSafeMode = true;
    [ObservableProperty] private int _statsPeriodIndex; // 0=24h 1=7d 2=30d 3=all
    [ObservableProperty] private string _statsSummary = "";
    [ObservableProperty] private IList<ChartBar>? _portfolioBars;
    [ObservableProperty] private IList<ChartBar>? _tradeBars;
    [ObservableProperty] private decimal _statsPortfolio;
    [ObservableProperty] private int _statsBans;
    [ObservableProperty] private int _statsTradesOk;
    [ObservableProperty] private int _statsTradesFail;
    [ObservableProperty] private int _statsItems;
    [ObservableProperty] private decimal _statsVolume;
    [ObservableProperty] private bool _hideBlockedAccounts;
    [ObservableProperty] private decimal _statsLivePortfolio;
    [ObservableProperty] private decimal _statsWithdrawnLifetime;
    [ObservableProperty] private decimal _statsWithdrawnSession;
    [ObservableProperty] private int _statsWithdrawnItems;
    [ObservableProperty] private int _statsCaseCount;
    [ObservableProperty] private int _statsLiveItems;
    [ObservableProperty] private IList<ChartBar>? _categoryBars;
    [ObservableProperty] private IList<ChartBar>? _volumeBars;
    [ObservableProperty] private decimal _statsPeriodVolume;
    [ObservableProperty] private int _statsPeriodItems;
    [ObservableProperty] private double _statsSuccessRate;
    [ObservableProperty] private decimal _statsPortfolioDelta;
    [ObservableProperty] private string _onlineHelp =
        "Online = an active Steam session. Trades and confirmations require it; private inventories may require login.";
    [ObservableProperty] private string _singleProxyInput = "";
    [ObservableProperty] private string _proxyBulkText = "";
    [ObservableProperty] private string _proxyStatus = "proxy: —";
    [ObservableProperty] private int _proxyAssignedCount;
    [ObservableProperty] private string _focusedProxyEdit = "";
    [ObservableProperty] private string _proxyCheckSummary = "";
    [ObservableProperty] private bool _isCheckingProxies;
    public ObservableCollection<ProxyCheckResult> ProxyCheckResults { get; } = new();
    public ObservableCollection<ProxyUsageRow> ProxyUsageRows { get; } = new();

    // Inventory filters — all off by default so a scan always shows what it found.
    [ObservableProperty] private bool _filterCasesOnly;
    [ObservableProperty] private bool _filterReadyOnly;
    [ObservableProperty] private decimal _filterMinPrice;
    [ObservableProperty] private bool _hideTradeHold;

    /// <summary>0 = price ↓, 1 = price ↑, 2 = name, 3 = newest first (scan order).</summary>
    [ObservableProperty] private int _inventorySortIndex;

    public string InventorySortLabel => InventorySortIndex switch
    {
        0 => T("Price ↓", "Цена ↓"),
        1 => T("Price ↑", "Цена ↑"),
        2 => T("Name", "Имя"),
        _ => T("Default", "По умолч.")
    };

    partial void OnInventorySortIndexChanged(int value)
    {
        OnPropertyChanged(nameof(InventorySortLabel));
        RefreshFilter();
    }

    /// <summary>Cycles the sort order — one button instead of a dropdown that hides its own state.</summary>
    [RelayCommand]
    private void CycleInventorySort() => InventorySortIndex = (InventorySortIndex + 1) % 4;

    /// <summary>Items owned by the selected accounts that the active filters removed.</summary>
    [ObservableProperty] private int _hiddenByFilterCount;

    /// <summary>
    /// Permanently untradable items skipped while <c>CountNonTradable</c> is off. Surfaced as a
    /// hint so the exclusion is visible rather than looking like items went missing.
    /// </summary>
    [ObservableProperty] private int _deadWeightCount;

    public string DeadWeightHint => DeadWeightCount == 0
        ? ""
        : T($"{DeadWeightCount} non-tradable hidden from list & portfolio",
            $"скрыто non-tradable: {DeadWeightCount} (не в списке и не в $)");

    public bool HasDeadWeight => DeadWeightCount > 0;

    partial void OnDeadWeightCountChanged(int value)
    {
        OnPropertyChanged(nameof(DeadWeightHint));
        OnPropertyChanged(nameof(HasDeadWeight));
    }
    [ObservableProperty] private string _inventoryEmptyTitle = "Nothing here yet";
    [ObservableProperty] private string _inventoryEmptyHint = "Scan accounts to load their inventories.";

    // Batch job UI
    [ObservableProperty] private string _queueProgressText = "idle";
    [ObservableProperty] private string _queueEtaText = "—";
    [ObservableProperty] private bool _queueRunning;
    [ObservableProperty] private bool _queuePaused;
    [ObservableProperty] private double _queuePercent;
    [ObservableProperty] private string _apiKeyStatus = "API key: not set";

    public bool IsHome => ShellPage == (int)Models.ShellPage.Home;
    public bool IsInventory => ShellPage == (int)Models.ShellPage.Inventory;
    public bool IsTransfer => ShellPage == (int)Models.ShellPage.Transfer;
    public bool IsConfirmations => ShellPage == (int)Models.ShellPage.Confirmations;
    public bool IsIncoming => ShellPage == (int)Models.ShellPage.Incoming;
    public bool IsReview => ShellPage == (int)Models.ShellPage.Review;
    public bool IsAudit => ShellPage == (int)Models.ShellPage.Audit;
    public bool IsStats => ShellPage == (int)Models.ShellPage.Stats;
    public bool IsProxyPage => ShellPage == (int)Models.ShellPage.Proxy;
    public bool IsGroups => ShellPage == (int)Models.ShellPage.Groups;
    public bool IsAccounts => ShellPage == (int)Models.ShellPage.Accounts;
    public bool IsSettings => ShellPage == (int)Models.ShellPage.Settings;
    public bool IsHwid => ShellPage == (int)Models.ShellPage.Hwid;
    public bool IsAutoFarm => ShellPage == (int)Models.ShellPage.AutoFarm;
    /// <summary>Blocked or missing maFile only — proxy is optional.</summary>
    public int AttentionCount => Accounts.Count(a => a.IsBlocked || !a.HasMaFile || a.ProxyCheckOk == false);
    public int ReadyAccountCount => Accounts.Count(a => a.CanTrade && a.InventoryCount > 0);

    /// <summary>Top accounts by inventory value — shown on Home.</summary>
    public ObservableCollection<SteamAccount> HomeTopAccounts { get; } = new();
    /// <summary>Top item names by quantity for Home donut.</summary>
    public ObservableCollection<PortfolioItemRow> HomeCountStats { get; } = new();

    // ---- Getting-started checklist (step state drives the Home page) ----
    public bool HasAccounts => Accounts.Count > 0;
    public int MaFileCount => Accounts.Count(a => a.HasMaFile);
    public int GroupedAccountCount => Accounts.Count(a => !string.IsNullOrWhiteSpace(a.GroupName));
    public int InventoryLoadedAccounts => Accounts.Count(a => a.InventoryCount > 0);
    public int ScannedAccountCount => Accounts.Count(a => a.InventoryScanned);
    public int UnscannedAccountCount => Math.Max(0, Accounts.Count - ScannedAccountCount);
    public int BanScannedAccounts => Accounts.Count(a => a.Review != null);
    public int MissingMaFileCount => Accounts.Count(a => !a.HasMaFile && !a.IsBlocked);
    public int BlockedAccountCount => Accounts.Count(a => a.IsBlocked);
    public int ProxyFailedCount => Accounts.Count(a => a.ProxyCheckOk == false);
    public decimal AveragePerAccount =>
        Accounts.Count == 0 ? 0 : TotalPortfolio / Accounts.Count;

    public int ReviewCleanCount => Accounts.Count(a => a.Review != null && !a.HasBanFlag && !a.IsBlocked);
    public int ReviewBannedCount => Accounts.Count(a => a.HasBanFlag || a.IsBlocked || a.HasVac);
    public int ReviewUncheckedCount => Accounts.Count(a => a.Review == null);
    public string ReviewSummaryLine =>
        Accounts.Count == 0
            ? "Import accounts to run a ban check"
            : ReviewUncheckedCount == Accounts.Count
                ? "Not checked yet — run Check selected or Check all"
                : $"Clean {ReviewCleanCount} · Banned / blocked {ReviewBannedCount} · Not checked {ReviewUncheckedCount}";
    public decimal BestItemValue => TopSkinStats.Count > 0 ? TopSkinStats[0].UnitPrice : 0;
    public string BestItemName => TopSkinStats.Count > 0 ? TopSkinStats[0].Name : "—";
    public string PortfolioDeltaText
    {
        get
        {
            var d = StatsPortfolioDelta;
            if (d == 0) return "no change vs previous period";
            var sign = d > 0 ? "↑" : "↓";
            return $"{sign} ${Math.Abs(d):0.00} vs previous period";
        }
    }
    public string PortfolioDeltaColor => StatsPortfolioDelta >= 0 ? "#FFFFFF" : "#E0575F";
    /// <summary>Compact delta for the sparkline end badge — the panel header already shows the total.</summary>
    public string StatsPortfolioDeltaText
    {
        get
        {
            var d = StatsPortfolioDelta;
            if (d == 0) return "";
            return $"{(d > 0 ? "+" : "−")}${Math.Abs(d):0.00}";
        }
    }
    /// <summary>Period as the same token the segment buttons pass, so the active pill can match it.</summary>
    public string StatsPeriodKey => StatsPeriodIndex switch
    {
        0 => "24h",
        1 => "7d",
        2 => "30d",
        _ => "all"
    };
    public string HomeHealthLine =>
        Accounts.Count == 0
            ? T("Import accounts → Scan inventory → Transfer skins", "Импорт → Скан инвентаря → Передача")
            : T(
                $"{ScannedAccountCount}/{Accounts.Count} scanned · {ReadyAccountCount} with items · {OnlineCount} online",
                $"{ScannedAccountCount}/{Accounts.Count} скан · {ReadyAccountCount} с предметами · {OnlineCount} online");
    public string HomeItemsLine =>
        StatsLiveItems > 0
            ? T($"{StatsLiveItems} items · {StatsCaseCount} cases", $"{StatsLiveItems} шт. · {StatsCaseCount} кейсов")
            : InventoryLoadedAccounts > 0
                ? T(
                    $"{Accounts.Sum(a => a.InventoryCount)} items across {InventoryLoadedAccounts} accounts",
                    $"{Accounts.Sum(a => a.InventoryCount)} шт. на {InventoryLoadedAccounts} акк.")
                : T("Scan to load skins", "Сканируй, чтобы загрузить скины");
    public string HomeAttentionDetail
    {
        get
        {
            if (AttentionCount == 0) return T("All clear", "Всё чисто");
            var parts = new List<string>();
            if (BlockedAccountCount > 0) parts.Add(T($"{BlockedAccountCount} blocked", $"{BlockedAccountCount} блок."));
            if (MissingMaFileCount > 0) parts.Add(T($"{MissingMaFileCount} no maFile", $"{MissingMaFileCount} без maFile"));
            if (ProxyFailedCount > 0) parts.Add(T($"{ProxyFailedCount} proxy fail", $"{ProxyFailedCount} прокси fail"));
            return parts.Count > 0 ? string.Join(" · ", parts) : T($"{AttentionCount} need check", $"{AttentionCount} нужно проверить");
        }
    }
    public string HomeScanDetail =>
        Accounts.Count == 0
            ? T("Import accounts first", "Сначала импорт аккаунтов")
            : UnscannedAccountCount == 0
                ? T("All accounts scanned", "Все аккаунты просканированы")
                : T($"{UnscannedAccountCount} not scanned yet", $"{UnscannedAccountCount} ещё не сканированы");
    public string HomeDonutCenterTop => StatsLiveItems > 0 ? StatsLiveItems.ToString() : Items.Where(Counts).Sum(i => Math.Max(1, i.Amount)).ToString();
    public string HomeDonutCenterBottom => T("items", "шт.");

    public string Step1State => HasAccounts ? "DONE" : "TODO";
    public string Step1Detail => HasAccounts
        ? $"{Accounts.Count} accounts loaded · {MaFileCount} with maFile"
        : "Import a login:password list and point to your maFiles folder.";

    public string Step2State => ProxyAssignedCount > 0 ? "DONE" : (Accounts.Count >= 10 ? "NEEDED" : "OPTIONAL");
    public string Step2Detail => ProxyAssignedCount > 0
        ? $"{ProxyAssignedCount} of {Accounts.Count} accounts have a proxy"
        : Accounts.Count >= 10
            ? $"Recommended: {Accounts.Count} accounts without enough proxies — rate-limits and bans are more likely."
            : "Optional for small farms. Add one proxy per account, or one proxy / pool per group.";

    /// <summary>Show a home banner when many accounts lack proxies.</summary>
    public bool ShowProxyRecommendation =>
        Accounts.Count >= 10 && ProxyAssignedCount < Math.Max(1, (int)Math.Ceiling(Accounts.Count * 0.5));

    public string ProxyRecommendationBanner =>
        Accounts.Count == 0
            ? ""
            : ProxyAssignedCount == 0
                ? T(
                    $"Add proxies: {Accounts.Count} accounts share one IP — Steam will throttle or block scans/trades.",
                    $"Добавь прокси: {Accounts.Count} акк. с одного IP — Steam будет резать скан/трейды.")
                : T(
                    $"Proxy coverage low: {ProxyAssignedCount}/{Accounts.Count} accounts. Aim for one proxy per account or a balanced group pool.",
                    $"Мало прокси: {ProxyAssignedCount}/{Accounts.Count} акк. Лучше 1 прокси на аккаунт или пул на группу.");

    public int HwidProfileCount => Accounts.Count(a => a.HasHwid);
    public string HwidSummaryLine =>
        Accounts.Count == 0
            ? T("Import accounts first", "Сначала импорт аккаунтов")
            : HwidProfileCount == Accounts.Count
                ? T($"{HwidProfileCount} unique device profiles ready", $"{HwidProfileCount} device-профилей готово")
                : T(
                    $"{HwidProfileCount}/{Accounts.Count} profiles — missing ones are created on first use",
                    $"{HwidProfileCount}/{Accounts.Count} профилей — остальные создадутся при первом входе");

    [ObservableProperty] private string _hwidFilter = "";
    [ObservableProperty] private int _statsFloatCount;
    [ObservableProperty] private string _statsFloatSummary = "No float data yet";
    [ObservableProperty] private decimal _statsAvgInventory;

    public string Step3State => AccountGroups.Count > 0 ? "DONE" : "TODO";
    public string Step3Detail => AccountGroups.Count > 0
        ? $"{AccountGroups.Count} groups · {GroupedAccountCount} accounts assigned"
        : "A group holds its own destination link and proxy policy.";

    public string Step4State => InventoryLoadedAccounts > 0 ? "DONE" : "TODO";
    public string Step4Detail => InventoryLoadedAccounts > 0
        ? $"{InventoryLoadedAccounts} inventories loaded · {BanScannedAccounts} ban-checked · {BanCount} flagged"
        : "Scan loads inventories and checks bans in one pass.";

    private void RefreshChecklist()
    {
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(MaFileCount));
        OnPropertyChanged(nameof(GroupedAccountCount));
        OnPropertyChanged(nameof(InventoryLoadedAccounts));
        OnPropertyChanged(nameof(BanScannedAccounts));
        OnPropertyChanged(nameof(MissingMaFileCount));
        OnPropertyChanged(nameof(BlockedAccountCount));
        OnPropertyChanged(nameof(ProxyFailedCount));
        OnPropertyChanged(nameof(ScannedAccountCount));
        OnPropertyChanged(nameof(UnscannedAccountCount));
        OnPropertyChanged(nameof(AveragePerAccount));
        OnPropertyChanged(nameof(BestItemValue));
        OnPropertyChanged(nameof(BestItemName));
        OnPropertyChanged(nameof(PortfolioDeltaText));
        OnPropertyChanged(nameof(PortfolioDeltaColor));
        OnPropertyChanged(nameof(HomeHealthLine));
        OnPropertyChanged(nameof(HomeItemsLine));
        OnPropertyChanged(nameof(HomeAttentionDetail));
        OnPropertyChanged(nameof(HomeScanDetail));
        OnPropertyChanged(nameof(HomeDonutCenterTop));
        OnPropertyChanged(nameof(HomeDonutCenterBottom));
        OnPropertyChanged(nameof(Step1State));
        OnPropertyChanged(nameof(Step1Detail));
        OnPropertyChanged(nameof(Step2State));
        OnPropertyChanged(nameof(Step2Detail));
        OnPropertyChanged(nameof(Step3State));
        OnPropertyChanged(nameof(Step3Detail));
        OnPropertyChanged(nameof(Step4State));
        OnPropertyChanged(nameof(Step4Detail));
    }
    public string NextActionTitle =>
        !HasAccounts ? "Import your Steam accounts"
        : UnscannedAccountCount > 0 ? "Scan inventories & bans"
        : MissingMaFileCount > 0 ? "Add maFiles for trading"
        : ReadyAccountCount > 0 ? "Transfer ready skins"
        : "Open inventory to select items";
    public string NextActionDetail =>
        !HasAccounts ? "Import login:password list + maFiles folder."
        : UnscannedAccountCount > 0 ? $"{UnscannedAccountCount} account(s) not scanned yet — load skins and ban status."
        : MissingMaFileCount > 0 ? $"{MissingMaFileCount} account(s) need maFile for confirmations/trades. Proxy is optional."
        : ReadyAccountCount > 0 ? $"{ReadyAccountCount} account(s) have items · portfolio ${TotalPortfolio:0.00}."
        : "Pick accounts, open Inventory, select skins, then Transfer.";

    public MainViewModel(AppSettings? settings = null)
    {
        Settings = settings ?? AppSettings.Load();
        L = new LocalizationService(Settings);
        Ui = new UiStrings(L);
        Accounts.CollectionChanged += OnAccountsCollectionChanged;
        AccountGroups.CollectionChanged += (_, _) =>
        {
            RefreshChecklist();
            RebuildGroupRouteRows();
        };
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        foreach (var account in Accounts) ObserveAccount(account);
        Items.CollectionChanged += (_, _) => RefreshFilter();
        _bg = new BackgroundRefreshService(Settings);
        _bg.Log += (m, l) => Log(m, l);
        _bg.AccountUpdated += a => Dispatcher.UIThread.Post(() =>
        {
            a.OnReviewChanged();
            RecalcDashboard();
        });
        _autoConf = new ConfirmationAutoService(Settings);
        _autoConf.Log += (m, l) => Log(m, l);

        HwidAdminHint = "…";
        RealHwid = new HwidProfile { PcName = Environment.MachineName };

        // Default: spoof always for every account/transaction
        if (!Settings.AlwaysSpoofHwid)
            Settings.AlwaysSpoofHwid = true;

        SteamSession.GlobalDefaultProxy = Settings.DefaultProxy;
        SyncSfxFromSettings();
        RefreshApiKeyStatus();

        if (!string.IsNullOrWhiteSpace(Settings.DefaultTradeUrl))
            TradeUrl = Settings.DefaultTradeUrl;
        RefreshGroupSummaries();
        RebuildGroupRouteRows();
        // Inventory is grid-only (list layout removed).
        if (!Settings.InventoryLayoutGrid)
        {
            Settings.InventoryLayoutGrid = true;
            Settings.Save();
        }

        ShellPage = (int)Models.ShellPage.Home;
        StreamSafeMode = true;
        LoadCacheForAccounts();
        RecalcDashboard();
        RebuildAccountList();
        PushTimeline("boot", "SilverManager ready", "stream-safe · permanent HWID");
        // soft boot chime (Botanica Power Up 1s @ low volume)
        Dispatcher.UIThread.Post(() => _sfx.Play(Sfx.Startup), DispatcherPriority.Background);

        // NO live 2FA on screen (stream safety). Codes only on demand via Copy guard in Advanced.
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _guardTimer.Tick += (_, _) => { /* intentionally empty — codes not shown */ };

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _statsTimer.Tick += (_, _) =>
        {
            try
            {
                _stats.RecordSnapshot(Accounts, _audit);
                // Overview carries the portfolio sparkline too, so it needs the same refresh.
                if (IsStats || IsHome) RefreshStatsUi();
            }
            catch { /* */ }
        };

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RefreshHwidAdminHints();
                try { RealHwid = _hwidSvc.ReadRealHardware(); }
                catch { /* keep placeholder */ }

                var created = 0;
                foreach (var a in Accounts)
                {
                    if (a.Hwid == null)
                    {
                        a.Hwid = _hwidSvc.GenerateProfile();
                        a.Hwid.Enabled = true;
                        created++;
                    }
                }
                if (created > 0) _store.Save();

                RebuildAccountList();
                _stats.RecordSnapshot(Accounts, _audit);
                RefreshStatsUi();
                _statsTimer.Start();

                if (Settings.BackgroundRefreshEnabled) StartBackground();
                StartAutoConfirmIfNeeded();
                // Never auto Review-all on open — it jumps to the Review tab and confuses the user.
                // Ban checks stay manual (Check selected / Check all) or via optional background timer.
            }
            catch (Exception ex)
            {
                Log("Init: " + ex.Message, LogLevel.Warning);
            }
        }, DispatcherPriority.Background);
    }

    private void StartAutoConfirmIfNeeded()
    {
        _autoConf.Stop();
        if (Settings.AutoConfirmMarket || Settings.AutoConfirmTrustedTrades || Settings.AutoConfirmAllTrades)
        {
            _autoConf.Start(() => Accounts.Where(a => !a.IsBlocked).ToList(), a => _sessions.TryGet(a.Id));
            Log("Auto-confirm: ON", LogLevel.Info);
        }
    }

    private StatsPeriod CurrentPeriod => StatsPeriodIndex switch
    {
        0 => StatsPeriod.Hours24,
        1 => StatsPeriod.Days7,
        2 => StatsPeriod.Days30,
        _ => StatsPeriod.All
    };

    private void RefreshStatsUi()
    {
        var p = CurrentPeriod;
        PortfolioBars = _stats.BuildPortfolioBars(p);
        TradeBars = _stats.BuildTradeBars(p);
        VolumeBars = _stats.BuildVolumeBars(p);
        var s = _stats.Summarize(p);
        StatsBans = s.bans;
        StatsTradesOk = s.tradesOk;
        StatsTradesFail = s.tradesFail;
        StatsItems = s.items;
        StatsVolume = s.volume;
        StatsPeriodVolume = s.volume;
        StatsPeriodItems = s.items;
        var attempts = s.tradesOk + s.tradesFail;
        StatsSuccessRate = attempts == 0 ? 0 : 100.0 * s.tradesOk / attempts;
        StatsPortfolioDelta = _stats.GetPortfolioDelta(p);
        StatsSummary = p switch
        {
            StatsPeriod.Hours24 => "24 hours",
            StatsPeriod.Days7 => "7 days",
            StatsPeriod.Days30 => "30 days",
            _ => "all time"
        };
        // live bans from accounts
        StatsBans = Accounts.Count(a => a.IsBlocked || a.HasBanFlag);

        // Only tradable (or opted-in non-tradable) items feed live stats.
        var counted = Items.Where(Counts).ToList();
        var live = PortfolioAnalytics.LiveTotals(counted, Accounts);
        StatsLivePortfolio = Items.Count > 0 ? live.live : Accounts.Sum(a => a.InventoryValue);
        StatsPortfolio = StatsLivePortfolio;
        StatsLiveItems = Items.Count > 0 ? live.items : Accounts.Sum(a => a.InventoryCount);
        StatsCaseCount = live.cases;
        StatsWithdrawnLifetime = _stats.LifetimeWithdrawnUsd;
        StatsWithdrawnSession = _stats.SessionWithdrawnUsd;
        StatsWithdrawnItems = _stats.LifetimeWithdrawnItems;
        var scanned = Math.Max(1, ScannedAccountCount > 0 ? ScannedAccountCount : Accounts.Count(a => a.InventoryValue > 0 || a.InventoryCount > 0));
        if (scanned == 0) scanned = 1;
        StatsAvgInventory = StatsLivePortfolio / scanned;

        var cases = PortfolioAnalytics.BuildCaseRows(counted);
        var tops = PortfolioAnalytics.BuildTopSkins(counted);
        var agg = PortfolioAnalytics.BuildTopAggregated(counted);

        CaseStats.Clear();
        foreach (var r in cases) CaseStats.Add(r);
        TopSkinStats.Clear();
        foreach (var r in tops) TopSkinStats.Add(r);
        HomeTopSkins.Clear();
        foreach (var r in tops.Take(5)) HomeTopSkins.Add(r);
        TopAggregateStats.Clear();
        foreach (var r in agg) TopAggregateStats.Add(r);
        CategoryStats.Clear();

        var rarities = PortfolioAnalytics.BuildRarityRows(counted);
        QualityStats.Clear();
        foreach (var r in rarities) QualityStats.Add(r);

        var counts = PortfolioAnalytics.BuildCountBreakdown(counted, top: 7);
        HomeCountStats.Clear();
        foreach (var r in counts) HomeCountStats.Add(r);

        RebuildHomeTopAccounts();
        OnPropertyChanged(nameof(BestItemValue));
        OnPropertyChanged(nameof(BestItemName));
        OnPropertyChanged(nameof(PortfolioDeltaText));
        OnPropertyChanged(nameof(PortfolioDeltaColor));
        OnPropertyChanged(nameof(StatsPortfolioDeltaText));
        OnPropertyChanged(nameof(HomeItemsLine));
        OnPropertyChanged(nameof(HomeDonutCenterTop));
        OnPropertyChanged(nameof(HomeDonutCenterBottom));
        OnPropertyChanged(nameof(AveragePerAccount));
        OnPropertyChanged(nameof(StatsAvgInventory));
        OnPropertyChanged(nameof(ScannedAccountCount));
        OnPropertyChanged(nameof(UnscannedAccountCount));
        OnPropertyChanged(nameof(HomeScanDetail));
        OnPropertyChanged(nameof(HomeAttentionDetail));
        OnPropertyChanged(nameof(ShowProxyRecommendation));
        OnPropertyChanged(nameof(ProxyRecommendationBanner));
        OnPropertyChanged(nameof(ReviewCleanCount));
        OnPropertyChanged(nameof(ReviewBannedCount));
        OnPropertyChanged(nameof(ReviewUncheckedCount));
        OnPropertyChanged(nameof(ReviewSummaryLine));
    }

    private void RebuildHomeTopAccounts()
    {
        HomeTopAccounts.Clear();
        foreach (var a in Accounts
                     .OrderByDescending(x => x.InventoryValue)
                     .ThenByDescending(x => x.InventoryCount)
                     .Take(8))
            HomeTopAccounts.Add(a);
    }

    partial void OnShellPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsInventory));
        OnPropertyChanged(nameof(IsTransfer));
        OnPropertyChanged(nameof(IsConfirmations));
        OnPropertyChanged(nameof(IsIncoming));
        OnPropertyChanged(nameof(IsReview));
        OnPropertyChanged(nameof(IsAudit));
        OnPropertyChanged(nameof(IsStats));
        OnPropertyChanged(nameof(IsProxyPage));
        OnPropertyChanged(nameof(IsGroups));
        OnPropertyChanged(nameof(IsAccounts));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsHwid));
        OnPropertyChanged(nameof(IsAutoFarm));
        PageTransition = true;
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(160);
            PageTransition = false;
        });
        if (value is (int)Models.ShellPage.Stats or (int)Models.ShellPage.Home)
            RefreshStatsUi();
        if (value is (int)Models.ShellPage.Hwid)
            RefreshHwidPage();
        // keep Advanced open when user lands on a secondary page
        if (value is (int)Models.ShellPage.Confirmations
            or (int)Models.ShellPage.Incoming or (int)Models.ShellPage.Review
            or (int)Models.ShellPage.Audit)
            AdvancedNavOpen = true;
        // soft page change
        _sfx.Play(Sfx.Nav, debounceClick: true);
    }

    partial void OnStatsPeriodIndexChanged(int value)
    {
        OnPropertyChanged(nameof(StatsPeriodKey));
        RefreshStatsUi();
    }
    partial void OnHideBlockedAccountsChanged(bool value) => RebuildAccountList();

    partial void OnSearchQueryChanged(string value) => RefreshFilter();
    partial void OnTradableOnlyChanged(bool value) => RefreshFilter();
    partial void OnFilterCasesOnlyChanged(bool value) => RefreshFilter();
    partial void OnFilterReadyOnlyChanged(bool value) => RefreshFilter();
    partial void OnFilterMinPriceChanged(decimal value) => RefreshFilter();
    partial void OnHideTradeHoldChanged(bool value) => RefreshFilter();
    partial void OnAccountFilterChanged(string value) => RebuildAccountList();
    partial void OnAccountQuickFilterChanged(string value) => RebuildAccountList();

    /// <summary>Mirror the active group onto the chips so a selected chip reads as pressed.</summary>
    partial void OnSelectedAccountGroupChanged(AccountGroup? value)
    {
        foreach (var g in AccountGroups)
        {
            g.IsSelectedGroup = g == value;
            g.IsProxyTarget = g == value;
        }
        OnPropertyChanged(nameof(ProxyGroupHeader));
        OnPropertyChanged(nameof(HasProxyGroup));
    }

    public bool HasProxyGroup => SelectedAccountGroup != null;
    public string ProxyGroupHeader => SelectedAccountGroup == null
        ? "Pick a group above"
        : $"{SelectedAccountGroup.Name} · {SelectedAccountGroup.AccountCountText}";

    partial void OnTradeUrlChanged(string value)
    {
        try
        {
            if (value.Contains("partner=", StringComparison.OrdinalIgnoreCase))
            {
                var info = SteamSession.ParseTradeUrl(value);
                TradePartner = info.PartnerSteam64;
            }
            else TradePartner = null;
        }
        catch { TradePartner = null; }
    }

    public void StartBackground()
    {
        _bg.Start(() => Accounts.ToList(), a => _sessions.TryGet(a.Id));
        BgStatus = Settings.BackgroundRefreshEnabled ? $"bg · {Settings.BackgroundRefreshMinutes}m" : "bg · off";
    }

    private void Log(string message, LogLevel level = LogLevel.Info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Logs.Insert(0, new LogEntry { Message = message, Level = level });
            while (Logs.Count > 300) Logs.RemoveAt(Logs.Count - 1);
            // rare UI sounds — not every log line
            if (level == LogLevel.Error) _sfx.Play(Sfx.Error, debounceClick: true);
        });
    }

    private void SyncSfxFromSettings()
    {
        _sfx.Enabled = Settings.SoundEnabled;
        _sfx.Volume = Math.Clamp(Settings.SoundVolumePercent / 100f, 0f, 1f);
    }

    public void PlaySfx(Sfx sfx) => _sfx.Play(sfx, debounceClick: true);

    private void PushTimeline(string icon, string title, string detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Timeline.Insert(0, new TimelineEntry { Icon = icon, Title = title, Detail = detail });
            while (Timeline.Count > 80) Timeline.RemoveAt(Timeline.Count - 1);
        });
    }

    private void SetBusy(string? text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsBusy = text != null;
            BusyText = text;
            ActivityIndeterminate = true;
        });
    }

    /// <summary>
    /// Whether an item counts toward portfolio value and item counts. Permanently untradable
    /// items are excluded unless the user opts in, so headline numbers only ever describe
    /// value the user can actually move.
    /// </summary>
    /// <summary>
    /// Portfolio / stats counting. Default (CountNonTradable off): only currently tradable
    /// items contribute to value and totals. Hold / non-tradable skins stay visible in Inventory
    /// but do not inflate the portfolio number.
    /// </summary>
    public bool Counts(InventoryItem i) => !i.IsPermanentlyUntradable;

    private void RecalcDashboard()
    {
        // Prefer live Items with Counts(); only fall back to account snapshots when nothing is loaded.
        if (Items.Count > 0)
            TotalPortfolio = Items.Where(Counts).Sum(i => i.Price * Math.Max(1, i.Amount));
        else
            TotalPortfolio = Accounts.Sum(a => a.InventoryValue);
        OnlineCount = Accounts.Count(a => a.Status == AccountStatus.Online);
        BanCount = Accounts.Count(a => a.HasBanFlag);
        ProxyAssignedCount = Accounts.Count(a => a.HasProxy);
        var def = string.IsNullOrWhiteSpace(Settings.DefaultProxy) ? 0 : 1;
        var sharedGroups = Accounts.Where(a => a.HasProxy)
            .GroupBy(a => a.Proxy!, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        ProxyStatus = ProxyAssignedCount > 0
            ? T($"proxy: {ProxyAssignedCount} acc · {sharedGroups} shared" + (def > 0 ? " + default" : ""), $"прокси: {ProxyAssignedCount} акк. · {sharedGroups} общих" + (def > 0 ? " + default" : ""))
            : T(def > 0 ? "proxy: default only" : "proxy: —", def > 0 ? "прокси: только default" : "прокси: —");
        RefreshProxyUsage();
        var scores = Accounts.Select(HealthScore).ToList();
        HealthSummary = scores.Count == 0 ? "—" : $"avg health {scores.Average():0}";
        RefreshChecklist();
        RebuildHomeTopAccounts();
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(ReadyAccountCount));
        OnPropertyChanged(nameof(NextActionTitle));
        OnPropertyChanged(nameof(NextActionDetail));
        // Full stats rebuild only on Stats page (Home uses light refresh during ops)
        if (IsStats)
            RefreshStatsUi();
        else
        {
            OnPropertyChanged(nameof(HomeHealthLine));
            OnPropertyChanged(nameof(HomeScanDetail));
            OnPropertyChanged(nameof(HomeAttentionDetail));
            OnPropertyChanged(nameof(HomeItemsLine));
            OnPropertyChanged(nameof(AccountsSubtitle));
        }
        StatusBanner = T(
            $"SilverManager · {Accounts.Count} acc · ${TotalPortfolio:0.00} · {OnlineCount} online · {ProxyAssignedCount} px · out ${_stats.SessionWithdrawnUsd:0.00}",
            $"SilverManager · {Accounts.Count} акк. · ${TotalPortfolio:0.00} · {OnlineCount} online · {ProxyAssignedCount} px · out ${_stats.SessionWithdrawnUsd:0.00}");
        OnPropertyChanged(nameof(ShowProxyRecommendation));
        OnPropertyChanged(nameof(ProxyRecommendationBanner));
        OnPropertyChanged(nameof(HwidProfileCount));
        OnPropertyChanged(nameof(HwidSummaryLine));
        OnPropertyChanged(nameof(Step2State));
        OnPropertyChanged(nameof(Step2Detail));
        NotifyLocalizedChips();
    }

    public string AccountsSubtitle =>
        T(
            $"{Accounts.Count} total · {SelectedAccountCount} selected · {ScannedAccountCount} scanned · {AccountGroups.Count} groups",
            $"{Accounts.Count} всего · {SelectedAccountCount} выбр. · {ScannedAccountCount} скан · {AccountGroups.Count} групп");

    // Localized chips / dynamic labels (refresh on language + count changes)
    public string ChipAccCount => $"{SelectedAccountCount} {T("acc", "акк.")}";
    public string ChipSelectedCount => $"{SelectedAccountCount} {T("selected", "выбрано")}";
    public string ChipScannedCount => $"{ScannedAccountCount} {T("scanned", "скан")}";
    public string ChipSrcCount => $"{SelectedAccountCount} {T("src", "ист.")}";
    public string InventoryVisibleSubText =>
        $"${VisibleValue:0.00} · {T("visible · click to select", "видимое · клик — выбор")}";
    public string HiddenFiltersText =>
        $"{HiddenByFilterCount} {T("items hidden by filters", "скрыто фильтрами")}";
    public string StagedMembersHint =>
        $"{StagedMemberCount} {T("accounts selected · nothing changes until you save", "акк. выбрано · сохрани, чтобы применить")}";
    public string TransferSourcesLine =>
        $"{SelectedAccountCount} {T("sources", "источн.")} · {SelectedItemCount} {T("items selected", "предметов")}";
    public string SelectedItemsBarText =>
        $"{SelectedItemCount} {T("selected", "выбрано")} · ${SelectedValue:0.00}";
    public string ScannedAccountsLine =>
        $"{ScannedAccountCount} {T("scanned accounts", "акк. просканировано")}";
    public string HwidProfilesChip =>
        $"{HwidProfileCount} {T("profiles", "профилей")}";
    public string BestItemLine =>
        string.IsNullOrWhiteSpace(BestItemName)
            ? "—"
            : $"{T("Best", "Лучший")}: {BestItemName}";
    public string TradePartnerLine =>
        string.IsNullOrWhiteSpace(TradePartner)
            ? T("Paste a trade link or enable warehouse above", "Вставь trade link или включи склад выше")
            : $"{T("Partner", "Партнёр")} {TradePartner}";

    private void NotifyLocalizedChips()
    {
        OnPropertyChanged(nameof(ChipAccCount));
        OnPropertyChanged(nameof(ChipSelectedCount));
        OnPropertyChanged(nameof(ChipScannedCount));
        OnPropertyChanged(nameof(ChipSrcCount));
        OnPropertyChanged(nameof(InventoryVisibleSubText));
        OnPropertyChanged(nameof(HiddenFiltersText));
        OnPropertyChanged(nameof(StagedMembersHint));
        OnPropertyChanged(nameof(TransferSourcesLine));
        OnPropertyChanged(nameof(SelectedItemsBarText));
        OnPropertyChanged(nameof(ScannedAccountsLine));
        OnPropertyChanged(nameof(HwidProfilesChip));
        OnPropertyChanged(nameof(BestItemLine));
        OnPropertyChanged(nameof(TradePartnerLine));
        OnPropertyChanged(nameof(AccountsSubtitle));
        OnPropertyChanged(nameof(IncomingEmptyHint));
        foreach (var a in Accounts)
            a.NotifyLocalizedStatus();
    }

    private void RefreshProxyUsage()
    {
        ProxyUsageRows.Clear();
        foreach (var row in ProxyHelper.BuildUsage(Accounts).Take(40))
            ProxyUsageRows.Add(row);
    }

    public static int HealthScore(SteamAccount a)
    {
        var s = 100;
        if (!a.HasMaFile) s -= 30;
        if (a.HasBanFlag) s -= 40;
        if (string.IsNullOrEmpty(a.SteamId64)) s -= 10;
        if (a.Status == AccountStatus.Error) s -= 15;
        if (a.InventoryCount == 0) s -= 5;
        return Math.Clamp(s, 0, 100);
    }

    private void RefreshFilter()
    {
        Dispatcher.UIThread.Post(() =>
        {
            FilteredItems.Clear();
            var q = SearchQuery.Trim();
            // 0 selected → empty inventory (do NOT show all)
            var selectedIds = Accounts.Where(a => a.IsSelected).Select(a => a.Id).ToHashSet();
            if (selectedIds.Count == 0)
            {
                VisibleValue = 0;
                HiddenByFilterCount = 0;
                DeadWeightCount = 0;
                InventoryEmptyTitle = Items.Count > 0 ? "No accounts selected" : "Nothing here yet";
                InventoryEmptyHint = Items.Count > 0
                    ? "Pick accounts or a group on the Accounts page to see their items."
                    : "Scan accounts to load their inventories.";
                RecalcSelection();
                RecalcDashboard();
                RebuildPipelinePreview();
                OnPropertyChanged(nameof(SelectedAccountCount));
                OnPropertyChanged(nameof(AccountsSubtitle));
                return;
            }

            decimal vis = 0;
            var owned = 0;
            var deadWeight = 0;
            var pass = new List<InventoryItem>();
            foreach (var it in Items)
            {
                if (!selectedIds.Contains(it.AccountId)) continue;
                // Setting off: hide only items that are permanently untradable (pins, coins).
                // Items on a temporary trade hold (IsOnTradeHold) always stay visible in the Inventory grid.
                if (!Settings.CountNonTradable && it.IsPermanentlyUntradable) { deadWeight++; continue; }
                owned++;
                if (TradableOnly && !it.Tradable) continue;
                if (HideTradeHold && it.IsOnTradeHold) continue;
                if (FilterReadyOnly && it.IsOnTradeHold) continue;
                if (FilterCasesOnly && !ItemClassifier.IsCase(it)) continue;
                if (FilterMinPrice > 0 && it.Price < FilterMinPrice) continue;
                // Smart rules decide what may be *sent*, not what the user may *see*.
                // They require Tradable, so applying them here hid every fresh weekly drop.
                if (!string.IsNullOrEmpty(q) &&
                    !it.MarketHashName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !it.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !it.Exterior.Contains(q, StringComparison.OrdinalIgnoreCase))
                    continue;
                pass.Add(it);
                vis += it.Price * Math.Max(1, it.Amount);
            }

            IEnumerable<InventoryItem> ordered = InventorySortIndex switch
            {
                0 => pass.OrderByDescending(i => i.Price).ThenBy(i => i.MarketHashName, StringComparer.OrdinalIgnoreCase),
                1 => pass.OrderBy(i => i.Price).ThenBy(i => i.MarketHashName, StringComparer.OrdinalIgnoreCase),
                2 => pass.OrderBy(i => i.MarketHashName, StringComparer.OrdinalIgnoreCase),
                _ => pass
            };
            foreach (var it in ordered) FilteredItems.Add(it);

            VisibleValue = vis;
            HiddenByFilterCount = owned - FilteredItems.Count;
            DeadWeightCount = deadWeight;

            // An empty grid must say why: no items at all, or everything filtered out.
            if (owned == 0)
            {
                InventoryEmptyTitle = "These accounts have no items";
                InventoryEmptyHint = "Run a scan on the selected accounts to load their inventories.";
            }
            else if (FilteredItems.Count == 0)
            {
                InventoryEmptyTitle = $"{owned} items hidden by filters";
                InventoryEmptyHint = "Clear the filters above to see everything these accounts hold.";
            }

            RecalcSelection();
            RecalcDashboard();
            RebuildPipelinePreview();
            // Do NOT RebuildAccountList here — clearing FilteredAccounts rebinds avatars and
            // makes every selection click look like a full image reload.
            OnPropertyChanged(nameof(ShowInventoryGrid));
            OnPropertyChanged(nameof(ShowInventoryList));
            OnPropertyChanged(nameof(SelectedAccountCount));
            OnPropertyChanged(nameof(AccountsSubtitle));
        });
    }

    /// <summary>Reset every inventory filter — the escape hatch from an empty grid.</summary>
    [RelayCommand]
    private void ClearInventoryFilters()
    {
        SearchQuery = "";
        TradableOnly = false;
        HideTradeHold = false;
        FilterReadyOnly = false;
        FilterCasesOnly = false;
        FilterMinPrice = 0;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.MinPriceToSend) or nameof(AppSettings.MaxPriceToSend)
            or nameof(AppSettings.ExcludeSouvenirs) or nameof(AppSettings.ExcludeStatTrak)
            or nameof(AppSettings.SkipTradeHoldItems) or nameof(AppSettings.MaxItemsPerOffer)
            or nameof(AppSettings.SessionValueLimit) or nameof(AppSettings.CountNonTradable))
        {
            RefreshSmartRuleSummary();
            RefreshFilter();
        }
        if (e.PropertyName is nameof(AppSettings.CountNonTradable) or nameof(AppSettings.SendToWarehouse)
            or nameof(AppSettings.RouteTradesByGroup)
            or nameof(AppSettings.InventoryLayoutGrid)
            or nameof(AppSettings.AccountGroupsPanelExpanded)
            or nameof(AppSettings.SoundEnabled)
            or nameof(AppSettings.SoundVolumePercent)
            or nameof(AppSettings.AlwaysSpoofHwid)
            or nameof(AppSettings.SafeModeDryRun)
            or nameof(AppSettings.SkipTradeHoldItems))
        {
            Settings.Save();
            OnPropertyChanged(nameof(RouteSummary));
            OnPropertyChanged(nameof(ShowGroupRouteMap));
            OnPropertyChanged(nameof(IsInventoryGrid));
            OnPropertyChanged(nameof(IsInventoryList));
            OnPropertyChanged(nameof(ShowInventoryGrid));
            OnPropertyChanged(nameof(ShowInventoryList));
            OnPropertyChanged(nameof(AccountGroupsPanelExpanded));
            OnPropertyChanged(nameof(AccountGroupsPanelToggleLabel));
            if (e.PropertyName is nameof(AppSettings.RouteTradesByGroup) or nameof(AppSettings.SendToWarehouse))
                RebuildGroupRouteRows();
            if (e.PropertyName is nameof(AppSettings.CountNonTradable))
                RepriceAllItemsAndTotals();
            if (e.PropertyName is nameof(AppSettings.SoundEnabled) or nameof(AppSettings.SoundVolumePercent))
                SyncSfxFromSettings();
        }
    }

    private void OnAccountsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var account in e.NewItems.OfType<SteamAccount>()) ObserveAccount(account);
        if (e.OldItems != null)
            foreach (var account in e.OldItems.OfType<SteamAccount>()) account.PropertyChanged -= OnAccountPropertyChanged;
        RebuildAccountList();
    }

    private void ObserveAccount(SteamAccount account)
    {
        account.PropertyChanged -= OnAccountPropertyChanged;
        account.PropertyChanged += OnAccountPropertyChanged;
    }

    private void OnAccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // During batch scan/load, skip — we refresh once at the end.
        if (_bulkUi) return;

        if (e.PropertyName is nameof(SteamAccount.IsSelected) or nameof(SteamAccount.InventoryCount) or nameof(SteamAccount.InventoryValue)
            or nameof(SteamAccount.HasMaFile) or nameof(SteamAccount.Proxy) or nameof(SteamAccount.ProxyCheckOk)
            or nameof(SteamAccount.Status) or nameof(SteamAccount.GroupName) or nameof(SteamAccount.IsMarkedBanned)
            or nameof(SteamAccount.OwnTradeUrl) or nameof(SteamAccount.IsWarehouse))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_bulkUi) return;
                // Selection must NOT rebuild FilteredAccounts — Clear/Add rebinds AdvancedImage and flashes avatars.
                if (e.PropertyName is nameof(SteamAccount.IsSelected))
                {
                    SelectedAccountCount = Accounts.Count(a => a.IsSelected);
                    OnPropertyChanged(nameof(SelectedAccountCount));
                    OnPropertyChanged(nameof(AccountsSubtitle));
                    OnPropertyChanged(nameof(AttentionCount));
                    OnPropertyChanged(nameof(ReadyAccountCount));
                    RefreshFilter();
                    RecalcDashboardLight();
                    return;
                }
                // IsWarehouse: never rebuild the whole list (and never notify ComboBox candidates
                // from here) — that re-enters WarehouseAccount setter and freezes the UI.
                if (e.PropertyName is nameof(SteamAccount.IsWarehouse))
                {
                    RefreshWarehouseUiLight();
                    return;
                }
                if (e.PropertyName is nameof(SteamAccount.GroupName)
                    or nameof(SteamAccount.IsMarkedBanned) or nameof(SteamAccount.HasMaFile))
                    RebuildAccountList();
                else
                    RecalcDashboardLight();
            });
        }
    }

    /// <summary>Cheap dashboard numbers without charts / full item aggregation.</summary>
    private void RecalcDashboardLight()
    {
        if (Items.Count > 0)
            TotalPortfolio = Items.Where(Counts).Sum(i => i.Price * Math.Max(1, i.Amount));
        else
            TotalPortfolio = Accounts.Sum(a => a.InventoryValue);
        OnlineCount = Accounts.Count(a => a.Status == AccountStatus.Online);
        BanCount = Accounts.Count(a => a.HasBanFlag);
        ProxyAssignedCount = Accounts.Count(a => a.HasProxy);
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(ReadyAccountCount));
        OnPropertyChanged(nameof(ScannedAccountCount));
        OnPropertyChanged(nameof(UnscannedAccountCount));
        OnPropertyChanged(nameof(HomeHealthLine));
        OnPropertyChanged(nameof(HomeScanDetail));
        OnPropertyChanged(nameof(HomeAttentionDetail));
        OnPropertyChanged(nameof(NextActionTitle));
        OnPropertyChanged(nameof(NextActionDetail));
        OnPropertyChanged(nameof(AccountsSubtitle));
        OnPropertyChanged(nameof(ReviewCleanCount));
        OnPropertyChanged(nameof(ReviewBannedCount));
        OnPropertyChanged(nameof(ReviewUncheckedCount));
        OnPropertyChanged(nameof(ReviewSummaryLine));
        StatusBanner = T(
            $"SilverManager · {Accounts.Count} acc · ${TotalPortfolio:0.00} · {OnlineCount} online · {ProxyAssignedCount} px",
            $"SilverManager · {Accounts.Count} акк. · ${TotalPortfolio:0.00} · {OnlineCount} online · {ProxyAssignedCount} px");
        NotifyLocalizedChips();
    }

    private void RebuildAccountList()
    {
        RefreshGroupSummaries();
        // Light warehouse labels only — full RefreshWarehouseUi rebuilds ComboBox items and can re-enter.
        RefreshWarehouseUiLight();
        var q = AccountFilter.Trim();
        FilteredAccounts.Clear();
        IEnumerable<SteamAccount> src = Accounts;
        if (HideBlockedAccounts)
            src = src.Where(a => !a.IsBlocked);
        src = AccountQuickFilter switch
        {
            "Selected" => src.Where(a => a.IsSelected),
            "Online" => src.Where(a => a.Status == AccountStatus.Online),
            "Ready" => src.Where(a => a.CanTrade && a.InventoryCount > 0),
            "Attention" => src.Where(a => a.IsBlocked || !a.HasMaFile || a.ProxyCheckOk == false),
            "Blocked" => src.Where(a => a.IsBlocked || a.HasBanFlag),
            "No maFile" => src.Where(a => !a.HasMaFile),
            "No proxy" => src.Where(a => !a.HasProxy),
            "Ungrouped" => src.Where(a => string.IsNullOrWhiteSpace(a.GroupName)),
            _ => src
        };
        if (!string.IsNullOrEmpty(q))
            src = src.Where(a =>
                a.Login.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (a.PersonaName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.GroupName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.SteamId64?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) ||
                (a.BanReason?.Contains(q, StringComparison.OrdinalIgnoreCase) == true));
        foreach (var a in src)
            FilteredAccounts.Add(a);
        SelectedAccountCount = Accounts.Count(a => a.IsSelected);
        BanCount = Accounts.Count(a => a.IsBlocked || a.HasBanFlag);
        RefreshChecklist();
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(ReadyAccountCount));
        OnPropertyChanged(nameof(NextActionTitle));
        OnPropertyChanged(nameof(NextActionDetail));
    }

    private bool _hwidNoAdminWarned;

    /// <summary>Ensure permanent HWID profile exists; registry only if Admin (soft otherwise).</summary>
    private void EnsureAndApplyHwid(SteamAccount acc)
    {
        if (acc.Hwid == null)
        {
            acc.Hwid = _hwidSvc.GenerateProfile();
            acc.Hwid.Enabled = true;
            _store.Save();
            Log($"{acc.Login}: HWID profile created (permanent)", LogLevel.Info);
        }

        if (!Settings.AlwaysSpoofHwid || acc.Hwid is not { Enabled: true }) return;

        // Profile always saved; registry only with elevation — never break trade queue
        if (_hwidSvc.TryApplyForLaunch(acc.Hwid, out var note))
            return;

        if (!_hwidNoAdminWarned)
        {
            _hwidNoAdminWarned = true;
            Log(T($"HWID: {note}. Trades continue without registry spoof. Run as Administrator for full spoof.", $"HWID: {note}. Трейды продолжаются без registry-spoof. Для полного spoof запустите от имени администратора."),
                LogLevel.Warning);
        }
    }

    private bool PassSmartRules(InventoryItem it)
    {
        if (Settings.SkipTradeHoldItems && it.IsOnTradeHold) return false;
        if (!it.Tradable) return false;
        if (it.Price < Settings.MinPriceToSend) return false;
        if (it.Price > Settings.MaxPriceToSend) return false;
        if (Settings.ExcludeSouvenirs && it.MarketHashName.Contains("Souvenir", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Settings.ExcludeStatTrak && it.MarketHashName.Contains("StatTrak", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private void PushQueueUi()
    {
        Dispatcher.UIThread.Post(() =>
        {
            QueueRunning = _job.IsRunning;
            QueuePaused = _job.IsPaused;
            QueueProgressText = _job.ProgressText;
            QueueEtaText = _job.EtaText;
            QueuePercent = _job.Total <= 0 ? 0 : Math.Clamp(100.0 * _job.Done / _job.Total, 0, 100);
            ActivityProgress = QueuePercent;
            ActivityIndeterminate = _job.IsRunning && _job.Total == 0;
            if (_job.IsRunning)
            {
                IsBusy = true;
                BusyText = _job.ProgressText;
            }
        });
    }

    [RelayCommand]
    private void PauseQueue()
    {
        _job.Pause();
        PushQueueUi();
        Log("Queue: PAUSE", LogLevel.Warning);
    }

    [RelayCommand]
    private void ResumeQueue()
    {
        _job.Resume();
        PushQueueUi();
        Log("Queue: RESUME", LogLevel.Info);
    }

    [RelayCommand]
    private void CancelQueue()
    {
        _job.Cancel();
        PushQueueUi();
        Log("Queue: CANCEL requested", LogLevel.Warning);
    }

    private void RecalcSelection()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        SelectedItemCount = selected.Count;
        SelectedValue = selected.Sum(i => i.Price);
        RebuildPipelinePreview();
    }

    private void RebuildPipelinePreview()
    {
        var selected = Items.Where(i => i.IsSelected && i.Tradable).ToList();
        var groups = selected.GroupBy(i => i.AccountId).Count();
        var offers = selected.GroupBy(i => i.AccountId)
            .Sum(g => (int)Math.Ceiling(g.Count() / (double)Math.Max(1, Settings.MaxItemsPerOffer)));
        PipelinePreview =
            $"items {selected.Count} · accounts {groups} · offers ~{offers} · ${selected.Sum(i => i.Price):0.00}" +
            (Settings.SafeModeDryRun ? " · DRY-RUN" : "") +
            (Settings.MainSinkMode ? " · SINK" : "");
    }

    // ── Navigation ────────────────────────────────────────────

    [RelayCommand] private void ToggleAdvancedNav() => AdvancedNavOpen = !AdvancedNavOpen;
    [RelayCommand] private void GoHome() => ShellPage = (int)Models.ShellPage.Home;
    [RelayCommand] private void SetAccountQuickFilter(string filter) => AccountQuickFilter = string.IsNullOrWhiteSpace(filter) ? "All" : filter;
    [RelayCommand] private void SetTransferPlanFilter(string filter) => TransferPlanFilter = string.IsNullOrWhiteSpace(filter) ? "All" : filter;
    [RelayCommand] private void OpenTransferReview()
    {
        PendingTransferPlan = BuildTransferPlan(dryRun: false);
        TransferPlanApproved = false;
        RebuildTransferPlanRows();
        TransferReview = TransferPlanSummary;
        ShowTransferReview = true;
    }

    partial void OnPendingTransferPlanChanged(TransferPlan? value)
    {
        OnPropertyChanged(nameof(TransferPlanSummary));
        OnPropertyChanged(nameof(TransferPlanIssueSummary));
        OnPropertyChanged(nameof(HasPendingTransferPlan));
        OnPropertyChanged(nameof(CanSendTransferPlan));
    }
    partial void OnTransferPlanApprovedChanged(bool value) => OnPropertyChanged(nameof(CanSendTransferPlan));
    partial void OnTransferPlanFilterChanged(string value) => RebuildTransferPlanRows();
    partial void OnTransferPlanSearchChanged(string value) => RebuildTransferPlanRows();

    private void RebuildTransferPlanRows()
    {
        FilteredTransferPlanAccounts.Clear();
        if (PendingTransferPlan == null) return;
        IEnumerable<TransferPlanAccount> rows = PendingTransferPlan.Accounts;
        rows = TransferPlanFilter switch
        {
            "Ready" => rows.Where(x => x.IsReady),
            "Issues" => rows.Where(x => !x.IsReady),
            _ => rows
        };
        var q = TransferPlanSearch.Trim();
        if (!string.IsNullOrWhiteSpace(q))
            rows = rows.Where(x => x.Login.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (x.GroupName?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                || x.DestinationSteam64.Contains(q, StringComparison.OrdinalIgnoreCase));
        // Compact preview: retain an upper bound in the UI even for >1,000 accounts.
        foreach (var row in rows.Take(100)) FilteredTransferPlanAccounts.Add(row);
    }

    [RelayCommand] private void CloseTransferReview() => ShowTransferReview = false;
    [RelayCommand] private void GoTransferFromReview()
    {
        ShowTransferReview = false;
        ShellPage = (int)Models.ShellPage.Transfer;
    }
    [RelayCommand]
    private async Task SendApprovedTransferPlanAsync()
    {
        if (!CanSendTransferPlan || PendingTransferPlan == null)
        {
            Log("Build and approve a valid transfer plan first", LogLevel.Warning);
            return;
        }
        await ExecuteTransferPlanAsync(PendingTransferPlan);
    }
    [RelayCommand] private void GoInventory() => ShellPage = (int)Models.ShellPage.Inventory;
    [RelayCommand] private void GoTransfer() => ShellPage = (int)Models.ShellPage.Transfer;
    [RelayCommand] private void GoConfirmations() => ShellPage = (int)Models.ShellPage.Confirmations;
    [RelayCommand] private void GoIncoming() => ShellPage = (int)Models.ShellPage.Incoming;
    [RelayCommand] private void GoReview() { ShellPage = (int)Models.ShellPage.Review; RefreshApiKeyStatus(); }
    [RelayCommand] private void GoAudit() { ShellPage = (int)Models.ShellPage.Audit; }
    [RelayCommand] private void GoStats() { ShellPage = (int)Models.ShellPage.Stats; }
    [RelayCommand] private void GoGroups() { ShellPage = (int)Models.ShellPage.Groups; RefreshGroupSummaries(); RebuildAccountList(); }
    [RelayCommand] private void GoAccounts() { ShellPage = (int)Models.ShellPage.Accounts; RebuildAccountList(); }
    [RelayCommand] private void GoHwid() { ShellPage = (int)Models.ShellPage.Hwid; RefreshHwidPage(); }
    [RelayCommand] private void GoAutoFarm() => ShellPage = (int)Models.ShellPage.AutoFarm;

    public const string MonkePanelSiteUrl = "https://www.monkepanel.com";
    public const string MonkePanelTelegramUrl = "https://t.me/monkepanel";

    [RelayCommand]
    private void OpenMonkePanelSite() => OpenExternalUrl(MonkePanelSiteUrl, "MonkePanel site");

    [RelayCommand]
    private void OpenMonkePanelTelegram() => OpenExternalUrl(MonkePanelTelegramUrl, "MonkePanel Telegram");

    private void OpenExternalUrl(string url, string label)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            Log($"Opened {label}: {url}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Log($"Could not open browser ({label}): {ex.Message} · {url}", LogLevel.Error);
        }
    }
    [RelayCommand] private void RunNextAction()
    {
        if (!HasAccounts) { OpenImport(); return; }
        if (UnscannedAccountCount > 0) { _ = ScanAccountsAsync(); return; }
        if (MissingMaFileCount > 0) { GoAccounts(); return; }
        if (ReadyAccountCount > 0) { GoTransfer(); return; }
        GoInventory();
    }

    /// <summary>Selects every usable account when the user has not chosen any yet.</summary>
    private int EnsureSelectionOrAll()
    {
        var selected = Accounts.Count(a => a.IsSelected && !a.IsBlocked);
        if (selected > 0) return selected;
        foreach (var a in Accounts) a.IsSelected = !a.IsBlocked;
        RebuildAccountList();
        return Accounts.Count(a => a.IsSelected);
    }

    /// <summary>Force-select every non-blocked account (for Scan all).</summary>
    private int SelectAllUsableAccounts()
    {
        foreach (var a in Accounts) a.IsSelected = !a.IsBlocked;
        RebuildAccountList();
        return Accounts.Count(a => a.IsSelected);
    }

    /// <summary>Home / primary: scan every account (not only current checkboxes).</summary>
    [RelayCommand]
    private async Task ScanAllAccountsAsync()
    {
        if (!HasAccounts) { Log("Import accounts first", LogLevel.Warning); OpenImport(); return; }
        var count = SelectAllUsableAccounts();
        if (count == 0) { Log("No usable accounts to scan", LogLevel.Warning); return; }
        await BeginScanAsync(count);
    }

    /// <summary>Scan only currently checked accounts. Does not auto-select all.</summary>
    [RelayCommand]
    private async Task ScanSelectedAccountsAsync()
    {
        if (!HasAccounts) { Log("Import accounts first", LogLevel.Warning); OpenImport(); return; }
        var count = Accounts.Count(a => a.IsSelected && !a.IsBlocked);
        if (count == 0)
        {
            Log("Select accounts first (checkboxes), or use Scan all", LogLevel.Warning);
            return;
        }
        await BeginScanAsync(count);
    }

    /// <summary>Legacy name → Scan all (home, next-action, etc.).</summary>
    [RelayCommand]
    private async Task ScanAccountsAsync() => await ScanAllAccountsAsync();

    private async Task BeginScanAsync(int count)
    {
        // Soft gate: mass scan without proxies is the #1 rate-limit source.
        if (ShowProxyRecommendation && count >= 10)
        {
            AskConfirm(
                "Proxies recommended",
                ProxyRecommendationBanner + "\n\nScanning many accounts from one IP often hits rate limits or temporary blocks.\nContinue anyway?",
                "Scan anyway",
                () => _ = RunScanPipelineAsync(count));
            return;
        }

        await RunScanPipelineAsync(count);
    }

    private async Task RunScanPipelineAsync(int count)
    {
        Log($"Scan started for {count} accounts", LogLevel.Info);
        // Fresh network load (not stale cache) so portfolio matches live inventories.
        await LoadInventoriesAsync();
        if (!string.IsNullOrWhiteSpace(Settings.SteamWebApiKey))
            await CheckVacAsync();
        else
            Log("Ban scan skipped: add a Steam Web API key in Settings", LogLevel.Warning);
        RefreshChecklist();
        Log("Scan finished", LogLevel.Success);
    }
    [RelayCommand] private void GoSettings() { ShellPage = (int)Models.ShellPage.Settings; }

    [RelayCommand]
    private void SetStatsPeriod(string? period)
    {
        StatsPeriodIndex = period switch
        {
            "24h" => 0,
            "7d" => 1,
            "30d" => 2,
            _ => 3
        };
    }

    // ── Accounts ──────────────────────────────────────────────

    [RelayCommand]
    private void CreateAccountGroup()
    {
        var name = NewGroupName.Trim();
        if (string.IsNullOrWhiteSpace(name)) { Log("Enter a group name", LogLevel.Warning); return; }
        if (AccountGroups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Log("A group with that name already exists", LogLevel.Warning);
            return;
        }
        var group = new AccountGroup
        {
            Name = name,
            TradeUrl = NewGroupTradeUrl.Trim(),
            Proxy = ProxyHelper.Normalize(NewGroupProxy) ?? "",
            // walk the luminance ramp so adjacent groups never share a dot
            Color = AccountGroup.DotColor(AccountGroups.Count)
        };
        group.IsExpanded = false; // stay collapsed; expand card later to edit warehouse / link
        AccountGroups.Add(group);
        _groupStore.Save();
        NewGroupName = "";
        NewGroupTradeUrl = "";
        NewGroupProxy = "";
        SelectedAccountGroup = group;
        RefreshGroupSummaries();
        RebuildGroupRouteRows();
        Log($"Group created: {group.Name} — pick members, then expand the card anytime", LogLevel.Success);
        // Member picker opens as a panel (not by expanding the card).
        OpenGroupEditor(group);
    }

    [RelayCommand]
    private void ToggleGroupExpanded(AccountGroup? group)
    {
        if (group == null) return;
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded)
            RefreshGroupSummaries();
    }

    [RelayCommand]
    private void AssignSelectedToGroup(AccountGroup? group)
    {
        if (group == null) { Log("Select a group first", LogLevel.Warning); return; }
        var accounts = Accounts.Where(a => a.IsSelected).ToList();
        if (accounts.Count == 0) { Log("Select accounts first (Select all or click rows)", LogLevel.Warning); return; }
        _bulkUi = true;
        try
        {
            foreach (var account in accounts) account.GroupName = group.Name;
        }
        finally { _bulkUi = false; }
        _store.Save();
        RebuildAccountList();
        RefreshGroupSummaries();
        Log($"Assigned {accounts.Count} accounts → {group.Name}", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private void ClearSelectedGroup()
    {
        var accounts = Accounts.Where(a => a.IsSelected).ToList();
        if (accounts.Count == 0) { Log("Select accounts to ungroup", LogLevel.Warning); return; }
        foreach (var account in accounts) account.GroupName = null;
        _store.Save();
        RefreshGroupSummaries();
        Log($"Removed {accounts.Count} accounts from groups", LogLevel.Info);
    }

    // ── Warehouse ─────────────────────────────────────────────

    /// <summary>Guards WarehouseAccount setter against ComboBox re-entrancy.</summary>
    private bool _warehouseUiBusy;

    public SteamAccount? WarehouseAccount
    {
        get => Accounts.FirstOrDefault(a => a.IsWarehouse);
        set
        {
            if (_warehouseUiBusy) return;
            var current = Accounts.FirstOrDefault(a => a.IsWarehouse);
            if (ReferenceEquals(current, value))
            {
                if (value?.HasOwnTradeUrl == true && TradeUrl != value.OwnTradeUrl)
                    TradeUrl = value.OwnTradeUrl!;
                return;
            }

            _warehouseUiBusy = true;
            _bulkUi = true;
            try
            {
                foreach (var a in Accounts)
                    a.IsWarehouse = value != null && a == value;
                if (value?.HasOwnTradeUrl == true)
                    TradeUrl = value.OwnTradeUrl!;
                _store.Save();
            }
            finally
            {
                _bulkUi = false;
                _warehouseUiBusy = false;
            }
            RefreshWarehouseUiLight();
        }
    }
    public bool HasWarehouse => WarehouseAccount != null;

    public string WarehouseSummary
    {
        get
        {
            var w = WarehouseAccount;
            if (w == null) return T("No warehouse account", "Склад не выбран");
            return w.HasOwnTradeUrl
                ? $"{w.Login} · {T("link saved", "ссылка сохранена")}"
                : $"{w.Login} · {T("trade link missing", "нет трейд-ссылки")}";
        }
    }

    /// <summary>Where transfers currently land — the one line that answers "who gets my skins".</summary>
    public string RouteSummary
    {
        get
        {
            if (Settings.SendToWarehouse)
                return HasWarehouse
                    ? T($"→ warehouse {WarehouseAccount!.Login}", $"→ склад {WarehouseAccount!.Login}")
                    : T("→ warehouse (not set)", "→ склад (не выбран)");
            if (Settings.RouteTradesByGroup)
            {
                var ready = GroupRouteRows.Count(r => r.IsReady);
                var total = GroupRouteRows.Count;
                return total == 0
                    ? T("→ by group (no groups yet)", "→ по группам (групп нет)")
                    : T($"→ by group · {ready}/{total} destinations ready", $"→ по группам · {ready}/{total} направлений готовы");
            }
            return T("→ single trade link", "→ одна трейд-ссылка");
        }
    }

    /// <summary>Visible map of each group → where its skins go (Transfer page).</summary>
    public ObservableCollection<GroupRouteRow> GroupRouteRows { get; } = new();
    public bool ShowGroupRouteMap => !Settings.SendToWarehouse && Settings.RouteTradesByGroup;

    private void RebuildGroupRouteRows()
    {
        GroupRouteRows.Clear();
        foreach (var g in AccountGroups.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = Accounts.Count(a => string.Equals(a.GroupName, g.Name, StringComparison.OrdinalIgnoreCase));
            string dest;
            string status;
            bool ready;
            var wh = !string.IsNullOrWhiteSpace(g.WarehouseAccountId)
                ? Accounts.FirstOrDefault(a => a.Id == g.WarehouseAccountId)
                : null;
            if (wh != null)
            {
                dest = T($"Warehouse: {wh.Login}", $"Склад: {wh.Login}");
                if (wh.HasOwnTradeUrl)
                {
                    status = T("OK · trade link", "OK · трейд-ссылка");
                    ready = true;
                }
                else
                {
                    status = T("Need trade link on warehouse", "Нужна трейд-ссылка у склада");
                    ready = false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(g.TradeUrl))
            {
                dest = ShortTradeUrl(g.TradeUrl);
                status = T("Trade link", "Трейд-ссылка");
                ready = true;
            }
            else
            {
                dest = T("Not set", "Не задано");
                status = T("Missing destination", "Нет назначения");
                ready = false;
            }

            GroupRouteRows.Add(new GroupRouteRow
            {
                GroupName = g.Name,
                Color = g.Color ?? "#EDEDF0",
                AccountCount = members,
                Destination = dest,
                Status = status,
                IsReady = ready
            });
        }
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(ShowGroupRouteMap));
        OnPropertyChanged(nameof(GroupRouteRows));
    }

    private static string ShortTradeUrl(string url)
    {
        try
        {
            var info = SteamSession.ParseTradeUrl(url);
            return $"partner={info.PartnerAccountId} · token={info.Token}";
        }
        catch
        {
            return url.Length > 48 ? url[..48] + "…" : url;
        }
    }

    private void RefreshWarehouseUi()
    {
        RefreshWarehouseUiLight();
        // Candidates list only when accounts/maFiles change — not on every warehouse flag flip
        // (rebuilding ComboBox ItemsSource was clearing SelectedItem → null setter → crash loop).
        OnPropertyChanged(nameof(TransferWarehouseCandidates));
    }

    /// <summary>Update warehouse labels without touching ComboBox ItemsSource.</summary>
    private void RefreshWarehouseUiLight()
    {
        if (_warehouseUiBusy) return;
        OnPropertyChanged(nameof(WarehouseAccount));
        OnPropertyChanged(nameof(HasWarehouse));
        OnPropertyChanged(nameof(WarehouseSummary));
        OnPropertyChanged(nameof(RouteSummary));
        OnPropertyChanged(nameof(WarehouseIsConfigured));
        OnPropertyChanged(nameof(WarehouseGuardMessage));
        if (PendingTransferPlan != null) PendingTransferPlan = null;
    }

    /// <summary>
    /// Marks one account as the storage destination. Trade-link fetch is optional and never
    /// blocks marking; auto-fetch only when maFile is present.
    /// </summary>
    [RelayCommand]
    private async Task MarkWarehouseAsync(SteamAccount? account)
    {
        account ??= FocusedAccount ?? Accounts.FirstOrDefault(a => a.IsSelected);
        if (account == null) { Log("Pick an account first", LogLevel.Warning); return; }

        try
        {
            _warehouseUiBusy = true;
            _bulkUi = true;
            try
            {
                foreach (var a in Accounts)
                    a.IsWarehouse = a == account;
                _store.Save();
            }
            finally
            {
                _bulkUi = false;
                _warehouseUiBusy = false;
            }

            RefreshWarehouseUiLight();
            Log($"Warehouse: {account.Login}", LogLevel.Success);
            _sfx.Play(Sfx.Success);

            if (account.HasOwnTradeUrl)
            {
                TradeUrl = account.OwnTradeUrl!;
                Log($"{account.Login}: trade link already saved", LogLevel.Info);
                return;
            }

            if (!account.HasMaFile)
            {
                Log($"{account.Login}: marked as warehouse — add maFile, then press Fetch trade link", LogLevel.Warning);
                return;
            }

            // Fetch in background-safe path; never throw out of the command uncaught.
            await FetchWarehouseTradeLinkAsync(account);
        }
        catch (Exception ex)
        {
            Log($"Set warehouse failed: {ex.Message}", LogLevel.Error);
            SetBusy(null);
        }
    }

    /// <summary>
    /// Gets an account's own trade link through an authenticated Steam session,
    /// then validates that the link points back to that same account.
    /// Used for the global warehouse and for per-group warehouse destinations.
    /// </summary>
    /// <summary>Core: login + Econ.GetTradeOfferAccessToken (CM), HTML privacy page as fallback.</summary>
    private async Task<string> FetchTradeUrlForAccountAsync(SteamAccount account)
    {
        EnsureAndApplyHwid(account);
        var session = _sessions.TryGet(account.Id);
        if (session is not { IsOnline: true })
        {
            session = _sessions.GetOrCreate(account);
            await session.LoginAsync(new Progress<string>(m => SetBusy($"{account.Login}: {m}")));
            account.Status = AccountStatus.Online;
            if (!string.IsNullOrWhiteSpace(session.SteamId64))
                account.SteamId64 = session.SteamId64;
        }

        if (string.IsNullOrWhiteSpace(account.SteamId64) && !string.IsNullOrWhiteSpace(session.SteamId64))
            account.SteamId64 = session.SteamId64;
        if (string.IsNullOrWhiteSpace(account.SteamId64))
            throw new InvalidOperationException("SteamID64 missing after login");

        SetBusy($"{account.Login}: fetching trade link via Steam (Econ.GetTradeOfferAccessToken)…");
        var url = await session.GetOwnTradeUrlAsync();
        var parsed = SteamSession.ParseTradeUrl(url);
        // partner is accountid → steam64 of the owner; must match this account.
        if (!string.IsNullOrWhiteSpace(account.SteamId64) && parsed.PartnerSteam64 != account.SteamId64)
            throw new InvalidOperationException(
                $"Trade link partner {parsed.PartnerSteam64} does not match account {account.SteamId64}");
        return url;
    }

    [RelayCommand]
    private async Task FetchWarehouseTradeLinkAsync(SteamAccount? account)
    {
        account ??= WarehouseAccount ?? FocusedAccount;
        if (account == null) { Log("Choose a warehouse account first", LogLevel.Warning); return; }
        if (account.IsBlocked) { Log($"{account.Login}: blocked accounts cannot be used as warehouse", LogLevel.Error); return; }
        if (!account.HasMaFile) { Log($"{account.Login}: warehouse needs a maFile to sign in", LogLevel.Error); return; }

        try
        {
            SetBusy($"Warehouse {account.Login}: signing in…");
            var url = await FetchTradeUrlForAccountAsync(account);

            account.OwnTradeUrl = url;
            TradeUrl = url;
            if (FocusedAccount == account || ShowAccountPanel)
                EditWarehouseTradeUrl = url;
            foreach (var g in AccountGroups.Where(g => g.WarehouseAccountId == account.Id))
                g.TradeUrl = url;
            _store.Save();
            _groupStore.Save();
            RefreshWarehouseUiLight();
            RefreshGroupSummaries();
            Log($"{account.Login}: trade link fetched → filled in Transfer / editor fields", LogLevel.Success);
            _sfx.Play(Sfx.Success);
        }
        catch (Exception ex)
        {
            Log($"{account.Login}: could not get warehouse trade link — {ex.Message}", LogLevel.Error);
            Log("Tip: account needs maFile, network access, and trade URL enabled on Steam.", LogLevel.Warning);
        }
        finally { SetBusy(null); }
    }

    /// <summary>Pick a warehouse from Transfer page ComboBox and optionally fetch its trade link.</summary>
    [RelayCommand]
    private async Task SetTransferWarehouseAsync(SteamAccount? account)
    {
        if (account == null) return;
        try
        {
            Settings.SendToWarehouse = true;
            Settings.Save();
            WarehouseAccount = account; // safe setter (bulk + reentrancy guard)
            if (account.HasOwnTradeUrl)
            {
                TradeUrl = account.OwnTradeUrl!;
                Log($"Warehouse: {account.Login} · trade link ready", LogLevel.Success);
                return;
            }
            if (!account.HasMaFile)
            {
                Log($"{account.Login}: marked warehouse — need maFile to fetch trade link", LogLevel.Warning);
                return;
            }
            await FetchWarehouseTradeLinkAsync(account);
        }
        catch (Exception ex)
        {
            Log($"Set transfer warehouse failed: {ex.Message}", LogLevel.Error);
            SetBusy(null);
        }
    }

    [RelayCommand]
    private void ClearWarehouse()
    {
        WarehouseAccount = null;
        Settings.SendToWarehouse = false;
        Settings.Save();
        RefreshWarehouseUiLight();
        Log("Warehouse cleared", LogLevel.Info);
    }

    /// <summary>Selects every account except the warehouse — the warehouse must not send to itself.</summary>
    [RelayCommand]
    private void SelectAllSourcesForWarehouse()
    {
        if (!HasWarehouse) { Log("Mark a warehouse account first", LogLevel.Warning); return; }
        _bulkUi = true;
        try
        {
            foreach (var a in Accounts) a.IsSelected = !a.IsWarehouse && !a.IsBlocked;
        }
        finally { _bulkUi = false; }
        RebuildAccountList();
        RefreshFilter();
        Log($"Sources: {Accounts.Count(a => a.IsSelected)} accounts → {WarehouseAccount!.Login}", LogLevel.Info);
    }

    /// <summary>
    /// Chip click: selects the group, or clears it when the same group is clicked again.
    /// A filter you cannot switch off is a trap, so the chip toggles.
    /// </summary>
    [RelayCommand]
    private void SelectAccountGroup(AccountGroup? group)
    {
        if (group == null) return;
        if (SelectedAccountGroup == group) ClearGroupSelection();
        else ApplyGroupSelection(group);
        OnPropertyChanged(nameof(HwidSelectedGroupHint));
    }

    /// <summary>Unconditional select — used by scan/transfer flows that must not toggle off.</summary>
    private void ApplyGroupSelection(AccountGroup group)
    {
        SelectedAccountGroup = group;
        _bulkUi = true;
        try
        {
            foreach (var account in Accounts)
                account.IsSelected = string.Equals(account.GroupName, group.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally { _bulkUi = false; }
        RebuildAccountList();
        RefreshFilter();
        RecalcDashboardLight();
        Log($"Selected {Accounts.Count(a => a.IsSelected)} accounts from {group.Name}", LogLevel.Info);
    }

    [RelayCommand]
    private void ClearGroupSelection()
    {
        var name = SelectedAccountGroup?.Name;
        SelectedAccountGroup = null;
        _bulkUi = true;
        try
        {
            foreach (var account in Accounts) account.IsSelected = false;
        }
        finally { _bulkUi = false; }
        RebuildAccountList();
        RefreshFilter();
        RecalcDashboardLight();
        OnPropertyChanged(nameof(HwidSelectedGroupHint));
        if (name != null) Log($"Cleared selection from {name}", LogLevel.Info);
    }

    /// <summary>Point the proxy panel at a group without disturbing account selection.</summary>
    [RelayCommand]
    private void FocusProxyGroup(AccountGroup? group)
    {
        if (group == null) return;
        SelectedAccountGroup = SelectedAccountGroup == group ? null : group;
    }

    /// <summary>Select group members only (does not load inventory).</summary>
    [RelayCommand]
    private void SelectGroupOnly(AccountGroup? group)
    {
        if (group != null) ApplyGroupSelection(group);
    }

    [RelayCommand]
    private async Task ScanGroupAsync(AccountGroup? group)
    {
        if (group == null) { Log("Choose a group", LogLevel.Warning); return; }
        ApplyGroupSelection(group);
        if (Accounts.Count(a => a.IsSelected) == 0)
        {
            Log($"{group.Name}: no accounts — add some first", LogLevel.Warning);
            return;
        }
        await LoadInventoriesAsync();
    }

    [RelayCommand]
    private void TransferGroup(AccountGroup? group)
    {
        if (group == null) return;
        ApplyGroupSelection(group);
        // Multi-warehouse farms: each group routes to its own destination.
        Settings.RouteTradesByGroup = true;
        Settings.SendToWarehouse = false;
        Settings.Save();
        var groupWarehouse = !string.IsNullOrWhiteSpace(group.WarehouseAccountId)
            ? Accounts.FirstOrDefault(a => a.Id == group.WarehouseAccountId)
            : null;
        if (groupWarehouse?.HasOwnTradeUrl == true)
            TradeUrl = groupWarehouse.OwnTradeUrl!;
        else if (!string.IsNullOrWhiteSpace(group.TradeUrl))
            TradeUrl = group.TradeUrl;
        else
            Log($"{group.Name}: no group destination yet — set warehouse or paste a trade link", LogLevel.Warning);

        // Auto-pick tradable items for selected group members (skip warehouse itself).
        var selectedIds = Accounts.Where(a => a.IsSelected).Select(a => a.Id).ToHashSet();
        var whId = groupWarehouse?.Id;
        var n = 0;
        foreach (var item in Items)
        {
            var pick = selectedIds.Contains(item.AccountId)
                       && item.Tradable
                       && PassSmartRules(item)
                       && (whId == null || item.AccountId != whId);
            item.IsSelected = pick;
            if (pick) n++;
        }
        RecalcSelection();
        if (n == 0)
            Log($"{group.Name}: no tradable items loaded — Scan the group first", LogLevel.Warning);

        RefreshWarehouseUi();
        // Always open the transfer plan preview so multi-destination issues surface before send.
        OpenTransferReview();
        Log($"{group.Name}: transfer plan · {Accounts.Count(a => a.IsSelected)} acc · {n} items · route by group", LogLevel.Info);
    }

    [RelayCommand]
    private void SaveAccountGroup(AccountGroup? group)
    {
        if (group == null) return;
        var newName = group.Name.Trim();
        if (string.IsNullOrWhiteSpace(newName)) { Log("Group name cannot be empty", LogLevel.Error); return; }

        // Membership is stored as GroupName string on each account — renaming must re-stamp members.
        var oldName = _groupNameById.TryGetValue(group.Id, out var snap) ? snap : null;
        if (!string.IsNullOrWhiteSpace(oldName) &&
            !string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            if (AccountGroups.Any(g => g.Id != group.Id && g.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                Log($"Group name '{newName}' already exists", LogLevel.Error);
                group.Name = oldName!;
                return;
            }
            var moved = 0;
            foreach (var account in Accounts.Where(a => string.Equals(a.GroupName, oldName, StringComparison.OrdinalIgnoreCase)))
            {
                account.GroupName = newName;
                moved++;
            }
            if (moved > 0)
            {
                _store.Save();
                Log($"Renamed group '{oldName}' → '{newName}' · {moved} accounts updated", LogLevel.Info);
            }
        }

        group.Name = newName;
        group.TradeUrl = group.TradeUrl.Trim();
        group.Proxy = ProxyHelper.Normalize(group.Proxy) ?? "";
        group.ProxyPool = string.Join(Environment.NewLine, ProxyHelper.ParseLines(group.ProxyPool));
        _groupNameById[group.Id] = newName;
        _groupStore.Save();
        RefreshGroupSummaries();
        Log($"Group saved: {group.Name}", LogLevel.Success);
    }

    // ── Group membership editor ───────────────────────────────

    /// <summary>Group whose member list is open. Null closes the picker.</summary>
    [ObservableProperty] private AccountGroup? _editingGroup;
    [ObservableProperty] private string _groupMemberSearch = "";

    public ObservableCollection<GroupMemberRow> GroupMemberRows { get; } = [];
    public bool IsEditingGroup => EditingGroup != null;
    public int StagedMemberCount => GroupMemberRows.Count(r => r.IsMember);

    public string GroupEditorTitle => EditingGroup == null
        ? ""
        : T($"Accounts in {EditingGroup.Name}", $"Аккаунты в «{EditingGroup.Name}»");

    partial void OnEditingGroupChanged(AccountGroup? value)
    {
        OnPropertyChanged(nameof(IsEditingGroup));
        OnPropertyChanged(nameof(GroupEditorTitle));
    }

    partial void OnGroupMemberSearchChanged(string value) => RebuildGroupMemberRows();

    private void RebuildGroupMemberRows()
    {
        // Preserve ticks across a search: the staged set lives in the rows, and rebuilding
        // the list on every keystroke would otherwise silently discard the user's picks.
        var staged = GroupMemberRows.Where(r => r.IsMember).Select(r => r.Account.Id).ToHashSet();
        var unstaged = GroupMemberRows.Where(r => !r.IsMember).Select(r => r.Account.Id).ToHashSet();

        GroupMemberRows.Clear();
        var group = EditingGroup;
        if (group == null) { OnPropertyChanged(nameof(StagedMemberCount)); return; }

        var q = GroupMemberSearch.Trim();
        foreach (var a in Accounts)
        {
            if (q.Length > 0 &&
                !a.Login.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(a.PersonaName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                continue;

            var isMember = staged.Contains(a.Id)
                || (!unstaged.Contains(a.Id) && string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase));

            var other = string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase) ? null : a.GroupName;
            var row = new GroupMemberRow { Account = a, IsMember = isMember, OtherGroup = other };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GroupMemberRow.IsMember))
                    OnPropertyChanged(nameof(StagedMemberCount));
            };
            GroupMemberRows.Add(row);
        }
        OnPropertyChanged(nameof(StagedMemberCount));
    }

    [RelayCommand]
    private void OpenGroupEditor(AccountGroup? group)
    {
        if (group == null) return;
        if (!HasAccounts) { Log("Import accounts first", LogLevel.Warning); OpenImport(); return; }
        EditingGroup = group;
        GroupMemberSearch = "";
        RebuildGroupMemberRows();
    }

    [RelayCommand]
    private void CloseGroupEditor()
    {
        EditingGroup = null;
        GroupMemberRows.Clear();
        GroupMemberSearch = "";
    }

    [RelayCommand]
    private void ToggleGroupMember(GroupMemberRow? row)
    {
        if (row == null) return;
        row.IsMember = !row.IsMember;
    }

    [RelayCommand]
    private void StageAllGroupMembers()
    {
        var allOn = GroupMemberRows.Count > 0 && GroupMemberRows.All(r => r.IsMember);
        foreach (var r in GroupMemberRows) r.IsMember = !allOn;
        OnPropertyChanged(nameof(StagedMemberCount));
    }

    /// <summary>
    /// Commits the staged ticks: ticked accounts join, unticked ones that used to belong
    /// leave. Rows filtered out by the search are untouched, so searching narrows the edit
    /// instead of wiping members the user never looked at.
    /// </summary>
    [RelayCommand]
    private void SaveGroupMembers()
    {
        var group = EditingGroup;
        if (group == null) return;

        var added = 0;
        var removed = 0;
        _bulkUi = true;
        try
        {
            foreach (var row in GroupMemberRows)
            {
                var belongs = string.Equals(row.Account.GroupName, group.Name, StringComparison.OrdinalIgnoreCase);
                if (row.IsMember && !belongs) { row.Account.GroupName = group.Name; added++; }
                else if (!row.IsMember && belongs) { row.Account.GroupName = null; removed++; }
            }
        }
        finally { _bulkUi = false; }

        _store.Save();
        _groupStore.Save();
        RebuildAccountList();
        RefreshGroupSummaries();
        RebuildGroupRouteRows();
        CloseGroupEditor();
        // After saving members, collapse so the list of groups stays scannable.
        group.IsExpanded = false;
        Log($"{group.Name}: +{added} / −{removed} accounts", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private async Task LoadGroupInventoryAsync(AccountGroup? group)
    {
        if (group == null) return;
        await RunWithGroupSelectionAsync(group, LoadInventoriesAsync);
    }

    [RelayCommand]
    private async Task ReviewGroupAsync(AccountGroup? group)
    {
        if (group == null) return;
        await RunWithGroupSelectionAsync(group, ReviewSelectedAsync);
    }

    [RelayCommand]
    private void PrepareGroupTransfer(AccountGroup? group)
    {
        // Same path as Transfer — one consistent multi-warehouse flow.
        TransferGroup(group);
    }

    [RelayCommand]
    private void SetGroupWarehouse(AccountGroup? group)
    {
        if (group == null) return;
        // Prefer ComboBox picker — any account in the app, not only group members.
        SteamAccount? account = null;
        if (group.SelectedWarehouseOption is { AccountId.Length: > 0 } opt)
            account = Accounts.FirstOrDefault(a => a.Id == opt.AccountId);
        account ??= FocusedAccount;
        if (account == null)
        {
            Log("Pick a warehouse from the full account list in the dropdown", LogLevel.Warning);
            return;
        }
        group.WarehouseAccountId = account.Id;
        // Mirror verified trade URL onto the group field so the card shows the destination immediately.
        if (account.HasOwnTradeUrl)
            group.TradeUrl = account.OwnTradeUrl!;
        _groupStore.Save();
        RefreshGroupSummaries();
        RebuildGroupRouteRows();
        Log($"{group.Name}: warehouse = {account.Login}" +
            (string.Equals(account.GroupName, group.Name, StringComparison.OrdinalIgnoreCase) ? "" : " (outside group)"),
            LogLevel.Success);
        if (!account.HasOwnTradeUrl)
            _ = FetchWarehouseTradeLinkAsync(account);
        else
            Log($"{group.Name}: trade link ready for {account.Login}", LogLevel.Info);
    }

    /// <summary>Applies the ComboBox warehouse pick (including "none").</summary>
    [RelayCommand]
    private void ApplyGroupWarehousePicker(AccountGroup? group)
    {
        if (group == null) return;
        var opt = group.SelectedWarehouseOption;
        if (opt == null || string.IsNullOrWhiteSpace(opt.AccountId))
        {
            ClearGroupWarehouse(group);
            return;
        }
        SetGroupWarehouse(group);
    }

    [RelayCommand]
    private void ClearGroupWarehouse(AccountGroup? group)
    {
        if (group == null) return;
        group.WarehouseAccountId = null;
        group.SelectedWarehouseOption = group.WarehouseOptions.FirstOrDefault(o => string.IsNullOrEmpty(o.AccountId));
        _groupStore.Save();
        RefreshGroupSummaries();
        Log($"{group.Name}: warehouse removed", LogLevel.Info);
    }

    [RelayCommand]
    private void FetchGroupWarehouseTradeLink(AccountGroup? group)
    {
        if (group == null) return;
        // Apply picker first so "Fetch link" works without a separate Apply click.
        if (group.SelectedWarehouseOption is { AccountId.Length: > 0 } opt &&
            group.WarehouseAccountId != opt.AccountId)
            ApplyGroupWarehousePicker(group);

        var account = !string.IsNullOrWhiteSpace(group.WarehouseAccountId)
            ? Accounts.FirstOrDefault(a => a.Id == group.WarehouseAccountId)
            : null;
        if (account == null) { Log("Pick a warehouse in the dropdown first", LogLevel.Warning); return; }
        _ = FetchWarehouseTradeLinkAsync(account);
    }

    private async Task RunWithGroupSelectionAsync(AccountGroup group, Func<Task> action)
    {
        foreach (var account in Accounts)
            account.IsSelected = string.Equals(account.GroupName, group.Name, StringComparison.OrdinalIgnoreCase);
        RefreshFilter();
        await action();
    }

    [RelayCommand]
    private void SetGroupProxyMode(string? mode)
    {
        var group = SelectedAccountGroup;
        if (group == null) { Log("Choose a proxy group first", LogLevel.Warning); return; }
        group.ProxyAssignmentMode = mode is "Fixed" or "Random" ? mode : "Balanced";
        _groupStore.Save();
        Log($"{group.Name}: proxy policy = {group.ProxyPolicyText}", LogLevel.Info);
    }

    [RelayCommand]
    private void ApplySingleProxyToGroup(AccountGroup? group)
    {
        if (group == null) { Log("Select a group first", LogLevel.Warning); return; }
        var proxy = ProxyHelper.Normalize(group.Proxy);
        if (string.IsNullOrEmpty(proxy) || !ProxyHelper.IsValid(proxy))
        {
            Log("Enter a valid group proxy", LogLevel.Warning);
            return;
        }
        var members = Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0) { Log("This group has no accounts", LogLevel.Warning); return; }
        foreach (var account in members) account.Proxy = proxy;
        _store.Save();
        _groupStore.Save();
        RecalcDashboard();
        Log($"{group.Name}: one proxy assigned to {members.Count} accounts", LogLevel.Success);
    }

    [RelayCommand]
    private void DistributeGroupProxyPool(AccountGroup? group)
    {
        if (group == null) { Log("Select a group first", LogLevel.Warning); return; }
        var pool = ProxyHelper.ParseLines(group.ProxyPool);
        var members = Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pool.Count == 0) { Log("Add one proxy per line to the group pool", LogLevel.Warning); return; }
        if (members.Count == 0) { Log("This group has no accounts", LogLevel.Warning); return; }
        var orderedPool = group.ProxyAssignmentMode == "Random"
            ? pool.OrderBy(_ => Guid.NewGuid()).ToList()
            : pool;
        ProxyHelper.Distribute(members, orderedPool);
        _store.Save();
        _groupStore.Save();
        RecalcDashboard();
        Log($"{group.Name}: {pool.Count} proxies distributed across {members.Count} accounts · {group.ProxyPolicyText}", LogLevel.Success);
        _sfx.Play(Sfx.Done);
    }

    [RelayCommand]
    private void ClearGroupMemberProxies(AccountGroup? group)
    {
        if (group == null) return;
        var members = Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var account in members) account.Proxy = null;
        _store.Save();
        RecalcDashboard();
        Log($"{group.Name}: cleared proxies for {members.Count} accounts", LogLevel.Info);
    }

    [RelayCommand]
    private void DeleteAccountGroup(AccountGroup? group)
    {
        if (group == null) return;
        foreach (var account in Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)))
            account.GroupName = null;
        AccountGroups.Remove(group);
        if (SelectedAccountGroup == group) SelectedAccountGroup = null;
        _store.Save();
        _groupStore.Save();
        RefreshGroupSummaries();
        Log($"Group deleted: {group.Name}", LogLevel.Warning);
    }

    private void RefreshGroupSummaries()
    {
        foreach (var group in AccountGroups)
        {
            var members = Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            group.AccountCount = members.Count;
            group.PortfolioUsd = members.Sum(a => a.InventoryValue);
            group.InventoryLoadedCount = members.Count(a => a.InventoryCount > 0);
            group.ReadyCount = members.Count(a => a.CanTrade && a.InventoryCount > 0);
            group.AttentionCount = members.Count(a => a.IsBlocked || !a.HasMaFile || a.ProxyCheckOk == false);
            var ids = members.Select(a => a.Id).ToHashSet();
            var groupItems = Items.Where(i => ids.Contains(i.AccountId) && Counts(i)).ToList();
            group.ItemCount = groupItems.Sum(i => Math.Max(1, i.Amount));
            group.CaseCount = groupItems.Where(ItemClassifier.IsCase).Sum(i => Math.Max(1, i.Amount));
            group.SkinCount = groupItems.Where(i => !ItemClassifier.IsCase(i)).Sum(i => Math.Max(1, i.Amount));
            group.ProxyAssignedCount = members.Count(a => a.HasProxy);
            group.UniqueProxyCount = members.Where(a => a.HasProxy).Select(a => a.Proxy!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            group.MembersPreview = members.Count == 0
                ? "No accounts yet"
                : string.Join(", ", members.Take(8).Select(m => m.Login))
                  + (members.Count > 8 ? $" +{members.Count - 8}" : "");
            var wh = !string.IsNullOrWhiteSpace(group.WarehouseAccountId)
                ? Accounts.FirstOrDefault(a => a.Id == group.WarehouseAccountId)
                : null;
            group.WarehouseLogin = wh?.Login;
            group.WarehouseHasTradeUrl = wh?.HasOwnTradeUrl == true;

            // Warehouse picker: ANY account in the app (not only group members).
            group.WarehouseOptions.Clear();
            group.WarehouseOptions.Add(new GroupWarehouseOption
            {
                AccountId = "",
                Login = "",
                Label = "— pick warehouse account —",
                HasTradeUrl = false
            });
            // Prefer accounts with maFile (can fetch trade link), then the rest.
            foreach (var a in Accounts
                         .OrderByDescending(x => x.HasMaFile)
                         .ThenBy(x => x.Login, StringComparer.OrdinalIgnoreCase))
            {
                var inGroup = string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase);
                var bits = new List<string> { a.Login };
                if (a.HasOwnTradeUrl) bits.Add("link OK");
                else if (a.HasMaFile) bits.Add("needs link");
                else bits.Add("no maFile");
                if (inGroup) bits.Add("in group");
                group.WarehouseOptions.Add(new GroupWarehouseOption
                {
                    AccountId = a.Id,
                    Login = a.Login,
                    Label = string.Join(" · ", bits),
                    HasTradeUrl = a.HasOwnTradeUrl
                });
            }
            group.SelectedWarehouseOption = group.WarehouseOptions.FirstOrDefault(o => o.AccountId == (group.WarehouseAccountId ?? ""))
                                           ?? group.WarehouseOptions[0];

            _groupNameById[group.Id] = group.Name;
        }
    }

    [RelayCommand]
    private void ToggleAccount(SteamAccount acc)
    {
        acc.IsSelected = !acc.IsSelected;
        FocusedAccount = acc;
        RebuildHwidCompare(acc);
        // IsSelected → OnAccountPropertyChanged updates counts + inventory filter (no list rebuild).
        _sfx.Play(Sfx.Select, debounceClick: true);
    }

    [RelayCommand]
    private void SelectAllAccounts()
    {
        // Toggle selection on currently filtered list (search results), not entire 1000 if filtered
        var visible = FilteredAccounts.Count > 0 ? FilteredAccounts.ToList() : Accounts.ToList();
        var allOn = visible.Count > 0 && visible.All(a => a.IsSelected);
        _bulkUi = true;
        try
        {
            foreach (var a in visible) a.IsSelected = !allOn;
        }
        finally { _bulkUi = false; }
        // Do not RebuildAccountList — preserves avatars; selection is binding-driven.
        SelectedAccountCount = Accounts.Count(a => a.IsSelected);
        OnPropertyChanged(nameof(SelectedAccountCount));
        OnPropertyChanged(nameof(AccountsSubtitle));
        RefreshFilter();
        RecalcDashboardLight();
        Log(allOn ? "Selection cleared" : $"Selected {visible.Count} accounts", LogLevel.Info);
    }

    [RelayCommand]
    private void ClearAccountSelection()
    {
        _bulkUi = true;
        try
        {
            foreach (var a in Accounts) a.IsSelected = false;
        }
        finally { _bulkUi = false; }
        SelectedAccountCount = 0;
        OnPropertyChanged(nameof(SelectedAccountCount));
        OnPropertyChanged(nameof(AccountsSubtitle));
        RefreshFilter();
        RecalcDashboardLight();
    }

    /// <summary>Accounts eligible as warehouse destinations on Transfer (has maFile, not blocked).</summary>
    public List<SteamAccount> TransferWarehouseCandidates =>
        Accounts.Where(a => a.HasMaFile && !a.IsBlocked).OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand]
    private void SelectAllSourcesForTransfer()
    {
        var wh = WarehouseAccount;
        _bulkUi = true;
        try
        {
            foreach (var a in Accounts)
                a.IsSelected = !a.IsBlocked && a != wh;
        }
        finally { _bulkUi = false; }
        SelectedAccountCount = Accounts.Count(a => a.IsSelected);
        OnPropertyChanged(nameof(SelectedAccountCount));
        OnPropertyChanged(nameof(AccountsSubtitle));
        RefreshFilter();
        RecalcDashboardLight();
        Log($"Sources: {SelectedAccountCount} accounts", LogLevel.Info);
    }

    /// <summary>Select all tradable items on selected source accounts, then open send plan.</summary>
    [RelayCommand]
    private void PrepareAndSendTransfer()
    {
        var sources = Accounts.Where(a => a.IsSelected && !a.IsBlocked).ToList();
        if (sources.Count == 0) { Log("Select source accounts first", LogLevel.Warning); return; }
        if (Settings.SendToWarehouse && !WarehouseIsConfigured)
        {
            Log("Pick a warehouse account and fetch its trade link first", LogLevel.Warning);
            return;
        }
        if (!Settings.SendToWarehouse && string.IsNullOrWhiteSpace(TradeUrl) && !Settings.RouteTradesByGroup)
        {
            Log("Paste a trade link or enable warehouse / group routing", LogLevel.Warning);
            return;
        }

        var ids = sources.Select(a => a.Id).ToHashSet();
        var whId = Settings.SendToWarehouse ? WarehouseAccount?.Id : null;
        var n = 0;
        foreach (var item in Items)
        {
            var pick = ids.Contains(item.AccountId)
                       && item.Tradable
                       && PassSmartRules(item)
                       && (whId == null || item.AccountId != whId);
            item.IsSelected = pick;
            if (pick) n++;
        }
        RecalcSelection();
        if (n == 0)
        {
            Log("No tradable items loaded for selected sources — scan inventory first", LogLevel.Warning);
            return;
        }
        OpenTransferReview();
        Log($"Ready to send: {n} items from {sources.Count} accounts", LogLevel.Info);
    }

    [RelayCommand]
    private void ToggleItem(InventoryItem item)
    {
        if (!item.Tradable) { Log("Not tradable", LogLevel.Warning); return; }
        if (!PassSmartRules(item)) { Log("Excluded by smart rules", LogLevel.Warning); return; }
        item.IsSelected = !item.IsSelected;
        RecalcSelection();
    }

    /// <summary>Selects every non-blocked account, then reloads the item list for them.</summary>
    [RelayCommand]
    private async Task UseAllAccountsAsync()
    {
        if (!HasAccounts) { Log("Import accounts first", LogLevel.Warning); OpenImport(); return; }
        foreach (var a in Accounts) a.IsSelected = !a.IsBlocked;
        RebuildAccountList();
        Log($"Source: all {Accounts.Count(a => a.IsSelected)} usable accounts", LogLevel.Info);
        await LoadInventoriesAsync();
    }

    /// <summary>Scopes the inventory to one group and loads its members' items.</summary>
    [RelayCommand]
    private async Task UseGroupAccountsAsync(AccountGroup? group)
    {
        group ??= SelectedAccountGroup;
        if (group == null) { Log("Choose a group first", LogLevel.Warning); return; }
        ApplyGroupSelection(group);
        if (Accounts.Count(a => a.IsSelected) == 0)
        {
            Log($"{group.Name} has no accounts assigned yet", LogLevel.Warning);
            return;
        }
        await LoadInventoriesAsync();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var tradable = FilteredItems.Where(i => i.Tradable).ToList();
        var allOn = tradable.Count > 0 && tradable.All(i => i.IsSelected);
        foreach (var i in tradable) i.IsSelected = !allOn;
        RecalcSelection();
        Log(allOn ? "Selection cleared" : $"Selected {SelectedItemCount} items", LogLevel.Info);
    }

    /// <summary>Drops the selection across the whole item pool, not just the visible page.</summary>
    [RelayCommand]
    private void ClearItemSelection()
    {
        foreach (var i in Items) i.IsSelected = false;
        RecalcSelection();
    }

    [RelayCommand]
    private void SelectBySmartRules()
    {
        foreach (var i in FilteredItems.Where(i => i.Tradable && PassSmartRules(i)))
            i.IsSelected = true;
        RecalcSelection();
        Log($"Smart select: {SelectedItemCount} items", LogLevel.Success);
    }

    [RelayCommand]
    private void SelectDuplicates()
    {
        var groups = FilteredItems.Where(i => i.Tradable)
            .GroupBy(i => i.MarketHashName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        var n = 0;
        foreach (var g in groups)
        foreach (var i in g) { i.IsSelected = true; n++; }
        RecalcSelection();
        Log($"Duplicates selected: {n}", LogLevel.Info);
    }

    [RelayCommand]
    private void OpenImport()
    {
        ShowImport = true;
        _sfx.Play(Sfx.Panel);
    }

    [RelayCommand]
    private void CloseImport()
    {
        ShowImport = false;
        _sfx.Play(Sfx.Click, debounceClick: true);
    }

    [RelayCommand]
    private async Task BrowseLoginsAsync()
    {
        var path = await FileDialogs.OpenFileAsync("login:password", ("Text", new[] { "txt", "csv", "log", "*" }));
        if (path != null) LoginsPath = path;
    }

    [RelayCommand]
    private async Task BrowseMaDirAsync()
    {
        var path = await FileDialogs.OpenFolderAsync("maFiles");
        if (path != null) MaDir = path;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(LoginsPath)) { Log("Select a file", LogLevel.Error); return; }
        try
        {
            SetBusy("Importing…");
            var r = await Task.Run(() => _store.Import(LoginsPath, string.IsNullOrWhiteSpace(MaDir) ? null : MaDir));
            Log($"Import +{r.Imported} upd {r.Updated} ma {r.MaFilesFound}", LogLevel.Success);
            PushTimeline("↓", "Import", $"+{r.Imported} accounts");
            ShowImport = false;
            RecalcDashboard();
            _sfx.Play(Sfx.Success);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task LoginSelectedAsync()
    {
        var list = Accounts.Where(a => a.IsSelected && !a.IsBlocked).ToList();
        var blocked = Accounts.Count(a => a.IsSelected && a.IsBlocked);
        if (blocked > 0) Log($"Skipped {blocked} blocked accounts during login", LogLevel.Warning);
        if (list.Count == 0) { Log("No eligible accounts (all blocked or unselected)", LogLevel.Warning); return; }
        SetBusy($"Login 0/{list.Count}");
        var i = 0;
        foreach (var acc in list)
        {
            i++;
            SetBusy($"Login {i}/{list.Count}: {acc.Login}");
            acc.Status = AccountStatus.Connecting;
            try
            {
                if (string.IsNullOrEmpty(acc.SharedSecret)) throw new Exception("Missing maFile");
                EnsureAndApplyHwid(acc);
                var session = _sessions.GetOrCreate(acc);
                await session.LoginAsync(new Progress<string>(m => { acc.StatusText = m; Log($"{acc.Login}: {m}"); }));
                acc.Status = AccountStatus.Online;
                acc.StatusText = "online";
                acc.SteamId64 = session.SteamId64;
                Log($"{acc.Login}: online", LogLevel.Success);
                PushTimeline("●", acc.Login, "online");
                if (Settings.AutoAcceptEmptyIncoming)
                    _ = TryAutoAcceptIncoming(acc, session);
            }
            catch (Exception ex)
            {
                HandleAccountFailure(acc, ex.Message);
                Log($"{acc.Login}: {ex.Message} → continue", LogLevel.Error);
            }
            await Task.Delay(1000);
        }
        _store.Save();
        RecalcDashboard();
        SetBusy(null);
    }

    private async Task TryAutoAcceptIncoming(SteamAccount acc, SteamSession session)
    {
        try
        {
            var offers = await session.GetTradeOffersAsync();
            foreach (var o in offers.Where(x => x.IsIncoming && x.IsEmptyGive))
            {
                if (await session.AcceptTradeOfferAsync(o.OfferId))
                {
                    Log($"{acc.Login}: auto-accept empty #{o.OfferId}", LogLevel.Success);
                    _audit.Add("auto-accept", acc.Login, "empty incoming", o.OfferId);
                }
            }
        }
        catch { /* optional */ }
    }

    [RelayCommand]
    private async Task LoadInventoriesAsync()
    {
        var list = Accounts.Where(a => a.IsSelected && !a.IsBlocked).ToList();
        var blockedSel = Accounts.Count(a => a.IsSelected && a.IsBlocked);
        if (blockedSel > 0) Log($"Skipped {blockedSel} blocked accounts during load", LogLevel.Warning);
        if (list.Count == 0) { Log("Select eligible accounts (not VAC/blocked)", LogLevel.Warning); return; }

        _bulkUi = true;
        SetBusy($"Scan 0/{list.Count}");

        // Prices in background — re-apply when done so portfolio isn't stuck at $0 mid-scan.
        _ = Task.Run(async () =>
        {
            try
            {
                var count = await _prices.RefreshAsync();
                Dispatcher.UIThread.Post(() =>
                {
                    PriceStatus = $"prices · {count}";
                    // Always reprice; during bulk we still stamp prices on items already loaded.
                    RepriceAllItemsAndTotals();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Log($"Prices: {ex.Message}", LogLevel.Warning));
            }
        });

        var ct = _job.Start("inventory", list.Count);
        var delay = new AdaptiveDelay(Settings.BaseTradeDelayMs / 2);
        var loaded = 0;
        // Buffer items off UI thread; flush once per account (not per item)
        try
        {
            foreach (var acc in list)
            {
                await _job.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                _job.Current = acc.Login;
                SetBusy($"Scan {_job.Done + 1}/{list.Count}: {acc.Login}");
                PushQueueUi();
                acc.Status = AccountStatus.Busy;
                try
                {
                    // Always force fresh live fetch during inventory scan so new items and trade holds are fetched immediately
                    var inv = await LoadInventoryForAccountAsync(acc, forceRefresh: true, ct);
                    foreach (var it in inv)
                        it.Price = _prices.GetPrice(it.MarketHashName);

                    var ordered = inv.OrderByDescending(x => x.Price).ToList();
                    var value = ordered.Where(Counts).Sum(x => x.Price * Math.Max(1, x.Amount));

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // replace this account's items in one pass
                        for (var idx = Items.Count - 1; idx >= 0; idx--)
                            if (Items[idx].AccountId == acc.Id) Items.RemoveAt(idx);
                        foreach (var it in ordered) Items.Add(it);
                        acc.InventoryCount = ordered.Count;
                        acc.InventoryValue = value;
                        acc.InventoryScanned = true;
                        if (acc.Status != AccountStatus.Online) acc.Status = AccountStatus.Offline;
                        acc.StatusText = $"{ordered.Count} items";
                    }, DispatcherPriority.Background);

                    loaded += ordered.Count;
                    _job.Ok++;
                    Log($"{acc.Login}: {ordered.Count} · ${value:0.00}", LogLevel.Success);
                    delay.OnSuccess();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _job.Fail++;
                    acc.Status = AccountStatus.Error;
                    acc.StatusText = ex.Message;
                    Log($"{acc.Login}: {ex.Message}", LogLevel.Error);
                    delay.OnRateLimitOrError(ex.Message);
                }
                finally
                {
                    ReleaseSession(acc, "after inventory");
                    _job.Done++;
                    PushQueueUi();
                }
                await delay.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Inventory queue cancelled", LogLevel.Warning);
        }
        finally
        {
            _job.Finish();
            PushQueueUi();
            _bulkUi = false;
        }

        try
        {
            _store.Save();
            // Single end-of-scan UI refresh (was thrashing every account → freeze)
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Final price stamp + per-account values (fixes "scan again for full portfolio").
                RepriceAllItemsAndTotals();
                RebuildAccountList();
                RebuildHomeTopAccounts();
                try { _stats.RecordSnapshot(Accounts, _audit); } catch { /* non-fatal */ }
                RefreshStatsUi();
                RebuildGroupRouteRows();
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log($"Post-scan UI: {ex.Message}", LogLevel.Warning);
        }
        finally
        {
            SetBusy(null);
        }

        // Stay on Home after bulk scan — jumping to Inventory with 10k cards freezes UI
        if (ShellPage != (int)Models.ShellPage.Inventory)
            ShellPage = (int)Models.ShellPage.Home;
        Log($"Done: {loaded} items · live ${TotalPortfolio:0.00}", LogLevel.Success);
        if (loaded > 0) _sfx.Play(Sfx.Done);
    }

    /// <summary>
    /// Stamp latest market prices on every loaded item and recompute account + portfolio totals.
    /// Called after each scan and whenever the price catalog finishes refreshing.
    /// </summary>
    private void RepriceAllItemsAndTotals()
    {
        if (Items.Count == 0)
        {
            RecalcDashboardLight();
            return;
        }

        foreach (var it in Items)
            it.Price = _prices.GetPrice(it.MarketHashName);

        var byAcc = Items.GroupBy(i => i.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var acc in Accounts)
        {
            if (!byAcc.TryGetValue(acc.Id, out var list)) continue;
            acc.InventoryCount = list.Count;
            acc.InventoryValue = list.Where(Counts).Sum(x => x.Price * Math.Max(1, x.Amount));
            acc.InventoryScanned = true;
        }

        TotalPortfolio = Items.Where(Counts).Sum(i => i.Price * Math.Max(1, i.Amount));
        RefreshFilter();
        RecalcDashboardLight();
        RefreshGroupSummaries();
    }

    private async Task<List<InventoryItem>> LoadInventoryForAccountAsync(
        SteamAccount acc, bool forceRefresh, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromHours(Math.Max(1, Settings.InventoryCacheHours));
        if (!forceRefresh && Settings.PreferInventoryCache)
        {
            var cached = _invCache.TryLoad(acc.Id, acc.Login, maxAge);
            if (cached != null)
            {
                Log($"{acc.Login}: inv cache ({cached.Count})", LogLevel.Info);
                return cached;
            }
        }

        EnsureAndApplyHwid(acc);
        var session = _sessions.GetOrCreate(acc);
        List<InventoryItem> inv;
        try
        {
            if (string.IsNullOrEmpty(acc.SteamId64) && string.IsNullOrEmpty(session.SteamId64))
            {
                await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")), ct);
                acc.SteamId64 = session.SteamId64;
                acc.Status = AccountStatus.Online;
            }
            inv = await session.GetCs2InventoryAsync(ct);
        }
        catch (Exception invEx)
        {
            Log($"{acc.Login}: public inventory fetch failed ({invEx.Message}) → attempting session login…", LogLevel.Info);
            try
            {
                await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")), ct);
                acc.SteamId64 = session.SteamId64;
                acc.Status = AccountStatus.Online;
                inv = await session.GetCs2InventoryAsync(ct);
            }
            catch (Exception authEx)
            {
                Log($"{acc.Login}: live fetch failed ({authEx.Message}) — falling back to disk cache…", LogLevel.Warning);
                var cached = _invCache.TryLoad(acc.Id, acc.Login, TimeSpan.FromDays(365));
                if (cached != null && cached.Count > 0)
                {
                    Log($"{acc.Login}: restored {cached.Count} items from disk cache", LogLevel.Info);
                    return cached;
                }
                throw;
            }
        }

        inv.RemoveAll(i => i.IsPermanentlyUntradable);
        foreach (var it in inv)
            it.Price = _prices.GetPrice(it.MarketHashName);
        _invCache.Save(acc.Id, inv, acc.Login);
        return inv;
    }

    private void LoadCacheForAccounts()
    {
        try
        {
            var addedCount = 0;
            foreach (var acc in Accounts)
            {
                var cached = _invCache.TryLoad(acc.Id, acc.Login, TimeSpan.FromDays(365));
                if (cached != null && cached.Count > 0)
                {
                    cached.RemoveAll(i => i.IsPermanentlyUntradable);
                    foreach (var it in cached)
                        it.Price = _prices.GetPrice(it.MarketHashName);

                    for (var idx = Items.Count - 1; idx >= 0; idx--)
                        if (Items[idx].AccountId == acc.Id) Items.RemoveAt(idx);

                    foreach (var it in cached) Items.Add(it);
                    acc.InventoryCount = cached.Count;
                    acc.InventoryValue = cached.Sum(x => x.Price * Math.Max(1, x.Amount));
                    acc.InventoryScanned = true;
                    acc.StatusText = $"{cached.Count} items";
                    addedCount += cached.Count;
                }
            }
            if (addedCount > 0)
            {
                Log($"Loaded {addedCount} items from disk cache for {Accounts.Count} accounts", LogLevel.Info);
            }
            RecalcDashboard();
            RebuildAccountList();
            RefreshFilter();
        }
        catch { /* ignore cache boot errors */ }
    }

    [RelayCommand]
    private async Task SendTradesAsync()
    {
        _sfx.Play(Sfx.Click);
        OpenTransferReview();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DryRunTransferAsync()
    {
        PendingTransferPlan = BuildTransferPlan(dryRun: true);
        TransferPlanApproved = false;
        RebuildTransferPlanRows();
        ShowTransferReview = true;
        await Task.CompletedTask;
    }

    private TransferPlan BuildTransferPlan(bool dryRun)
    {
        var plan = new TransferPlan { IsDryRun = dryRun, Fingerprint = BuildTransferFingerprint() };
        var selected = Items.Where(i => i.IsSelected).ToList();
        var warehouse = Settings.SendToWarehouse ? WarehouseAccount : null;
        if (Settings.SendToWarehouse && !WarehouseIsConfigured)
        {
            plan.Issues.Add(new TransferPlanIssue { Message = "warehouse is not configured", AccountCount = 1 });
            return plan;
        }
        if (selected.Count == 0)
        {
            plan.Issues.Add(new TransferPlanIssue { Message = "no items selected", AccountCount = 0 });
            return plan;
        }

        var byAccount = selected.GroupBy(x => x.AccountId);
        foreach (var group in byAccount)
        {
            var account = Accounts.FirstOrDefault(a => a.Id == group.Key);
            var row = new TransferPlanAccount { AccountId = group.Key, Login = account?.Login ?? "Unknown", GroupName = account?.GroupName };
            plan.Accounts.Add(row);
            if (account == null) { row.State = "Skipped"; row.Reason = "Account missing"; continue; }
            if (account.IsBlocked) { row.State = "Skipped"; row.Reason = "Blocked"; continue; }
            if (warehouse?.Id == account.Id) { row.State = "Skipped"; row.Reason = "Warehouse cannot send to itself"; continue; }
            if (!account.HasMaFile) { row.State = "Skipped"; row.Reason = "No maFile"; continue; }

            var eligible = group.Where(x => x.Tradable && PassSmartRules(x)).ToList();
            if (eligible.Count == 0) { row.State = "Skipped"; row.Reason = "No eligible items"; continue; }
            var route = ResolveRouteForAccount(account, warehouse);
            if (string.IsNullOrWhiteSpace(route)) { row.State = "Issue"; row.Reason = "No trade link"; continue; }
            try
            {
                var info = SteamSession.ParseTradeUrl(route);
                if (!string.IsNullOrWhiteSpace(Settings.TrustedPartnerSteam64) && info.PartnerSteam64 != Settings.TrustedPartnerSteam64)
                    throw new InvalidOperationException("Destination is not in the trusted-partner allowlist");
                if (!string.IsNullOrWhiteSpace(account.SteamId64) && info.PartnerSteam64 == account.SteamId64)
                    throw new InvalidOperationException("Destination resolves to the source account");
                row.TradeUrl = route;
                row.DestinationSteam64 = info.PartnerSteam64;
                row.DestinationLabel = warehouse != null ? "Warehouse" : Settings.RouteTradesByGroup && !string.IsNullOrWhiteSpace(account.GroupName) ? "Group route" : "Default route";
                foreach (var item in eligible)
                    row.Items.Add(new TransferPlanItem { AssetId = item.AssetId, Amount = Math.Max(1, item.Amount), UnitPrice = item.Price });
                row.OfferCount = (int)Math.Ceiling(row.Items.Count / (double)Math.Max(1, Settings.MaxItemsPerOffer));
            }
            catch (Exception ex) { row.State = "Issue"; row.Reason = ex.Message; }
        }

        foreach (var issue in plan.Accounts.Where(x => !x.IsReady).GroupBy(x => x.Reason))
            plan.Issues.Add(new TransferPlanIssue { Message = issue.Key, AccountCount = issue.Count(), Severity = issue.Key == "No eligible items" ? "Warning" : "Error" });
        if (_sessionSentValue + plan.TotalValue > Settings.SessionValueLimit)
            plan.Issues.Add(new TransferPlanIssue { Message = $"exceeds session cap ${Settings.SessionValueLimit:0}", AccountCount = plan.SourceCount });
        return plan;
    }

    private string ResolveRouteForAccount(SteamAccount account, SteamAccount? warehouse) =>
        TransferRouting.ResolveRoute(
            account,
            warehouse,
            AccountGroups,
            Accounts,
            TradeUrl,
            Settings.DefaultTradeUrl,
            Settings.MainSinkMode,
            Settings.RouteTradesByGroup);

    private string BuildTransferFingerprint()
    {
        // Include per-group destinations so changing a group warehouse invalidates an open plan.
        var groupRoutes = string.Join(";",
            AccountGroups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var wh = !string.IsNullOrWhiteSpace(g.WarehouseAccountId)
                        ? Accounts.FirstOrDefault(a => a.Id == g.WarehouseAccountId)
                        : null;
                    var dest = wh?.OwnTradeUrl ?? g.TradeUrl ?? "";
                    return $"{g.Id}:{g.WarehouseAccountId}:{dest}";
                }));
        return string.Join("|", Settings.SendToWarehouse, WarehouseAccount?.Id, WarehouseAccount?.OwnTradeUrl,
            TradeUrl, Settings.DefaultTradeUrl, Settings.MainSinkMode, Settings.RouteTradesByGroup, Settings.TrustedPartnerSteam64,
            Settings.MaxItemsPerOffer, Settings.SessionValueLimit, Settings.MinPriceToSend, Settings.MaxPriceToSend,
            Settings.SkipTradeHoldItems, groupRoutes);
    }

    private async Task ExecuteTransferPlanAsync(TransferPlan plan)
    {
        if (plan.Fingerprint != BuildTransferFingerprint())
        {
            Log("Transfer settings changed after preview. Build a new plan.", LogLevel.Error);
            return;
        }
        if (plan.IsDryRun)
        {
            Log("Dry run: " + plan.Summary, LogLevel.Info);
            return;
        }
        await ExecuteTransferAsync(dryRun: false, plan);
    }

    private async Task ExecuteTransferAsync(bool dryRun, TransferPlan? approvedPlan = null)
    { 
        if (approvedPlan != null)
        {
            await ExecuteApprovedPlanAsync(approvedPlan);
            return;
        }
        // Warehouse wins over every other route: the user flagged one account as the place
        // everything ends up, and a silent fallback to a stale pasted link would scatter skins.
        var warehouse = Settings.SendToWarehouse ? WarehouseAccount : null;
        if (Settings.SendToWarehouse && warehouse == null)
        {
            Log("Warehouse mode is on but no account is marked as warehouse", LogLevel.Error);
            return;
        }
        if (warehouse != null && !warehouse.HasOwnTradeUrl)
        {
            Log($"{warehouse.Login}: warehouse has no trade link — capture or paste it on the Transfer page", LogLevel.Error);
            return;
        }

        var url = TradeUrl;
        if (Settings.MainSinkMode && !string.IsNullOrWhiteSpace(Settings.DefaultTradeUrl))
            url = Settings.DefaultTradeUrl;
        if (warehouse != null) url = warehouse.OwnTradeUrl!;
        if (string.IsNullOrWhiteSpace(url) && !Settings.RouteTradesByGroup)
        {
            Log("Trade link is empty", LogLevel.Error);
            return;
        }

        var selected = Items.Where(i => i.IsSelected && i.Tradable && PassSmartRules(i)).ToList();
        if (warehouse != null)
        {
            // The warehouse cannot trade with itself; drop its own items silently rather than
            // failing the whole batch when the user selected "all accounts".
            var own = selected.RemoveAll(i => i.AccountId == warehouse.Id);
            if (own > 0) Log($"Skipped {own} items on the warehouse account itself", LogLevel.Info);
        }
        if (selected.Count == 0) { Log("No items (holds/smart rules?)", LogLevel.Warning); return; }

        if (_sessionSentValue + selected.Sum(i => i.Price) > Settings.SessionValueLimit)
        {
            Log($"Session limit ${Settings.SessionValueLimit} (safe)", LogLevel.Error);
            return;
        }

        var selectedAccounts = selected.Select(i => i.AccountId).Distinct()
            .Select(id => Accounts.FirstOrDefault(a => a.Id == id)).Where(a => a != null).Cast<SteamAccount>().ToList();
        var routeByAccount = new Dictionary<string, string>();
        foreach (var account in selectedAccounts)
        {
            // Same resolution as transfer-plan path: global warehouse, else group warehouse / group trade URL, else default.
            var routeUrl = ResolveRouteForAccount(account, warehouse);
            if (string.IsNullOrWhiteSpace(routeUrl))
            {
                Log($"{account.Login}: no trade link assigned", LogLevel.Error);
                return;
            }
            routeByAccount[account.Id] = routeUrl;
        }

        foreach (var route in routeByAccount.Values.Distinct(StringComparer.Ordinal))
        {
            SteamSession.TradeUrlInfo info;
            try { info = SteamSession.ParseTradeUrl(route); }
            catch (Exception ex) { Log(ex.Message, LogLevel.Error); return; }
            if (!string.IsNullOrWhiteSpace(Settings.TrustedPartnerSteam64) &&
                !string.Equals(info.PartnerSteam64, Settings.TrustedPartnerSteam64, StringComparison.Ordinal))
            {
                Log("Partner is not in the whitelist (TrustedPartnerSteam64)", LogLevel.Error);
                return;
            }
        }

        ShellPage = (int)Models.ShellPage.Transfer;
        var groups = selected.GroupBy(i => i.AccountId).ToList();
        var destinations = routeByAccount.Values.Distinct(StringComparer.Ordinal).Count();
        Log($"{(dryRun ? "DRY-RUN" : "SEND")}: {selected.Count} items · {groups.Count} acc · {destinations} destination(s)", LogLevel.Warning);
        PushTimeline(dryRun ? "?" : "→", "Transfer", $"{selected.Count} items");

        if (dryRun)
        {
            foreach (var g in groups)
            {
                var acc = Accounts.First(a => a.Id == g.Key);
                var chunks = g.Chunk(Math.Max(1, Settings.MaxItemsPerOffer)).ToList();
                Log($"DRY {acc.Login}: {g.Count()} items · {chunks.Count} offers · ${g.Sum(x => x.Price):0.00}", LogLevel.Info);
            }
            return;
        }

        var ct = _job.Start("transfer", groups.Count);
        var delay = new AdaptiveDelay(Math.Max(1500, Settings.BaseTradeDelayMs));
        var betweenAcc = Math.Max(1500, Settings.BetweenAccountsDelayMs);
        try
        {
            foreach (var g in groups)
            {
                await _job.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                var acc = Accounts.FirstOrDefault(a => a.Id == g.Key);
                if (acc == null) { _job.Done++; continue; }

                _job.Current = acc.Login;
                PushQueueUi();

                if (acc.IsBlocked)
                {
                    _job.Skipped++;
                    _job.Done++;
                    Log($"SKIP blocked {acc.Login}", LogLevel.Warning);
                    _audit.Add("skip-ban", acc.Login, acc.BanReason ?? "blocked");
                    PushQueueUi();
                    continue;
                }

                var assetChunks = g.Select(x => x.AssetId).Chunk(Math.Max(1, Settings.MaxItemsPerOffer)).ToList();
                var attempts = 0;
                var accountDone = false;
                while (!accountDone && attempts < 2)
                {
                    attempts++;
                    try
                    {
                        EnsureAndApplyHwid(acc);
                        var session = _sessions.TryGet(acc.Id);
                        if (session is not { IsOnline: true })
                        {
                            session = _sessions.GetOrCreate(acc);
                            await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")), ct);
                            acc.Status = AccountStatus.Online;
                            acc.SteamId64 = session.SteamId64;
                        }
                        foreach (var chunk in assetChunks)
                        {
                            await _job.WaitIfPausedAsync(ct);
                            ct.ThrowIfCancellationRequested();
                            var chunkList = chunk.ToList();
                            var (offerId, status) = await session.SendTradeAsync(routeByAccount[acc.Id], chunkList,
                                new Progress<string>(m => Log($"{acc.Login}: {m}")), ct);
                            acc.FailStreak = 0;
                            acc.IsTempSkipped = false;
                            var chunkItems = g.Where(x => chunkList.Contains(x.AssetId)).ToList();
                            var val = chunkItems.Sum(x => x.Price);
                            _sessionSentValue += val;
                            _job.Ok++;
                            _job.ValueUsd += val;
                            await Dispatcher.UIThread.InvokeAsync(() => ApplyWithdrawal(acc, chunkItems));
                            _audit.Add("trade", acc.Login, $"{status} · {chunk.Length} items · −${val:0.00}", offerId, val);
                            _stats.RecordTradeEvent(true, chunk.Length, val);
                            Log($"{acc.Login}: #{offerId} · {status} · −${val:0.00}",
                                status == "confirmed" ? LogLevel.Success : LogLevel.Warning);
                            if (!Settings.WebhookOnlyFailsAndSummary)
                                await _webhooks.NotifyAsync(Settings.WebhookUrl, "Trade sent",
                                    $"{acc.Login} #{offerId} {status} −${val:0.00}");
                            delay.OnSuccess();
                            await delay.WaitAsync(ct);
                        }
                        accountDone = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (delay.IsRateLimit(ex.Message) && attempts < 2)
                    {
                        delay.OnRateLimitOrError(ex.Message);
                        Log($"{acc.Login}: rate-limit → cooldown {delay.RateLimitCooldownMs / 1000}s · retry",
                            LogLevel.Warning);
                        ReleaseSession(acc, "rate-limit cooldown");
                        await delay.WaitCooldownAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _job.Fail++;
                        if (!delay.IsRateLimit(ex.Message))
                            HandleAccountFailure(acc, ex.Message);
                        else
                        {
                            acc.IsTempSkipped = true;
                            acc.FailStreak++;
                        }
                        Log($"{acc.Login}: {ex.Message} → continue", LogLevel.Error);
                        _audit.Add("trade-fail", acc.Login, ex.Message);
                        _stats.RecordTradeEvent(false, 0, 0);
                        if (Settings.WebhookOnlyFailsAndSummary)
                            await _webhooks.NotifyAsync(Settings.WebhookUrl, "Trade FAIL",
                                $"{acc.Login}: {ex.Message}");
                        delay.OnRateLimitOrError(ex.Message);
                        if (ex.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                            TryReplaceDeadProxy(acc, acc.Proxy);
                        accountDone = true;
                    }
                }
                ReleaseSession(acc, "after trade");
                _job.Done++;
                PushQueueUi();
                RecalcDashboard();
                // extra pause between accounts (Steam hates bursts)
                await Task.Delay(betweenAcc + delay.CurrentMs / 3, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Transfer cancelled", LogLevel.Warning);
        }
        finally
        {
            _job.Finish();
            PushQueueUi();
        }

        RecalcSelection();
        RecalcDashboard();
        _stats.RecordSnapshot(Accounts, _audit);
        RefreshStatsUi();
        var summary =
            $"Transfer done: ok {_job.Ok} · fail {_job.Fail} · skip {_job.Skipped} · ${_job.ValueUsd:0.00} · out session ${_stats.SessionWithdrawnUsd:0.00}";
        Log(summary, LogLevel.Success);
        await _webhooks.NotifyAsync(Settings.WebhookUrl, "Transfer summary", summary);
        if (_job.Ok > 0) _sfx.Play(Sfx.Done);
        else if (_job.Fail > 0 || _job.Skipped > 0) _sfx.Play(Sfx.Error);
        SetBusy(null);
    }

    private async Task ExecuteApprovedPlanAsync(TransferPlan plan)
    {
        ShellPage = (int)Models.ShellPage.Transfer;
        var work = plan.ReadyAccounts.ToList();
        var ct = _job.Start("transfer", plan.OfferCount);
        var delay = new AdaptiveDelay(Math.Max(1500, Settings.BaseTradeDelayMs));
        _bulkUi = true;
        try
        {
            foreach (var row in work)
            {
                await _job.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                var account = Accounts.FirstOrDefault(a => a.Id == row.AccountId);
                if (account == null || account.IsBlocked)
                {
                    _job.Skipped += row.OfferCount;
                    _job.Done += row.OfferCount;
                    continue;
                }
                _job.Current = account.Login;
                PushQueueUi();
                try
                {
                    EnsureAndApplyHwid(account);
                    var session = _sessions.TryGet(account.Id);
                    if (session is not { IsOnline: true })
                    {
                        session = _sessions.GetOrCreate(account);
                        await session.LoginAsync(new Progress<string>(m => Log($"{account.Login}: {m}")), ct);
                        account.Status = AccountStatus.Online;
                        account.SteamId64 = session.SteamId64;
                    }
                    foreach (var offer in row.Items.Chunk(Math.Max(1, Settings.MaxItemsPerOffer)))
                    {
                        await _job.WaitIfPausedAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        var assetIds = offer.Select(x => x.AssetId).ToList();
                        var (offerId, status) = await session.SendTradeAsync(row.TradeUrl, assetIds,
                            new Progress<string>(m => Log($"{account.Login}: {m}")), ct);
                        var value = offer.Sum(x => x.TotalValue);
                        _sessionSentValue += value;
                        _job.Ok++; _job.Done++; _job.ValueUsd += value;
                        _audit.Add("trade", account.Login, $"{status} · {offer.Length} items · −${value:0.00}", offerId, value);
                        _stats.RecordTradeEvent(true, offer.Length, value);
                        if (status == "confirmed")
                        {
                            var ids = assetIds.ToHashSet();
                            await Dispatcher.UIThread.InvokeAsync(() => ApplyWithdrawal(account, Items.Where(x => x.AccountId == account.Id && ids.Contains(x.AssetId)).ToList()));
                        }
                        else
                        {
                            Log($"{account.Login}: #{offerId} pending confirmation — items stay reserved until confirmed", LogLevel.Warning);
                        }
                        delay.OnSuccess();
                        await delay.WaitAsync(ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _job.Fail++;
                    _job.Done += Math.Max(0, row.OfferCount - 1);
                    _audit.Add("trade-fail", account.Login, ex.Message);
                    Log($"{account.Login}: {ex.Message}", LogLevel.Error);
                    delay.OnRateLimitOrError(ex.Message);
                }
                finally
                {
                    ReleaseSession(account, "after planned transfer");
                    PushQueueUi();
                }
                await Task.Delay(Math.Max(1500, Settings.BetweenAccountsDelayMs), ct);
            }
        }
        catch (OperationCanceledException) { Log("Transfer cancelled. Unstarted plan rows were not sent.", LogLevel.Warning); }
        finally
        {
            _bulkUi = false;
            _job.Finish();
            PushQueueUi();
        }
        RecalcSelection();
        RecalcDashboard();
        _stats.RecordSnapshot(Accounts, _audit);
        var summary = $"Transfer done: ok {_job.Ok} · fail {_job.Fail} · skip {_job.Skipped} · ${_job.ValueUsd:0.00}";
        Log(summary, _job.Fail > 0 ? LogLevel.Warning : LogLevel.Success);
        if (_job.Ok > 0) _sfx.Play(Sfx.Done);
    }

    /// <summary>One-click: load inv (cache) → smart select → transfer → offline per acc.</summary>
    [RelayCommand]
    private async Task DrainSelectedAsync()
    {
        var list = Accounts.Where(a => a.IsSelected && !a.IsBlocked).ToList();
        if (list.Count == 0) { Log("Select accounts for Drain", LogLevel.Warning); return; }
        // Drain bypasses the smart price rules, so require an explicit confirmation.
        if (!_drainConfirmed)
        {
            AskConfirm("Drain every tradable item",
                $"All tradable items from {list.Count} account(s) will be sent, ignoring min/max price rules. Verify the destination first.",
                $"Drain {list.Count}",
                () => { _drainConfirmed = true; _ = DrainSelectedAsync(); });
            return;
        }
        _drainConfirmed = false;
        var url = TradeUrl;
        if (Settings.MainSinkMode && !string.IsNullOrWhiteSpace(Settings.DefaultTradeUrl))
            url = Settings.DefaultTradeUrl;
        if (string.IsNullOrWhiteSpace(url)) { Log("A trade link is required (or Main sink + default URL)", LogLevel.Error); return; }

        Log($"DRAIN start · {list.Count} acc", LogLevel.Warning);
        PushTimeline("⇢", "Drain", $"{list.Count} accounts");
        // 1) inventory
        await LoadInventoriesAsync();
        if (_job.Token.IsCancellationRequested) return;
        // 2) smart select ready items
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var it in Items)
            {
                if (!list.Any(a => a.Id == it.AccountId)) continue;
                it.IsSelected = PassSmartRules(it);
            }
            RefreshFilter();
            RecalcSelection();
        });
        var n = Items.Count(i => i.IsSelected);
        if (n == 0)
        {
            Log("Drain: no tradable/ready items", LogLevel.Warning);
            return;
        }
        Log($"Drain: selected {n} items → transfer", LogLevel.Info);
        // 3) transfer (with pause/cancel)
        await ExecuteTransferAsync(dryRun: Settings.SafeModeDryRun);
    }

    [RelayCommand]
    private async Task RefreshCs2ProgressAsync()
    {
        if (!HasAccounts) { Log("Import accounts first", LogLevel.Warning); OpenImport(); return; }
        EnsureSelectionOrAll();
        var list = Accounts.Where(a => a.IsSelected && !a.IsBlocked && a.HasMaFile).ToList();
        if (list.Count == 0)
        {
            Log("CS2 XP / weekly needs accounts with a maFile (login is required for GCPD)", LogLevel.Warning);
            return;
        }
        Log("CS2 XP/Weekly: login → GCPD + inventory history (best effort, not an in-game API)", LogLevel.Info);
        var ct = _job.Start("cs2-progress", list.Count);
        try
        {
            foreach (var acc in list)
            {
                await _job.WaitIfPausedAsync(ct);
                ct.ThrowIfCancellationRequested();
                _job.Current = acc.Login;
                PushQueueUi();
                try
                {
                    // HWID soft — never abort progress
                    EnsureAndApplyHwid(acc);
                    var session = _sessions.GetOrCreate(acc);
                    if (!session.IsOnline)
                    {
                        await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")), ct);
                        acc.SteamId64 = session.SteamId64;
                        acc.Status = AccountStatus.Online;
                    }
                    // Always force GCPD for XP/weekly
                    await _reviewSvc.ReviewAsync(acc, Settings.SteamWebApiKey, session,
                        includeGcpd: true, ct);
                    acc.OnReviewChanged();
                    _job.Ok++;
                    var r = acc.Review;
                    var gotXp = r?.Cs2Level >= 0 || r?.Cs2Xp >= 0;
                    Log($"{acc.Login}: {r?.Cs2XpText ?? "CS2 ?"} · {r?.WeeklyDropText ?? "Weekly ?"} · {r?.WeeklyDropNote}",
                        gotXp || r?.WeeklyDropClaimed != null ? LogLevel.Success : LogLevel.Warning);
                }
                catch (Exception ex)
                {
                    _job.Fail++;
                    Log($"{acc.Login}: XP/drop {ex.Message}", LogLevel.Error);
                }
                finally
                {
                    ReleaseSession(acc, "after cs2 progress");
                    _job.Done++;
                    PushQueueUi();
                }
                await Task.Delay(1200, ct);
            }
            _store.Save();
            ShellPage = (int)Models.ShellPage.Review;
        }
        catch (OperationCanceledException) { Log("CS2 progress cancelled", LogLevel.Warning); }
        finally
        {
            _job.Finish();
            PushQueueUi();
            SetBusy(null);
        }
        _sfx.Play(Sfx.Done);
    }

    /// <summary>
    /// After successful trade: drop items from UI list and subtract value from account portfolio.
    /// </summary>
    private void ApplyWithdrawal(SteamAccount acc, IReadOnlyList<InventoryItem> withdrawn)
    {
        if (withdrawn.Count == 0) return;
        var keys = withdrawn.Select(x => x.Key).ToHashSet();
        var val = withdrawn.Sum(x => x.Price);
        var count = withdrawn.Count;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (keys.Contains(Items[i].Key))
                Items.RemoveAt(i);
        }

        acc.InventoryValue = Math.Max(0, acc.InventoryValue - val);
        acc.InventoryCount = Math.Max(0, acc.InventoryCount - count);
        if (acc.InventoryCount == 0 && !Items.Any(x => x.AccountId == acc.Id))
            acc.InventoryCount = 0;

        RefreshFilter();
        RecalcDashboard();
        _store.Save();
    }

    /// <summary>
    /// Logoff + dispose Steam session so batch jobs (1000 acc) never pile up online sockets/threads.
    /// </summary>
    private void ReleaseSession(SteamAccount acc, string reason)
    {
        try
        {
            var had = _sessions.TryGet(acc.Id) is { IsOnline: true };
            _sessions.Release(acc.Id);
            // Keep Error/Blocked status visible; only clear live session states
            if (acc.Status is AccountStatus.Online or AccountStatus.Busy or AccountStatus.Connecting)
            {
                acc.Status = AccountStatus.Offline;
                acc.StatusText = "offline";
            }
            if (had)
                Log($"{acc.Login}: session closed · {reason}", LogLevel.Info);
        }
        catch
        {
            // never break the queue
        }
    }

    private void HandleAccountFailure(SteamAccount acc, string message)
    {
        acc.FailStreak++;
        if (BanHeuristics.LooksLikeHardBan(message) || acc.FailStreak >= 3)
        {
            acc.MarkBlocked(message);
            _sessions.Remove(acc.Id);
            Log($"⚑ {acc.Login} marked BLOCKED · {message}", LogLevel.Warning);
            _ = _webhooks.NotifyAsync(Settings.WebhookUrl, "Account blocked", $"{acc.Login}: {message}");
        }
        else if (BanHeuristics.LooksLikeBanOrBlock(message))
        {
            acc.IsTempSkipped = true;
            acc.Status = AccountStatus.Error;
            acc.StatusText = "temp skip";
        }
        else
        {
            acc.Status = AccountStatus.Error;
            acc.StatusText = message.Length > 40 ? message[..40] : message;
        }
        RebuildAccountList();
        _store.Save();
    }

    // ── VAC / ban management ──────────────────────────────────

    private void RefreshApiKeyStatus()
    {
        var k = Settings.SteamWebApiKey?.Trim() ?? "";
        ApiKeyStatus = string.IsNullOrEmpty(k)
            ? "API key: not set · VAC / Review WebAPI unavailable"
            : k.Length < 20
                ? "API key: too short — check the pasted value"
                : $"API key: OK ({k.Length} characters) · VAC / Review ready";
    }

    [RelayCommand]
    private void SaveApiKey()
    {
        Settings.SteamWebApiKey = Settings.SteamWebApiKey?.Trim() ?? "";
        Settings.Save();
        RefreshApiKeyStatus();
        Log(string.IsNullOrEmpty(Settings.SteamWebApiKey)
            ? "API key cleared"
            : "API key saved · VAC / Review WebAPI", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private void OpenSteamApiKeyPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://steamcommunity.com/dev/apikey",
                UseShellExecute = true
            });
            Log("Opened steamcommunity.com/dev/apikey — Domain: localhost → Register → paste the key here", LogLevel.Info);
            ShellPage = (int)Models.ShellPage.Settings;
        }
        catch (Exception ex)
        {
            Log("Could not open browser: " + ex.Message + " · open https://steamcommunity.com/dev/apikey manually", LogLevel.Error);
        }
    }

    [RelayCommand]
    private async Task CopyApiKeyGuideUrlAsync()
    {
        const string url = "https://steamcommunity.com/dev/apikey";
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                {
                    MainWindow: { Clipboard: { } clip }
                })
            {
                await clip.SetTextAsync(url);
                Log($"Copied: {url}", LogLevel.Success);
                return;
            }
        }
        catch { /* */ }
        Log(url, LogLevel.Info);
    }

    [RelayCommand]
    private async Task CheckVacAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SteamWebApiKey))
        {
            Log("VAC check requires a Steam Web API key → Settings · guide + Open key page", LogLevel.Error);
            ShellPage = (int)Models.ShellPage.Settings;
            return;
        }

        var list = Accounts.Where(a => a.IsSelected).ToList();
        if (list.Count == 0) list = Accounts.ToList();
        SetBusy($"VAC check 0/{list.Count}");
        try
        {
            await _reviewSvc.ReviewManyAsync(
                list,
                Settings.SteamWebApiKey,
                a => _sessions.TryGet(a.Id),
                includeGcpd: false,
                new Progress<string>(m => Log(m)));

            var vac = 0;
            foreach (var a in list)
            {
                a.OnReviewChanged();
                if (a.HasVac || a.IsBlocked)
                {
                    vac++;
                    a.IsSelected = false;
                }
            }
            _store.Save();
            RebuildAccountList();
            RecalcDashboard();
            RefreshReviewSummary();
            _stats.RecordSnapshot(Accounts, _audit);
            Log($"VAC check: {vac} blocked / {list.Count} · {ReviewSummaryLine}", vac > 0 ? LogLevel.Warning : LogLevel.Success);
            await _webhooks.NotifyAsync(Settings.WebhookUrl, "VAC check", $"{vac} blocked of {list.Count}");
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private void RemoveVacAccounts()
    {
        var toRemove = Accounts.Where(a => a.HasVac || a.IsMarkedBanned || a.IsBlocked).ToList();
        if (toRemove.Count == 0) { Log("No VAC/blocked accounts to remove", LogLevel.Info); return; }
        AskConfirm("Remove banned accounts",
            $"{toRemove.Count} flagged account(s) will be deleted from the list along with their cached items. This cannot be undone.",
            $"Remove {toRemove.Count}",
            () => RemoveVacAccountsCore(toRemove));
    }

    private void RemoveVacAccountsCore(List<SteamAccount> toRemove)
    {
        foreach (var a in toRemove)
        {
            _sessions.Remove(a.Id);
            // drop items
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].AccountId == a.Id) Items.RemoveAt(i);
            Accounts.Remove(a);
        }
        _store.Save();
        RefreshFilter();
        RebuildAccountList();
        RecalcDashboard();
        _stats.RecordSnapshot(Accounts, _audit);
        Log($"Removed {toRemove.Count} VAC/blocked accounts", LogLevel.Warning);
        PushTimeline("✕", "Cleanup", $"removed {toRemove.Count} banned");
    }

    [RelayCommand]
    private void RemoveSelectedAccounts()
    {
        var toRemove = Accounts.Where(a => a.IsSelected).ToList();
        if (toRemove.Count == 0) { Log("Nothing selected", LogLevel.Warning); return; }
        AskConfirm("Remove selected accounts",
            $"{toRemove.Count} selected account(s) will be deleted from the list. maFiles on disk are not touched.",
            $"Remove {toRemove.Count}",
            () => RemoveSelectedAccountsCore(toRemove));
    }

    private void RemoveSelectedAccountsCore(List<SteamAccount> toRemove)
    {
        foreach (var a in toRemove)
        {
            _sessions.Remove(a.Id);
            for (var i = Items.Count - 1; i >= 0; i--)
                if (Items[i].AccountId == a.Id) Items.RemoveAt(i);
            Accounts.Remove(a);
        }
        _store.Save();
        RefreshFilter();
        Log($"Removed {toRemove.Count} accounts", LogLevel.Info);
    }

    [RelayCommand]
    private void ClearAccountBlock(SteamAccount? acc)
    {
        acc ??= FocusedAccount;
        if (acc == null) return;
        acc.ClearBlock();
        _store.Save();
        RebuildAccountList();
        Log($"{acc.Login}: block cleared", LogLevel.Info);
    }

    // ── Confirmations / Incoming: explicit watch lists (no auto-check on tab open) ──

    /// <summary>Accounts to poll for mobile confirmations.</summary>
    public ObservableCollection<SteamAccount> ConfirmWatchList { get; } = new();
    /// <summary>Accounts to poll for incoming trade offers.</summary>
    public ObservableCollection<SteamAccount> IncomingWatchList { get; } = new();

    [ObservableProperty] private SteamAccount? _confirmPickAccount;
    [ObservableProperty] private SteamAccount? _incomingPickAccount;

    [ObservableProperty] private string _confirmNewLogin = "";
    [ObservableProperty] private string _confirmNewPassword = "";
    [ObservableProperty] private string _confirmNewMaPath = "";
    [ObservableProperty] private string _incomingNewLogin = "";
    [ObservableProperty] private string _incomingNewPassword = "";
    [ObservableProperty] private string _incomingNewMaPath = "";

    /// <summary>Existing accounts not yet on the confirm watch list.</summary>
    public IEnumerable<SteamAccount> ConfirmPickCandidates =>
        Accounts.Where(a => !ConfirmWatchList.Any(w => w.Id == a.Id))
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing accounts not yet on the incoming watch list.</summary>
    public IEnumerable<SteamAccount> IncomingPickCandidates =>
        Accounts.Where(a => !IncomingWatchList.Any(w => w.Id == a.Id))
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase);

    private void NotifyWatchLists()
    {
        OnPropertyChanged(nameof(ConfirmPickCandidates));
        OnPropertyChanged(nameof(IncomingPickCandidates));
        OnPropertyChanged(nameof(ConfirmWatchList));
        OnPropertyChanged(nameof(IncomingWatchList));
    }

    [RelayCommand]
    private void AddConfirmFromPick()
    {
        if (ConfirmPickAccount == null) { Log(T("Pick an account from the list", "Выбери аккаунт из списка"), LogLevel.Warning); return; }
        if (ConfirmWatchList.Any(a => a.Id == ConfirmPickAccount.Id)) return;
        ConfirmWatchList.Add(ConfirmPickAccount);
        ConfirmPickAccount = null;
        NotifyWatchLists();
        Log(T($"Added to confirm check: {ConfirmWatchList.Last().Login}", $"В проверку confirm: {ConfirmWatchList.Last().Login}"), LogLevel.Info);
    }

    [RelayCommand]
    private void RemoveConfirmWatch(SteamAccount? acc)
    {
        if (acc == null) return;
        ConfirmWatchList.Remove(acc);
        NotifyWatchLists();
    }

    [RelayCommand]
    private async Task BrowseConfirmNewMaFileAsync()
    {
        var path = await FileDialogs.OpenFileAsync("maFile", ("maFile", new[] { "mafile", "json" }));
        if (path != null) ConfirmNewMaPath = path;
    }

    [RelayCommand]
    private void AddConfirmNewAccount()
    {
        var acc = TryCreateWatchAccount(ConfirmNewLogin, ConfirmNewPassword, ConfirmNewMaPath);
        if (acc == null) return;
        if (!ConfirmWatchList.Any(a => a.Id == acc.Id))
            ConfirmWatchList.Add(acc);
        ConfirmNewLogin = "";
        ConfirmNewPassword = "";
        ConfirmNewMaPath = "";
        NotifyWatchLists();
        Log(T($"New account for confirm check: {acc.Login}", $"Новый аккаунт для confirm: {acc.Login}"), LogLevel.Success);
    }

    [RelayCommand]
    private void AddIncomingFromPick()
    {
        if (IncomingPickAccount == null) { Log(T("Pick an account from the list", "Выбери аккаунт из списка"), LogLevel.Warning); return; }
        if (IncomingWatchList.Any(a => a.Id == IncomingPickAccount.Id)) return;
        IncomingWatchList.Add(IncomingPickAccount);
        IncomingPickAccount = null;
        NotifyWatchLists();
        Log(T($"Added to incoming check: {IncomingWatchList.Last().Login}", $"В проверку входящих: {IncomingWatchList.Last().Login}"), LogLevel.Info);
    }

    [RelayCommand]
    private void RemoveIncomingWatch(SteamAccount? acc)
    {
        if (acc == null) return;
        IncomingWatchList.Remove(acc);
        NotifyWatchLists();
    }

    [RelayCommand]
    private async Task BrowseIncomingNewMaFileAsync()
    {
        var path = await FileDialogs.OpenFileAsync("maFile", ("maFile", new[] { "mafile", "json" }));
        if (path != null) IncomingNewMaPath = path;
    }

    [RelayCommand]
    private void AddIncomingNewAccount()
    {
        var acc = TryCreateWatchAccount(IncomingNewLogin, IncomingNewPassword, IncomingNewMaPath);
        if (acc == null) return;
        if (!IncomingWatchList.Any(a => a.Id == acc.Id))
            IncomingWatchList.Add(acc);
        IncomingNewLogin = "";
        IncomingNewPassword = "";
        IncomingNewMaPath = "";
        NotifyWatchLists();
        Log(T($"New account for incoming check: {acc.Login}", $"Новый аккаунт для входящих: {acc.Login}"), LogLevel.Success);
    }

    /// <summary>Create or reuse account in main store, attach maFile if path given.</summary>
    private SteamAccount? TryCreateWatchAccount(string loginRaw, string password, string maPath)
    {
        var login = (loginRaw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(login))
        {
            Log(T("Login is required", "Нужен логин"), LogLevel.Warning);
            return null;
        }

        var existing = Accounts.FirstOrDefault(a => a.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(password)) existing.Password = password;
            if (!string.IsNullOrWhiteSpace(maPath))
            {
                if (!TryApplyMaFile(existing, maPath, out var err))
                {
                    Log(err, LogLevel.Error);
                    return null;
                }
            }
            if (!existing.HasMaFile)
            {
                Log(T($"{existing.Login}: needs maFile for Guard / offers", $"{existing.Login}: нужен maFile"), LogLevel.Warning);
                return null;
            }
            _store.Save();
            return existing;
        }

        var acc = new SteamAccount { Login = login, Password = password ?? "" };
        if (!string.IsNullOrWhiteSpace(maPath))
        {
            if (!TryApplyMaFile(acc, maPath, out var err))
            {
                Log(err, LogLevel.Error);
                return null;
            }
        }
        if (!acc.HasMaFile)
        {
            Log(T("maFile is required for confirmations and trade offers", "Для confirm и офферов нужен maFile"), LogLevel.Warning);
            return null;
        }

        Accounts.Add(acc);
        _store.Save();
        RebuildAccountList();
        return acc;
    }

    private static bool TryApplyMaFile(SteamAccount acc, string path, out string error)
    {
        error = "";
        try
        {
            var raw = File.ReadAllText(path);
            var ma = MaFile.Parse(raw);
            if (ma == null || string.IsNullOrWhiteSpace(ma.SharedSecret) || string.IsNullOrWhiteSpace(ma.IdentitySecret))
            {
                error = "Invalid maFile (shared_secret / identity_secret missing)";
                return false;
            }
            acc.SharedSecret = ma.SharedSecret;
            acc.IdentitySecret = ma.IdentitySecret;
            acc.DeviceId = ma.DeviceId;
            acc.MaFilePath = path;
            acc.HasMaFile = true;
            if (string.IsNullOrWhiteSpace(acc.SteamId64))
                acc.SteamId64 = ma.Session?.SteamId > 0 ? ma.Session.SteamId.ToString() : ma.SteamId;
            return true;
        }
        catch (Exception ex)
        {
            error = "maFile: " + ex.Message;
            return false;
        }
    }

    // ── Confirmations ─────────────────────────────────────────

    [RelayCommand]
    private async Task LoadConfirmationsAsync()
    {
        if (IsBusy) return;
        var list = ConfirmWatchList.ToList();
        if (list.Count == 0)
        {
            Log(T("Add accounts to the confirm list, then press Check", "Добавь аккаунты в список и нажми Проверить"), LogLevel.Warning);
            return;
        }

        Confirmations.Clear();
        SetBusy($"Confirmations 0/{list.Count}…");
        var total = 0;
        try
        {
            for (var i = 0; i < list.Count; i++)
            {
                var acc = list[i];
                SetBusy($"Confirmations {i + 1}/{list.Count}: {acc.Login}");
                try
                {
                    if (!acc.HasMaFile)
                    {
                        Log($"{acc.Login}: no maFile — skip", LogLevel.Warning);
                        continue;
                    }
                    EnsureAndApplyHwid(acc);
                    var s = _sessions.GetOrCreate(acc);
                    if (s is not { IsOnline: true })
                    {
                        await s.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")));
                        acc.Status = AccountStatus.Online;
                        if (!string.IsNullOrWhiteSpace(s.SteamId64))
                            acc.SteamId64 = s.SteamId64;
                    }
                    var confs = await s.GetConfirmationsAsync();
                    foreach (var c in confs) Confirmations.Add(c);
                    total += confs.Count;
                    Log($"{acc.Login}: {confs.Count} confirmation(s)", confs.Count > 0 ? LogLevel.Success : LogLevel.Info);
                }
                catch (Exception ex) { Log($"{acc.Login}: conf {ex.Message}", LogLevel.Warning); }
            }
        }
        finally { SetBusy(null); }

        Log(T($"Confirmations: {total}", $"Подтверждения: {total}"), total > 0 ? LogLevel.Success : LogLevel.Info);
        if (total > 0) _sfx.Play(Sfx.Done);
    }

    [RelayCommand]
    private async Task AcceptSelectedConfirmationsAsync()
    {
        var list = Confirmations.Where(c => c.IsSelected).ToList();
        if (list.Count == 0)
        {
            Log(T("Select confirmations first", "Сначала отметь подтверждения"), LogLevel.Warning);
            return;
        }
        SetBusy($"Accept {list.Count}…");
        foreach (var c in list)
        {
            try
            {
                var acc = Accounts.FirstOrDefault(a => a.Id == c.AccountId);
                var s = _sessions.TryGet(c.AccountId);
                if (s is not { IsOnline: true } && acc != null)
                {
                    s = _sessions.GetOrCreate(acc);
                    await s.LoginAsync();
                }
                if (s == null) continue;
                if (await s.RespondConfirmationAsync(c.ConfId, c.Key, true))
                {
                    Log($"{c.AccountLogin}: conf OK {c.Headline}", LogLevel.Success);
                    _audit.Add("conf-accept", c.AccountLogin, c.Headline);
                    Confirmations.Remove(c);
                }
            }
            catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        }
        SetBusy(null);
    }

    // ── Incoming trades ───────────────────────────────────────

    public int IncomingOfferCount => TradeOffers.Count(o => o.IsIncoming);
    public bool HasIncomingOffers => IncomingOfferCount > 0;
    public string IncomingEmptyHint =>
        IncomingOfferCount > 0
            ? ""
            : IncomingWatchList.Count == 0
                ? T("Add accounts above, then press Check.", "Добавь аккаунты выше и нажми Проверить.")
                : T("No active incoming offers. Send a trade first, then Check again.",
                    "Нет активных входящих. Отправь трейд, затем Проверить снова.");

    [RelayCommand]
    private async Task LoadTradeOffersAsync()
    {
        if (IsBusy) return;
        TradeOffers.Clear();
        OnPropertyChanged(nameof(IncomingOfferCount));
        OnPropertyChanged(nameof(HasIncomingOffers));
        OnPropertyChanged(nameof(IncomingEmptyHint));

        var targets = IncomingWatchList.ToList();
        if (targets.Count == 0)
        {
            Log(T("Add accounts to the incoming list, then press Check", "Добавь аккаунты во входящие и нажми Проверить"), LogLevel.Warning);
            return;
        }

        SetBusy($"Incoming 0/{targets.Count}…");
        var total = 0;
        var errors = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var acc = targets[i];
                SetBusy($"Incoming {i + 1}/{targets.Count}: {acc.Login}");
                try
                {
                    if (!acc.HasMaFile)
                    {
                        Log($"{acc.Login}: no maFile — skip", LogLevel.Warning);
                        continue;
                    }
                    EnsureAndApplyHwid(acc);
                    var session = _sessions.GetOrCreate(acc);
                    if (!session.IsOnline)
                    {
                        await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")));
                        acc.Status = AccountStatus.Online;
                        if (!string.IsNullOrWhiteSpace(session.SteamId64))
                            acc.SteamId64 = session.SteamId64;
                    }

                    var offers = await session.GetTradeOffersAsync();
                    var incoming = offers.Where(o => o.IsIncoming).ToList();
                    foreach (var o in incoming)
                    {
                        ClassifyTradeOffer(o);
                        TradeOffers.Add(o);
                    }
                    total += incoming.Count;
                    Log($"{acc.Login}: {incoming.Count} incoming",
                        incoming.Count > 0 ? LogLevel.Success : LogLevel.Info);
                }
                catch (Exception ex)
                {
                    errors++;
                    Log($"{acc.Login}: offers — {ex.Message}", LogLevel.Warning);
                }
            }
        }
        finally
        {
            SetBusy(null);
            OnPropertyChanged(nameof(IncomingOfferCount));
            OnPropertyChanged(nameof(HasIncomingOffers));
            OnPropertyChanged(nameof(IncomingEmptyHint));
        }

        Log(T(
            $"Incoming: {total} offer(s) on {targets.Count} account(s)" + (errors > 0 ? $" · {errors} error(s)" : ""),
            $"Входящие: {total} на {targets.Count} акк." + (errors > 0 ? $" · ошибок {errors}" : "")),
            total > 0 ? LogLevel.Success : LogLevel.Info);
        if (total > 0) _sfx.Play(Sfx.Done);
    }

    private void ClassifyTradeOffer(TradeOfferItem offer)
    {
        offer.IsTrustedPartner = !string.IsNullOrWhiteSpace(Settings.TrustedPartnerSteam64)
            && offer.PartnerSteam64 == Settings.TrustedPartnerSteam64;
        if (!offer.IsIncoming) { offer.SafetyLabel = "Outgoing"; offer.SafetyReason = "Sent by this account."; return; }
        if (offer.MyItems > 0)
        {
            offer.SafetyLabel = offer.IsTrustedPartner ? "Trusted, but gives items" : "Risk: gives items";
            offer.SafetyReason = "Accept only after you verify the partner and every item in Steam.";
        }
        else if (offer.IsTrustedPartner || offer.IsEmptyGive)
        {
            // Warehouse receive: partner sends items, we give nothing — normal farm flow.
            offer.SafetyLabel = offer.IsEmptyGive ? "Receive only (safe for warehouse)" : "Trusted receive-only";
            offer.SafetyReason = offer.IsEmptyGive
                ? "They send items · you give nothing. Typical warehouse deposit."
                : "Partner is on your allowlist; still confirm the offer in Steam.";
        }
        else
        {
            offer.SafetyLabel = "Unknown partner";
            offer.SafetyReason = "Not on the trusted-partner allowlist.";
        }
    }

    [RelayCommand]
    private void AcceptOffer(TradeOfferItem? offer)
    {
        if (offer == null || !offer.IsIncoming) return;
        // Receive-only (warehouse deposit): accept without extra confirm spam
        if (offer.IsEmptyGive)
        {
            _ = AcceptOfferCoreAsync(offer);
            return;
        }
        var warning = $"Partner: {offer.PartnerShort}\n{offer.Summary}\n{offer.SafetyReason}";
        AskConfirm("Accept incoming offer", warning, "Accept offer", () => _ = AcceptOfferCoreAsync(offer));
    }

    private async Task AcceptOfferCoreAsync(TradeOfferItem offer)
    {
        try
        {
            SetBusy($"{offer.AccountLogin}: accept #{offer.OfferId}…");
            var acc = Accounts.FirstOrDefault(a => a.Id == offer.AccountId)
                      ?? throw new Exception("Account missing");
            EnsureAndApplyHwid(acc);
            var s = _sessions.TryGet(offer.AccountId);
            if (s is not { IsOnline: true })
            {
                s = _sessions.GetOrCreate(acc);
                await s.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")));
                acc.Status = AccountStatus.Online;
            }
            if (await s.AcceptTradeOfferAsync(offer.OfferId, offer.PartnerSteam64))
            {
                Log($"{offer.AccountLogin}: accepted #{offer.OfferId}", LogLevel.Success);
                _audit.Add("accept", offer.AccountLogin, $"{offer.SafetyLabel} · {offer.Summary}", offer.OfferId);
                TradeOffers.Remove(offer);
                OnPropertyChanged(nameof(IncomingOfferCount));
                OnPropertyChanged(nameof(HasIncomingOffers));
                _sfx.Play(Sfx.Success);
            }
            else
            {
                Log($"{offer.AccountLogin}: accept failed for #{offer.OfferId}", LogLevel.Error);
            }
        }
        catch (Exception ex) { Log($"{offer.AccountLogin}: {ex.Message}", LogLevel.Error); }
        finally { SetBusy(null); }
    }

    /// <summary>Accept every incoming receive-only offer (warehouse deposits). Risky ones need manual review.</summary>
    [RelayCommand]
    private void AcceptAllIncoming()
    {
        var safe = TradeOffers.Where(o => o.IsIncoming && o.IsEmptyGive).ToList();
        var risky = TradeOffers.Count(o => o.IsIncoming && !o.IsEmptyGive);
        if (safe.Count == 0)
        {
            Log(risky > 0
                ? $"{risky} offer(s) give away your items — accept them one by one after review"
                : "No receive-only incoming offers to accept", LogLevel.Warning);
            return;
        }

        AskConfirm(
            T("Accept all deposits?", "Принять все депозиты?"),
            T(
                $"Accept {safe.Count} receive-only offer(s) on warehouse account(s)."
                + (risky > 0 ? $"\n{risky} other offer(s) that take items from you are left for manual review." : ""),
                $"Принять {safe.Count} входящих (только получение) на складе."
                + (risky > 0 ? $"\n{risky} оффер(ов), где вы отдаёте предметы — только вручную." : "")),
            T($"Accept {safe.Count}", $"Принять {safe.Count}"),
            () => _ = AcceptAllIncomingCoreAsync(safe));
    }

    private async Task AcceptAllIncomingCoreAsync(List<TradeOfferItem> offers)
    {
        var ok = 0;
        var fail = 0;
        foreach (var o in offers.ToList())
        {
            var before = TradeOffers.Contains(o);
            await AcceptOfferCoreAsync(o);
            if (!TradeOffers.Contains(o) && before) ok++;
            else if (TradeOffers.Contains(o)) fail++;
        }
        Log($"Accept all: OK {ok} · fail {fail}", ok > 0 ? LogLevel.Success : LogLevel.Error);
        if (ok > 0) _sfx.Play(Sfx.Done);
    }

    [RelayCommand]
    private void DeclineOffer(TradeOfferItem? offer)
    {
        if (offer == null || !offer.IsIncoming) return;
        AskConfirm("Decline incoming offer", $"Partner: {offer.PartnerShort}\n{offer.Summary}", "Decline offer", () => _ = DeclineOfferCoreAsync(offer));
    }

    private async Task DeclineOfferCoreAsync(TradeOfferItem offer)
    {
        try
        {
            SetBusy($"{offer.AccountLogin}: decline…");
            var acc = Accounts.FirstOrDefault(a => a.Id == offer.AccountId)
                      ?? throw new Exception("Account missing");
            EnsureAndApplyHwid(acc);
            var s = _sessions.TryGet(offer.AccountId);
            if (s is not { IsOnline: true })
            {
                s = _sessions.GetOrCreate(acc);
                await s.LoginAsync();
                acc.Status = AccountStatus.Online;
            }
            await s.DeclineTradeOfferAsync(offer.OfferId);
            TradeOffers.Remove(offer);
            OnPropertyChanged(nameof(IncomingOfferCount));
            OnPropertyChanged(nameof(HasIncomingOffers));
            Log($"Declined #{offer.OfferId}", LogLevel.Info);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task FetchOwnTradeUrlAsync()
    {
        var acc = FocusedAccount ?? Accounts.FirstOrDefault(a => a.IsSelected && a.Status == AccountStatus.Online)
                  ?? Accounts.FirstOrDefault(a => a.Status == AccountStatus.Online);
        if (acc == null) { Log("An online account is required", LogLevel.Warning); return; }
        try
        {
            SetBusy("Trade URL…");
            var s = _sessions.TryGet(acc.Id) ?? throw new Exception("offline");
            var url = await s.GetOwnTradeUrlAsync();
            TradeUrl = url;
            Settings.DefaultTradeUrl = url;
            Settings.Save();
            await SetClipboard(url);
            Log($"{acc.Login}: trade URL → clipboard", LogLevel.Success);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    // ── Review / HWID (existing) ──────────────────────────────

    [RelayCommand]
    private async Task ReviewSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SteamWebApiKey))
        {
            Log("VAC check requires a Steam Web API key → Settings", LogLevel.Warning);
            ShellPage = (int)Models.ShellPage.Settings;
            return;
        }
        var list = Accounts.Where(a => a.IsSelected).ToList();
        if (list.Count == 0) list = Accounts.ToList();
        SetBusy("Review…");
        foreach (var acc in list)
        {
            try
            {
                await _reviewSvc.ReviewAsync(acc, Settings.SteamWebApiKey, _sessions.TryGet(acc.Id), Settings.IncludeCs2GcpdInReview);
                acc.OnReviewChanged();
                if (acc.IsBlocked) acc.IsSelected = false;
                if (acc.Review?.BanChanged == true)
                {
                    Log($"BAN CHANGE {acc.Login}", LogLevel.Error);
                    await _webhooks.NotifyAsync(Settings.WebhookUrl, "Ban change", $"{acc.Login}: {acc.Review.BadgeSummary}");
                }
                else Log($"{acc.Login}: {acc.Review?.BadgeSummary}", LogLevel.Success);
            }
            catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        }
        _store.Save();
        RecalcDashboard();
        RefreshReviewSummary();
        ShellPage = (int)Models.ShellPage.Review;
        Log(ReviewSummaryLine, ReviewBannedCount > 0 ? LogLevel.Warning : LogLevel.Success);
        SetBusy(null);
    }

    [RelayCommand]
    private async Task ReviewAllAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.SteamWebApiKey))
        {
            Log("VAC check requires a Steam Web API key → Settings", LogLevel.Warning);
            ShellPage = (int)Models.ShellPage.Settings;
            return;
        }
        SetBusy("Review all…");
        try
        {
            await _reviewSvc.ReviewManyAsync(Accounts, Settings.SteamWebApiKey, a => _sessions.TryGet(a.Id),
                Settings.IncludeCs2GcpdInReview, new Progress<string>(m => Log(m)));
            foreach (var acc in Accounts)
            {
                acc.OnReviewChanged();
                if (acc.IsBlocked) acc.IsSelected = false;
            }
            _store.Save();
            RebuildAccountList();
            RecalcDashboard();
            RefreshReviewSummary();
            ShellPage = (int)Models.ShellPage.Review;
            Log(ReviewSummaryLine, ReviewBannedCount > 0 ? LogLevel.Warning : LogLevel.Success);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    private void RefreshReviewSummary()
    {
        OnPropertyChanged(nameof(ReviewCleanCount));
        OnPropertyChanged(nameof(ReviewBannedCount));
        OnPropertyChanged(nameof(ReviewUncheckedCount));
        OnPropertyChanged(nameof(ReviewSummaryLine));
    }

    [RelayCommand]
    private void GenerateHwidForSelected()
    {
        // Selected checkboxes on Accounts / Device IDs list.
        var list = Accounts.Where(a => a.IsSelected).ToList();
        if (list.Count == 0)
        {
            Log("Select one or more accounts first (checkbox / click rows), then press this button", LogLevel.Warning);
            return;
        }
        RegenerateHwidProfiles(list, "selected");
    }

    [RelayCommand]
    private void GenerateHwidForGroup(AccountGroup? group)
    {
        group ??= SelectedAccountGroup;
        if (group == null)
        {
            Log("Select a group on Accounts or Groups first, then regenerate that group's device IDs", LogLevel.Warning);
            return;
        }
        var members = Accounts.Where(a => string.Equals(a.GroupName, group.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0)
        {
            Log($"Group «{group.Name}» has no accounts", LogLevel.Warning);
            return;
        }
        RegenerateHwidProfiles(members, group.Name);
    }

    [RelayCommand]
    private void GenerateHwidForAll()
    {
        if (Accounts.Count == 0) { Log("No accounts imported", LogLevel.Warning); return; }
        AskConfirm("Regenerate all device profiles",
            $"A new device profile will be created for all {Accounts.Count} accounts.\nWindows hardware is not changed until an account logs in (admin for MachineGuid).",
            "Regenerate all", () => RegenerateHwidProfiles(Accounts.ToList(), "all"));
    }

    private void RegenerateHwidProfiles(List<SteamAccount> list, string scope)
    {
        if (list.Count == 0) { Log("No accounts in this scope", LogLevel.Warning); return; }
        foreach (var a in list)
        {
            a.Hwid = _hwidSvc.GenerateProfile();
            a.Hwid.Enabled = true;
        }
        if (FocusedAccount != null) RebuildHwidCompare(FocusedAccount);
        _store.Save();
        RefreshHwidPage();
        Log($"Device profiles regenerated: {list.Count} accounts · {scope}", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private void RefreshRealHwid()
    {
        try
        {
            RealHwid = _hwidSvc.ReadRealHardware();
            if (FocusedAccount != null) RebuildHwidCompare(FocusedAccount);
            RefreshHwidAdminHints();
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
    }

    [RelayCommand]
    private void ApplyHwidForFocused()
    {
        var acc = FocusedAccount ?? Accounts.FirstOrDefault(a => a.IsSelected);
        if (acc?.Hwid == null) { Log("Pick an account with a device profile", LogLevel.Warning); return; }
        try
        {
            if (_hwidSvc.TryApplyForLaunch(acc.Hwid, out var note))
                Log($"{acc.Login}: device identity applied · {note}", LogLevel.Success);
            else
                Log($"{acc.Login}: {note}", LogLevel.Warning);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
    }

    [RelayCommand]
    private void RestoreHwid()
    {
        try { _hwidSvc.Restore(); Log("Previous Windows identity restored", LogLevel.Success); }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
    }

    private void RebuildHwidCompare(SteamAccount acc)
    {
        HwidCompareRows.Clear();
        foreach (var row in _hwidSvc.Compare(RealHwid, acc.Hwid)) HwidCompareRows.Add(row);
    }

    public ObservableCollection<SteamAccount> HwidAccountRows { get; } = new();

    partial void OnHwidFilterChanged(string value) => RefreshHwidPage();

    private void RefreshHwidPage()
    {
        // Ensure every account has a stable profile (first visit after import).
        var created = 0;
        foreach (var a in Accounts)
        {
            if (a.Hwid != null) continue;
            a.Hwid = _hwidSvc.GenerateProfile();
            a.Hwid.Enabled = true;
            created++;
        }
        if (created > 0) _store.Save();

        var q = (HwidFilter ?? "").Trim();
        HwidAccountRows.Clear();
        foreach (var a in Accounts
                     .Where(a => q.Length == 0
                                 || a.Login.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || (a.PersonaName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || (a.GroupName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || a.HwidFingerprint.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase))
            HwidAccountRows.Add(a);

        RefreshHwidAdminHints();
        OnPropertyChanged(nameof(HwidProfileCount));
        OnPropertyChanged(nameof(HwidSummaryLine));
        OnPropertyChanged(nameof(HwidProfilesChip));
        if (FocusedAccount != null) RebuildHwidCompare(FocusedAccount);
    }

    private void RefreshHwidAdminHints()
    {
        OnPropertyChanged(nameof(IsRunningAsAdmin));
        OnPropertyChanged(nameof(ShowHwidAdminWarning));
        if (_hwidSvc.IsAdmin())
        {
            HwidAdminHint = T(
                "Admin OK · each login can apply that account's MachineGuid + PC name so Steam sees a different machine. Only one OS identity is active at a time.",
                "Админ OK · при логине применяется MachineGuid + имя ПК аккаунта — Steam видит другой ПК. Одновременно активна одна OS-идентичность.");
        }
        else
        {
            HwidAdminHint = T(
                "Not running as Administrator · profiles are still saved per account, but full registry spoof (MachineGuid + PC name) is limited. Right-click SilverManager.exe → Run as administrator.",
                "Не от администратора · профили всё равно сохраняются на аккаунт, но полный registry-spoof (MachineGuid + имя ПК) ограничен. ПКМ по SilverManager.exe → Запуск от имени администратора.");
        }
    }

    [RelayCommand]
    private void FocusHwidAccount(SteamAccount? acc)
    {
        if (acc == null) return;
        FocusedAccount = acc;
        RebuildHwidCompare(acc);
    }

    [RelayCommand]
    private void GenerateHwidForFocused()
    {
        var acc = FocusedAccount ?? Accounts.FirstOrDefault(a => a.IsSelected);
        if (acc == null)
        {
            Log("Click one account row in the list first, then press «This account»", LogLevel.Warning);
            return;
        }
        RegenerateHwidProfiles([acc], acc.Login);
    }

    public string HwidSelectedGroupHint => SelectedAccountGroup == null
        ? "No group selected — pick a group on Accounts/Groups, or use Selected / All"
        : $"Active group: {SelectedAccountGroup.Name} · {Accounts.Count(a => string.Equals(a.GroupName, SelectedAccountGroup.Name, StringComparison.OrdinalIgnoreCase))} accounts";

    // ── Settings / export / launch ────────────────────────────

    [RelayCommand]
    private void SetLanguage(string language)
    {
        L.SetLanguage(language);
        Ui = new UiStrings(L);
        OnPropertyChanged(nameof(Ui));
        OnPropertyChanged(nameof(IsRussian));
        OnPropertyChanged(nameof(AccountGroupsPanelToggleLabel));
        OnPropertyChanged(nameof(InventorySortLabel));
        OnPropertyChanged(nameof(DeadWeightHint));
        OnPropertyChanged(nameof(HomeDonutCenterBottom));
        OnPropertyChanged(nameof(HomeHealthLine));
        OnPropertyChanged(nameof(HomeItemsLine));
        OnPropertyChanged(nameof(HomeScanDetail));
        OnPropertyChanged(nameof(HomeAttentionDetail));
        OnPropertyChanged(nameof(ReviewSummaryLine));
        OnPropertyChanged(nameof(HwidSummaryLine));
        OnPropertyChanged(nameof(HwidSelectedGroupHint));
        OnPropertyChanged(nameof(ProxyRecommendationBanner));
        OnPropertyChanged(nameof(ShowProxyRecommendation));
        OnPropertyChanged(nameof(Step2State));
        OnPropertyChanged(nameof(Step2Detail));
        OnPropertyChanged(nameof(IncomingEmptyHint));
        OnPropertyChanged(nameof(TradePartnerLine));
        OnPropertyChanged(nameof(BestItemLine));
        foreach (var g in AccountGroups)
            g.NotifyLocalizedLabels();
        NotifyLocalizedChips();
        RefreshHwidAdminHints();
        RefreshApiKeyStatus();
        RefreshGroupSummaries();
        RebuildGroupRouteRows();
        RefreshFilter();
        RefreshStatsUi();
        RecalcDashboard();
        Log(T("Language applied to interface labels.", "Язык применён к подписям интерфейса."), LogLevel.Success);
    }

    [RelayCommand]
    private void ToggleInventoryLayout()
    {
        Settings.InventoryLayoutGrid = !Settings.InventoryLayoutGrid;
        Settings.Save();
        OnPropertyChanged(nameof(IsInventoryGrid));
        OnPropertyChanged(nameof(IsInventoryList));
        OnPropertyChanged(nameof(ShowInventoryGrid));
        OnPropertyChanged(nameof(ShowInventoryList));
        Log(Settings.InventoryLayoutGrid
            ? T("Inventory: grid cards (slower with 1000+ items)", "Инвентарь: сетка (медленнее при 1000+ предметах)")
            : T("Inventory: list (best for large farms)", "Инвентарь: список (лучше для больших ферм)"), LogLevel.Info);
    }

    [RelayCommand]
    private void SetInventoryLayout(string? mode)
    {
        var grid = string.Equals(mode, "grid", StringComparison.OrdinalIgnoreCase);
        if (Settings.InventoryLayoutGrid == grid) return;
        Settings.InventoryLayoutGrid = grid;
        Settings.Save();
        OnPropertyChanged(nameof(IsInventoryGrid));
        OnPropertyChanged(nameof(IsInventoryList));
        OnPropertyChanged(nameof(ShowInventoryGrid));
        OnPropertyChanged(nameof(ShowInventoryList));
    }

    [RelayCommand]
    private void ToggleAccountGroupsPanel()
    {
        AccountGroupsPanelExpanded = !AccountGroupsPanelExpanded;
    }

    [RelayCommand]
    private void ToggleLanguage()
    {
        SetLanguage(IsRussian ? "en" : "ru");
    }

    [RelayCommand]
    private void SaveSettings()
    {
        Settings.SteamWebApiKey = Settings.SteamWebApiKey?.Trim() ?? "";
        Settings.Save();
        SteamSession.GlobalDefaultProxy = Settings.DefaultProxy;
        SyncSfxFromSettings();
        RefreshApiKeyStatus();
        _bg.Stop();
        if (Settings.BackgroundRefreshEnabled) StartBackground();
        else BgStatus = "bg · off";
        StartAutoConfirmIfNeeded();
        if (!string.IsNullOrWhiteSpace(Settings.DefaultTradeUrl) && string.IsNullOrWhiteSpace(TradeUrl))
            TradeUrl = Settings.DefaultTradeUrl;
        Log("Settings saved", LogLevel.Success);
        _sfx.Play(Sfx.Success);
        RefreshFilter();
    }

    [RelayCommand]
    private void TestSound()
    {
        SyncSfxFromSettings();
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private async Task AutoSellJunkAsync()
    {
        var list = Accounts.Where(a => a.IsSelected && !a.IsBlocked).ToList();
        if (list.Count == 0) { Log("Select accounts for auto-sell", LogLevel.Warning); return; }

        SetBusy($"Auto-sell 0/{list.Count}");
        var totalListed = 0;
        var i = 0;
        foreach (var acc in list)
        {
            i++;
            SetBusy($"Auto-sell {i}/{list.Count}: {acc.Login}");
            try
            {
                EnsureAndApplyHwid(acc);
                var session = _sessions.TryGet(acc.Id);
                if (session is not { IsOnline: true })
                {
                    session = _sessions.GetOrCreate(acc);
                    await session.LoginAsync(new Progress<string>(m => Log($"{acc.Login}: {m}")));
                    acc.SteamId64 = session.SteamId64;
                    acc.Status = AccountStatus.Online;
                }

                var items = Items.Where(it => it.AccountId == acc.Id).ToList();
                if (items.Count == 0)
                {
                    // try load inv
                    items = await session.GetCs2InventoryAsync();
                    foreach (var it in items) it.Price = _prices.GetPrice(it.MarketHashName);
                }

                var (listed, failed, logs) = await MarketSellService.SellJunkAsync(
                    session,
                    items,
                    Settings.JunkMaxPriceUsd,
                    Settings.JunkMaxListingsPerAccount,
                    new Progress<string>(m => Log($"{acc.Login}: {m}")));

                totalListed += listed;
                foreach (var line in logs.Take(8))
                    Log($"{acc.Login}: {line}", line.StartsWith("OK") ? LogLevel.Success : LogLevel.Warning);
                Log($"{acc.Login}: listed {listed}, fail {failed}", LogLevel.Info);
                _audit.Add("market-sell", acc.Login, $"listed {listed} fail {failed}");

                // auto-confirm market listings
                if (Settings.AutoConfirmMarket)
                {
                    await Task.Delay(2000);
                    try
                    {
                        var confs = await session.GetConfirmationsAsync();
                        foreach (var c in confs.Where(c => c.Type == 3))
                        {
                            await session.RespondConfirmationAsync(c.ConfId, c.Key, true);
                            Log($"{acc.Login}: conf market OK", LogLevel.Success);
                        }
                    }
                    catch (Exception ex) { Log($"{acc.Login}: conf {ex.Message}", LogLevel.Warning); }
                }
            }
            catch (Exception ex)
            {
                HandleAccountFailure(acc, ex.Message);
                Log($"{acc.Login}: sell {ex.Message}", LogLevel.Error);
            }
            finally
            {
                ReleaseSession(acc, "after market sell");
            }
            await Task.Delay(1500);
        }
        Log($"Auto-sell done: {totalListed} listings", LogLevel.Success);
        SetBusy(null);
    }

    [RelayCommand]
    private async Task RunAutoConfirmNowAsync()
    {
        SetBusy("Auto-confirm…");
        try
        {
            var n = await _autoConf.CycleAsync(
                () => Accounts.Where(a => !a.IsBlocked).ToList(),
                a => _sessions.TryGet(a.Id));
            Log($"Auto-confirm: accepted {n}", LogLevel.Success);
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task ExportCleanListAsync()
    {
        var clean = ExportService.CleanLoginPassList(Accounts);
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"steamvault_clean_{DateTime.Now:yyyyMMdd_HHmm}.txt");
        await File.WriteAllTextAsync(path, clean);
        var count = Accounts.Count(a => !a.IsBlocked && !a.HasVac && !a.IsMarkedBanned);
        Log($"Clean list: {count} acc → {path}", LogLevel.Success);
        PushTimeline("↓", "Export clean", $"{count} accounts");
    }

    [RelayCommand]
    private async Task ExportCleanCsvAsync()
    {
        var csv = ExportService.CleanLoginPassProxyCsv(Accounts);
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"steamvault_clean_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, csv);
        Log($"Clean CSV → {path}", LogLevel.Success);
    }

    partial void OnFocusedAccountChanged(SteamAccount? value)
    {
        FocusedProxyEdit = value?.Proxy ?? "";
    }

    private string? ResolveInputProxy()
    {
        var raw = !string.IsNullOrWhiteSpace(SingleProxyInput) ? SingleProxyInput
            : !string.IsNullOrWhiteSpace(FocusedProxyEdit) ? FocusedProxyEdit
            : Settings.DefaultProxy;
        var proxy = ProxyHelper.Normalize(raw);
        if (string.IsNullOrEmpty(proxy))
        {
            Log("Enter a proxy in the field", LogLevel.Warning);
            return null;
        }
        if (!ProxyHelper.IsValid(proxy))
        {
            Log("Invalid proxy format", LogLevel.Error);
            return null;
        }
        return proxy;
    }

    /// <summary>1 proxy → 1 focused account (remembered in accounts.json).</summary>
        /// <summary>1 proxy → N selected accounts (same string, shared, remembered on each).</summary>
    [RelayCommand]
    private void ApplyProxyToSelected()
    {
        var proxy = ResolveInputProxy();
        if (proxy == null) return;
        var targets = Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Log("Select accounts on the left (up to 10) — one proxy for all", LogLevel.Warning);
            return;
        }
        foreach (var a in targets) a.Proxy = proxy;
        _store.Save();
        RecalcDashboard();
        Log($"Shared proxy → {targets.Count} acc · {ProxyHelper.Mask(proxy)} (saved on each)", LogLevel.Success);
        _sfx.Play(Sfx.Success);
    }

    [RelayCommand]
    private void ClearProxySelected()
    {
        var n = 0;
        foreach (var a in Accounts.Where(a => a.IsSelected))
        {
            a.Proxy = null;
            n++;
        }
        _store.Save();
        RecalcDashboard();
        Log($"Proxy removed from {n} accounts", LogLevel.Info);
    }

    [RelayCommand]
    private void ClearProxyFocused()
    {
        var acc = FocusedAccount;
        if (acc == null) return;
        acc.Proxy = null;
        FocusedProxyEdit = "";
        _store.Save();
        RecalcDashboard();
        Log($"{acc.Login}: proxy removed", LogLevel.Info);
    }

    [RelayCommand]
    private void SaveFocusedProxyEdit()
    {
        var acc = FocusedAccount;
        if (acc == null)
        {
            Log("No focused account", LogLevel.Warning);
            return;
        }
        acc.Proxy = string.IsNullOrWhiteSpace(FocusedProxyEdit)
            ? null
            : ProxyHelper.Normalize(FocusedProxyEdit);
        FocusedProxyEdit = acc.Proxy ?? "";
        _store.Save();
        RecalcDashboard();
        Log($"{acc.Login}: proxy = {ProxyHelper.Mask(acc.Proxy)}", LogLevel.Success);
    }

        /// <summary>List → selected 1:1 (or round-robin if fewer proxies).</summary>
    [RelayCommand]
    private void DistributeProxiesFromText()
    {
        var proxies = ProxyHelper.ParseLines(ProxyBulkText);
        if (proxies.Count == 0)
        {
            Log("Proxy list is empty/invalid. One line = one proxy", LogLevel.Warning);
            return;
        }

        var targets = Accounts.Where(a => a.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Log("Select accounts for 1:1 / round-robin distribution", LogLevel.Warning);
            return;
        }

        var n = ProxyHelper.Distribute(targets, proxies);
        _store.Save();
        RecalcDashboard();
        Log($"List: {proxies.Count} proxies → {n} acc (1:1 or round-robin). Saved.", LogLevel.Success);
        _sfx.Play(Sfx.Done);
    }

    /// <summary>Panel 2: one proxy list, spread across every account regardless of selection.</summary>
    [RelayCommand]
    private void DistributeProxiesToAllAccounts()
    {
        var proxies = ProxyHelper.ParseLines(ProxyBulkText);
        if (proxies.Count == 0)
        {
            Log("Proxy list is empty/invalid. One line = one proxy", LogLevel.Warning);
            return;
        }

        var targets = Accounts.ToList();
        if (targets.Count == 0) { Log("No accounts imported yet", LogLevel.Warning); return; }

        var n = ProxyHelper.Distribute(targets, proxies);
        _store.Save();
        RecalcDashboard();
        Log($"List: {proxies.Count} proxies → all {n} accounts (1:1 or round-robin). Saved.", LogLevel.Success);
        _sfx.Play(Sfx.Done);
    }

    /// <summary>Panel 1: the group's own proxy/pool, applied only to that group's members.</summary>
    [RelayCommand]
    private void ApplyProxyToGroupTarget()
    {
        var group = SelectedAccountGroup;
        if (group == null) { Log("Pick a group first", LogLevel.Warning); return; }
        if (group.HasProxyPool) DistributeGroupProxyPool(group);
        else if (group.HasGroupProxy) ApplySingleProxyToGroup(group);
        else Log("Add a proxy or a proxy pool for this group first", LogLevel.Warning);
    }

    [RelayCommand]
    private async Task ImportProxiesFromFileAsync()
    {
        var path = await FileDialogs.OpenFileAsync("proxy list", ("Text", new[] { "txt", "list", "csv", "proxy", "*" }));
        if (path == null) return;
        try
        {
            var proxies = ProxyHelper.ParseFile(path);
            if (proxies.Count == 0)
            {
                Log("No valid proxies in the file", LogLevel.Warning);
                return;
            }

            ProxyBulkText = string.Join(Environment.NewLine, proxies);
            Log($"File: {proxies.Count} proxies · {Path.GetFileName(path)}. Select accounts → Distribute 1:1 or check.", LogLevel.Info);
            _sfx.Play(Sfx.Panel);
        }
        catch (Exception ex)
        {
            Log("Proxy file: " + ex.Message, LogLevel.Error);
        }
    }

    [RelayCommand]
    private void SaveDefaultProxy()
    {
        Settings.DefaultProxy = ProxyHelper.Normalize(Settings.DefaultProxy) ?? "";
        Settings.Save();
        SteamSession.GlobalDefaultProxy = Settings.DefaultProxy;
        RecalcDashboard();
        Log(string.IsNullOrEmpty(Settings.DefaultProxy)
            ? "Default proxy cleared"
            : $"Default proxy: {ProxyHelper.Mask(Settings.DefaultProxy)}", LogLevel.Success);
    }

    [RelayCommand]
    private async Task CheckSingleProxyAsync()
    {
        var proxy = ResolveInputProxy();
        if (proxy == null) return;
        IsCheckingProxies = true;
        ProxyCheckSummary = "checking…";
        try
        {
            var r = await ProxyHelper.CheckAsync(proxy);
            ProxyCheckResults.Clear();
            ProxyCheckResults.Add(r);
            ProxyCheckSummary = r.StatusText;
            Log($"Check {r.ProxyShort}: {r.StatusText}", r.Ok ? LogLevel.Success : LogLevel.Error);
            if (r.Ok) _sfx.Play(Sfx.Success); else _sfx.Play(Sfx.Error);
        }
        finally { IsCheckingProxies = false; }
    }

    [RelayCommand]
    private async Task CheckSelectedAccountProxiesAsync()
    {
        var targets = Accounts.Where(a => a.IsSelected && a.HasProxy).ToList();
        if (targets.Count == 0)
        {
            // also check focused
            if (FocusedAccount is { HasProxy: true })
                targets.Add(FocusedAccount);
        }
        if (targets.Count == 0)
        {
            Log("No selected/focused account with a proxy to check", LogLevel.Warning);
            return;
        }

        IsCheckingProxies = true;
        ProxyCheckResults.Clear();
        ProxyCheckSummary = $"0/{targets.Count}";
        var ok = 0; var fail = 0; var i = 0;
        try
        {
            // unique proxies but map back to accounts
            var byProxy = targets.GroupBy(a => a.Proxy!, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in byProxy)
            {
                i++;
                ProxyCheckSummary = $"check {i}/{byProxy.Count}…";
                var r = await ProxyHelper.CheckAsync(g.Key);
                ProxyCheckResults.Insert(0, r);
                foreach (var acc in g)
                {
                    acc.ProxyCheckOk = r.Ok;
                    acc.ProxyCheckMs = r.Ms;
                    acc.ProxyCheckNote = r.StatusText;
                }
                if (r.Ok) ok++; else fail++;
                Log($"Check {r.ProxyShort} ({g.Count()} acc): {r.StatusText}",
                    r.Ok ? LogLevel.Success : LogLevel.Error);
            }
            ProxyCheckSummary = $"OK {ok} · FAIL {fail} · {byProxy.Count} unique";
            if (ok > 0 && fail == 0) _sfx.Play(Sfx.Done);
            else if (fail > 0) _sfx.Play(Sfx.Error);
        }
        finally { IsCheckingProxies = false; }
    }

    [RelayCommand]
    private async Task CheckProxyListAsync()
    {
        var proxies = ProxyHelper.ParseLines(ProxyBulkText);
        if (proxies.Count == 0)
        {
            // fallback single
            var one = ResolveInputProxy();
            if (one != null) proxies = [one];
        }
        if (proxies.Count == 0)
        {
            Log("Nothing to check — provide a list or single proxy", LogLevel.Warning);
            return;
        }

        IsCheckingProxies = true;
        ProxyCheckResults.Clear();
        ProxyCheckSummary = $"0/{proxies.Count}";
        try
        {
            var progress = new Progress<(int done, int total, ProxyCheckResult last)>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProxyCheckSummary = $"{p.done}/{p.total} · last {p.last.ProxyShort}";
                    // live feed
                    if (!ProxyCheckResults.Any(x => string.Equals(x.Proxy, p.last.Proxy, StringComparison.OrdinalIgnoreCase)))
                        ProxyCheckResults.Insert(0, p.last);
                });
            });
            var results = await ProxyHelper.CheckManyAsync(proxies, maxParallel: 4, progress: progress);
            ProxyCheckResults.Clear();
            foreach (var r in results) ProxyCheckResults.Add(r);
            var ok = results.Count(r => r.Ok);
            ProxyCheckSummary = $"OK {ok}/{results.Count}";
            Log($"Proxy list check: OK {ok} / FAIL {results.Count - ok}", ok > 0 ? LogLevel.Success : LogLevel.Error);
            if (ok == results.Count) _sfx.Play(Sfx.Done);
            else _sfx.Play(Sfx.Error);
        }
        finally { IsCheckingProxies = false; }
    }

    [RelayCommand]
    private void KeepOnlyWorkingProxiesInList()
    {
        var ok = ProxyCheckResults.Where(r => r.Ok).Select(r => r.Proxy).ToList();
        if (ok.Count == 0)
        {
            Log("No OK results — run Check list first", LogLevel.Warning);
            return;
        }
        ProxyBulkText = string.Join(Environment.NewLine, ok);
        _proxyPool.Clear();
        _proxyPool.AddRange(ok);
        Log($"Kept {ok.Count} working proxies in the list (+ auto-replace pool)", LogLevel.Success);
    }

    [RelayCommand]
    private void GoProxy()
    {
        ShellPage = (int)Models.ShellPage.Proxy;
    }

    /// <summary>If proxy is dead and pool has OK ones — reassign next.</summary>
    private bool TryReplaceDeadProxy(SteamAccount acc, string? deadProxy)
    {
        if (!Settings.AutoReplaceDeadProxy) return false;
        var pool = ProxyHelper.ParseLines(ProxyBulkText);
        if (pool.Count == 0 && _proxyPool.Count > 0) pool = _proxyPool.ToList();
        if (pool.Count == 0) return false;

        // Prefer checked-OK results
        var okSet = ProxyCheckResults.Where(r => r.Ok).Select(r => r.Proxy)
            .Concat(pool)
            .Where(p => !string.Equals(p, deadProxy, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (okSet.Count == 0) return false;

        // pick least-used
        var usage = Accounts.Where(a => a.HasProxy)
            .GroupBy(a => a.Proxy!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var next = okSet.OrderBy(p => usage.GetValueOrDefault(p, 0)).First();
        acc.Proxy = next;
        _store.Save();
        Log($"{acc.Login}: dead proxy → {ProxyHelper.Mask(next)}", LogLevel.Warning);
        return true;
    }

    [RelayCommand]
    private async Task RunBackgroundNowAsync()
    {
        SetBusy("BG…");
        try { await _bg.RunCycleAsync(() => Accounts.ToList(), a => _sessions.TryGet(a.Id)); _store.Save(); RecalcDashboard(); }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task CopyGuardCode(SteamAccount? acc)
    {
        // Codes never displayed in UI (stream-safe); only clipboard
        acc ??= FocusedAccount;
        if (acc == null) return;
        acc.RefreshGuardCode();
        await SetClipboard(acc.GuardCode);
        Log($"{acc.Login}: 2FA copied to clipboard (not shown in UI)", LogLevel.Info);
    }

    [RelayCommand]
    private async Task ExportInventoryCsvAsync()
    {
        var csv = ExportService.InventoryCsv(Items);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"steamvault_inv_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, csv);
        Log($"Export: {path}", LogLevel.Success);
    }

    [RelayCommand]
    private async Task ExportAccountsCsvAsync()
    {
        var csv = ExportService.AccountsCsv(Accounts, includeSecrets: false);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"steamvault_acc_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, csv);
        Log($"Export: {path}", LogLevel.Success);
    }

    // ExportCleanListAsync / ExportCleanCsvAsync defined above near auto-sell

    [RelayCommand]
    private async Task ExportAuditAsync()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"steamvault_audit_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, _audit.ExportCsv());
        Log($"Audit: {path}", LogLevel.Success);
    }

    [RelayCommand]
    private async Task LaunchSteamAsync()
    {
        var acc = FocusedAccount ?? Accounts.FirstOrDefault(a => a.IsSelected);
        try
        {
            SetBusy("Steam restart…");
            if (acc != null)
            {
                EnsureAndApplyHwid(acc);
                acc.RefreshGuardCode();
            }
            await SteamLauncher.RestartSteamAsync(acc?.Login, acc?.Password, acc?.GuardCode);
            if (acc != null)
            {
                // Stream-safe: don't put password+2FA in clipboard by default
                await SetClipboard(acc.Login);
                Log($"{acc.Login}: Steam launched · login in clipboard (2FA: Copy guard)", LogLevel.Success);
            }
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    [RelayCommand]
    private async Task RefreshPricesAsync()
    {
        SetBusy("Prices…");
        try
        {
            var c = await _prices.RefreshAsync(true);
            PriceStatus = $"prices · {c}";
            foreach (var it in Items) it.Price = _prices.GetPrice(it.MarketHashName);
            RefreshFilter();
        }
        catch (Exception ex) { Log(ex.Message, LogLevel.Error); }
        finally { SetBusy(null); }
    }

    private static async Task SetClipboard(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
        {
            var clip = TopLevel.GetTopLevel(w)?.Clipboard;
            if (clip != null) await clip.SetTextAsync(text);
        }
    }
}
