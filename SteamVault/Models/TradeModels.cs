using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

/// <summary>One row in Transfer «route by group» preview: where that farm sends skins.</summary>
public sealed class GroupRouteRow
{
    public string GroupName { get; set; } = "";
    public string Color { get; set; } = "#EDEDF0";
    public int AccountCount { get; set; }
    /// <summary>Human destination: warehouse login or trade link short form.</summary>
    public string Destination { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsReady { get; set; }
    public string AccountCountText => $"{AccountCount} acc";
}

public partial class ConfirmationItem : ObservableObject
{
    public string AccountId { get; set; } = "";
    public string AccountLogin { get; set; } = "";
    public string ConfId { get; set; } = "";
    public string Key { get; set; } = "";
    public int Type { get; set; }
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public string CreatorId { get; set; } = "";
    [ObservableProperty] private bool _isSelected;

    public string TypeLabel => Type switch
    {
        2 => "Trade",
        3 => "Market",
        _ => $"Type {Type}"
    };
}

public partial class TradeOfferItem : ObservableObject
{
    public string AccountId { get; set; } = "";
    public string AccountLogin { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string PartnerSteam64 { get; set; } = "";
    public bool IsIncoming { get; set; }
    public string State { get; set; } = "";
    public int TheirItems { get; set; }
    public int MyItems { get; set; }
    public string Message { get; set; } = "";
    public bool IsTrustedPartner { get; set; }
    public string SafetyLabel { get; set; } = "Review required";
    public string SafetyReason { get; set; } = "Check partner and items before accepting.";
    public bool CanAcceptSafely => IsIncoming && IsTrustedPartner && MyItems == 0;
    public bool IsEmptyReceive => IsIncoming && TheirItems == 0 && MyItems > 0;
    public bool IsEmptyGive => IsIncoming && MyItems == 0 && TheirItems > 0; // withdrawal-style

    public string Direction => IsIncoming ? "IN" : "OUT";
    public string Summary => $"{Direction} · {MyItems}→ / ←{TheirItems} · {State}";
    public string PartnerShort => string.IsNullOrWhiteSpace(PartnerSteam64)
        ? "Unknown partner"
        : PartnerSteam64.Length <= 10 ? PartnerSteam64 : PartnerSteam64[..6] + "…" + PartnerSteam64[^4..];
}

public sealed class AuditEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "";
    public string Account { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? OfferId { get; set; }
    public decimal? ValueUsd { get; set; }
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed class TimelineEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Icon { get; set; } = "•";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string TimeText => Time.ToString("HH:mm:ss");
}

public enum ShellPage
{
    Home = 0,
    Inventory = 1,
    Transfer = 2,
    Market = 8,
    // Advanced (secondary)
    Confirmations = 10,
    Incoming = 11,
    Review = 12,
    Audit = 15,
    Stats = 16,
    Proxy = 17,
    Groups = 18,
    Accounts = 19,
    Settings = 20,
    /// <summary>Per-account device profiles (HWID) so each login looks like a distinct PC.</summary>
    Hwid = 21,
    /// <summary>Info tab: auto-farming via MonkePanel.</summary>
    AutoFarm = 22
}
