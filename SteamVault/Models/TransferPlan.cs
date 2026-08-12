namespace SteamVault.Models;

/// <summary>
/// Immutable-at-approval transfer snapshot. It contains only the data needed to execute
/// a reviewed queue and deliberately never exposes secrets or unmasked trade-link tokens.
/// </summary>
public sealed class TransferPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public string Fingerprint { get; init; } = "";
    public bool IsDryRun { get; init; }
    public List<TransferPlanAccount> Accounts { get; } = [];
    public List<TransferPlanIssue> Issues { get; } = [];

    public IEnumerable<TransferPlanAccount> ReadyAccounts => Accounts.Where(x => x.IsReady);
    public int SourceCount => ReadyAccounts.Count();
    public int SkippedAccountCount => Accounts.Count(x => !x.IsReady);
    public int ItemCount => ReadyAccounts.Sum(x => x.Items.Count);
    public int OfferCount => ReadyAccounts.Sum(x => x.OfferCount);
    public decimal TotalValue => ReadyAccounts.Sum(x => x.TotalValue);
    public int DestinationCount => ReadyAccounts.Select(x => x.DestinationSteam64).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count();
    public bool HasBlockingIssues => Issues.Any(x => x.IsBlocking);

    public string Summary => $"{SourceCount} sources · {ItemCount} items · {OfferCount} offers · ${TotalValue:0.00} · {DestinationCount} destinations";
}

public sealed class TransferPlanAccount
{
    public string AccountId { get; init; } = "";
    public string Login { get; init; } = "";
    public string? GroupName { get; init; }
    public string DestinationSteam64 { get; set; } = "";
    public string DestinationLabel { get; set; } = "";
    public string TradeUrl { get; set; } = "";
    public List<TransferPlanItem> Items { get; } = [];
    public string State { get; set; } = "Ready";
    public string Reason { get; set; } = "";
    public bool IsReady => State == "Ready";
    public int OfferCount { get; set; }
    public decimal TotalValue => Items.Sum(x => x.TotalValue);
    public string DestinationShort => string.IsNullOrWhiteSpace(DestinationSteam64)
        ? "—"
        : DestinationSteam64.Length <= 10 ? DestinationSteam64 : DestinationSteam64[..6] + "…" + DestinationSteam64[^4..];
}

public sealed class TransferPlanItem
{
    public string AssetId { get; init; } = "";
    public int Amount { get; init; } = 1;
    public decimal UnitPrice { get; init; }
    public decimal TotalValue => UnitPrice * Math.Max(1, Amount);
}

public sealed class TransferPlanIssue
{
    public string Severity { get; init; } = "Error";
    public string Message { get; init; } = "";
    public int AccountCount { get; init; }
    public bool IsBlocking => Severity == "Error";
}
