using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

public partial class SteamAccount : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string? SharedSecret { get; set; }
    public string? IdentitySecret { get; set; }
    public string? DeviceId { get; set; }

    [ObservableProperty] private string? _steamId64;
    [ObservableProperty] private string? _personaName;
    [ObservableProperty] private string? _avatarUrl;
    [ObservableProperty] private AccountStatus _status = AccountStatus.Offline;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private int _inventoryCount;
    [ObservableProperty] private decimal _inventoryValue;
    /// <summary>True after at least one inventory fetch (even if empty).</summary>
    [ObservableProperty] private bool _inventoryScanned;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasMaFile;
    [ObservableProperty] private string _guardCode = "-----";
    [ObservableProperty] private AccountReview? _review;
    [ObservableProperty] private HwidProfile? _hwid;
    [ObservableProperty] private string _trustLabel = "green"; // green / yellow / red
    [ObservableProperty] private string? _notes;
    /// <summary>Optional user-defined routing group. Accounts in one group can share a dedicated trade link.</summary>
    [ObservableProperty] private string? _groupName;

    /// <summary>
    /// Marks this account as the storage destination. Exactly one account holds the flag —
    /// the ViewModel clears it elsewhere when a new one is marked, because "send everything
    /// to the warehouse" has to resolve to a single address to mean anything.
    /// </summary>
    [ObservableProperty] private bool _isWarehouse;

    /// <summary>Trade link of the warehouse account, captured once so transfers need no re-fetch.</summary>
    [ObservableProperty] private string? _ownTradeUrl;

    /// <summary>Where the currently linked maFile came from. The secrets themselves stay in the protected vault.</summary>
    [ObservableProperty] private string? _maFilePath;

    public bool HasOwnTradeUrl => !string.IsNullOrWhiteSpace(OwnTradeUrl);

    partial void OnOwnTradeUrlChanged(string? value) => OnPropertyChanged(nameof(HasOwnTradeUrl));

    /// <summary>Manually or auto-marked as banned/blocked — skip in trade queues.</summary>
    [ObservableProperty] private bool _isMarkedBanned;
    [ObservableProperty] private string? _banReason;
    [ObservableProperty] private DateTime? _bannedAt;
    /// <summary>Soft skip (rate-limit / temp error) — auto-retry later.</summary>
    [ObservableProperty] private bool _isTempSkipped;
    [ObservableProperty] private int _failStreak;

    /// <summary>Per-account proxy (remembered in accounts.json). Same string on N accounts = shared proxy.</summary>
    [ObservableProperty] private string? _proxy;

    /// <summary>Market CSGO (market.csgo.com) API Secret Key for this account.</summary>
    [ObservableProperty] private string? _marketApiKey;
    [ObservableProperty] private decimal _marketAvailableBalance;
    [ObservableProperty] private decimal _marketFrozenBalance;
    [ObservableProperty] private string _marketCurrency = "RUB";

    public bool HasMarketApiKey => !string.IsNullOrWhiteSpace(MarketApiKey);

    partial void OnMarketApiKeyChanged(string? value) => OnPropertyChanged(nameof(HasMarketApiKey));

    /// <summary>Last checker result for this account's proxy (UI only, not persisted).</summary>
    [ObservableProperty] private bool? _proxyCheckOk;
    [ObservableProperty] private int _proxyCheckMs;
    [ObservableProperty] private string? _proxyCheckNote;

    public bool HasProxy => !string.IsNullOrWhiteSpace(Proxy);

    /// <summary>True when a saved device profile exists for this account.</summary>
    public bool HasHwid => Hwid != null && !string.IsNullOrWhiteSpace(Hwid.MachineGuid);

    /// <summary>Short fingerprint for lists (MachineGuid prefix · PC name).</summary>
    public string HwidFingerprint
    {
        get
        {
            if (Hwid == null) return "no profile";
            var guid = Hwid.MachineGuid ?? "";
            var shortGuid = guid.Length > 8 ? guid[..8] : (string.IsNullOrWhiteSpace(guid) ? "—" : guid);
            var pc = string.IsNullOrWhiteSpace(Hwid.PcName) ? "" : Hwid.PcName;
            return string.IsNullOrEmpty(pc) ? shortGuid : $"{shortGuid} · {pc}";
        }
    }

    /// <summary>Single letter shown in the avatar slot before the real avatar loads (or when there is none).</summary>
    public string Initial
    {
        get
        {
            var src = !string.IsNullOrWhiteSpace(PersonaName) ? PersonaName! : Login;
            return string.IsNullOrWhiteSpace(src) ? "?" : src.Trim()[..1].ToUpperInvariant();
        }
    }
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);

    partial void OnPersonaNameChanged(string? value) => OnPropertyChanged(nameof(Initial));
    partial void OnAvatarUrlChanged(string? value) => OnPropertyChanged(nameof(HasAvatar));
    partial void OnHwidChanged(HwidProfile? value)
    {
        OnPropertyChanged(nameof(HasHwid));
        OnPropertyChanged(nameof(HwidFingerprint));
    }

    public string ProxyShort
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Proxy)) return "";
            var p = Proxy!;
            var at = p.LastIndexOf('@');
            if (at > 0) return "…@" + p[(at + 1)..];
            return p.Length > 22 ? p[..20] + "…" : p;
        }
    }

    public string ProxyCheckBadge => ProxyCheckOk switch
    {
        true => $"OK {ProxyCheckMs}ms",
        false => "FAIL",
        _ => HasProxy ? "PX" : ""
    };

    partial void OnProxyChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProxy));
        OnPropertyChanged(nameof(ProxyShort));
        ProxyCheckOk = null;
        ProxyCheckMs = 0;
        ProxyCheckNote = null;
        OnPropertyChanged(nameof(ProxyCheckBadge));
        OnPropertyChanged(nameof(Readiness));
    }

    partial void OnProxyCheckOkChanged(bool? value)
    {
        OnPropertyChanged(nameof(ProxyCheckBadge));
        OnPropertyChanged(nameof(Readiness));
    }

    public bool CanTrade => !string.IsNullOrEmpty(SharedSecret)
                            && !string.IsNullOrEmpty(IdentitySecret)
                            && !IsBlocked;

    /// <summary>Hard block: manual mark or any Steam API ban category — never trade.</summary>
    public bool IsBlocked => IsMarkedBanned || Review?.HasAnyBan == true || HasTradeBan;

    public bool HasVac => Review is { VacBanned: true } || Review is { VacBanCount: > 0 };
    public bool HasTradeBan =>
        Review is { EconomyBan: not null and not "none" } ||
        (BanReason?.Contains("trade", StringComparison.OrdinalIgnoreCase) == true);

    private static string L(string en, string ru) =>
        Services.LocalizationService.Current?.T(en, ru) ?? en;

    public string Readiness
    {
        get
        {
            if (IsBlocked || HasBanFlag) return L("Blocked", "Заблокирован");
            if (!HasMaFile) return L("No maFile", "Нет maFile");
            if (ProxyCheckOk == false) return L("Proxy failed", "Прокси fail");
            if (!InventoryScanned) return L("Not scanned", "Не сканирован");
            if (InventoryCount <= 0) return L("Empty inventory", "Пустой инвентарь");
            return L("Ready", "Готов");
        }
    }

    public string InventoryCountText => $"{InventoryCount} {L("items", "шт.")}";

    /// <summary>Refresh localized readiness/status after language switch.</summary>
    public void NotifyLocalizedStatus()
    {
        OnPropertyChanged(nameof(Readiness));
        OnPropertyChanged(nameof(InventoryCountText));
        OnPropertyChanged(nameof(VacStatus));
        OnPropertyChanged(nameof(VacCheckedText));
        OnPropertyChanged(nameof(ReviewBadge));
    }

    public string ReviewBadge => Review?.BadgeSummary ?? L("Not checked", "Не проверен");
    public bool HasBanFlag => IsBlocked || Review?.HasAnyBan == true;
    public string VacStatus => Review == null
        ? L("Not checked", "Не проверен")
        : HasVac ? L("VAC banned", "VAC бан")
        : Review.GameBanCount > 0 ? $"{L("Game ban", "Game ban")} ×{Review.GameBanCount}"
        : Review.CommunityBanned ? L("Community banned", "Community бан")
        : Review.EconomyBan is not null and not "none" ? $"Economy: {Review.EconomyBan}"
        : L("Clean", "Чистый");
    public string VacCheckedText => Review?.LastReviewedAt is { } date
        ? $"{L("Checked", "Проверен")} {date:dd.MM.yyyy HH:mm}"
        : L("Steam API not checked", "Steam API не проверялся");

    public void OnReviewChanged()
    {
        // Auto-mark from API review
        if (Review is { VacBanned: true } or { VacBanCount: > 0 })
        {
            IsMarkedBanned = true;
            BanReason ??= Review.BadgeSummary;
            BannedAt ??= DateTime.Now;
            TrustLabel = "red";
        }
        OnPropertyChanged(nameof(Review));
        OnPropertyChanged(nameof(ReviewBadge));
        OnPropertyChanged(nameof(HasBanFlag));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(HasVac));
        OnPropertyChanged(nameof(CanTrade));
        OnPropertyChanged(nameof(Readiness));
        OnPropertyChanged(nameof(VacStatus));
        OnPropertyChanged(nameof(VacCheckedText));
    }

    public void MarkBlocked(string reason)
    {
        IsMarkedBanned = true;
        BanReason = reason;
        BannedAt = DateTime.Now;
        TrustLabel = "red";
        IsSelected = false;
        Status = AccountStatus.Error;
        StatusText = "blocked";
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(HasBanFlag));
        OnPropertyChanged(nameof(CanTrade));
    }

    public void ClearBlock()
    {
        IsMarkedBanned = false;
        BanReason = null;
        BannedAt = null;
        FailStreak = 0;
        IsTempSkipped = false;
        TrustLabel = "green";
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(HasBanFlag));
        OnPropertyChanged(nameof(CanTrade));
    }

    public void RefreshGuardCode()
    {
        if (string.IsNullOrEmpty(SharedSecret))
        {
            GuardCode = "no ma";
            return;
        }
        try
        {
            GuardCode = Services.SteamTotp.GenerateAuthCode(SharedSecret);
        }
        catch
        {
            GuardCode = "err";
        }
    }
}

public enum AccountStatus
{
    Offline,
    Connecting,
    Online,
    Busy,
    Error
}
