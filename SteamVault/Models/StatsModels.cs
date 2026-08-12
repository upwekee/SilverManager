namespace SteamVault.Models;

public sealed class StatsSnapshot
{
    public DateTime Time { get; set; } = DateTime.Now;
    public int AccountCount { get; set; }
    public int BannedCount { get; set; }
    public int OnlineCount { get; set; }
    public decimal PortfolioUsd { get; set; }
    public int TradesOk { get; set; }
    public int TradesFail { get; set; }
    public int ItemsMoved { get; set; }
    public decimal VolumeUsd { get; set; }
}

public sealed class ChartBar
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public string Tip { get; set; } = "";
    public double Normalized { get; set; } // 0..1 for UI height
    /// <summary>Label drawn above the tallest bar. Falls back to the money-formatted value.</summary>
    public string ValueText { get; set; } = "";
}

public enum StatsPeriod
{
    Hours24 = 0,
    Days7 = 1,
    Days30 = 2,
    All = 3
}

public static class BanHeuristics
{
    public static bool LooksLikeBanOrBlock(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.ToLowerInvariant();
        return m.Contains("vac")
               || m.Contains("banned")
               || m.Contains("ban ")
               || m.Contains(" trade ban")
               || m.Contains("tradeban")
               || m.Contains("limited account")
               || m.Contains("account is limited")
               || m.Contains("cannot trade")
               || m.Contains("can't trade")
               || m.Contains("not available to trade")
               || m.Contains("access denied")
               || m.Contains("logged in elsewhere")
               || m.Contains("disabled")
               || m.Contains("suspended")
               || m.Contains("invalid password")
               || m.Contains("account logon denied")
               || m.Contains("rate limit")
               || m.Contains("too many")
               || m.Contains("timeout")
               || m.Contains("timeout");
    }

    public static bool LooksLikeHardBan(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = message.ToLowerInvariant();
        return m.Contains("vac")
               || m.Contains("banned")
               || m.Contains("trade ban")
               || m.Contains("tradeban")
               || m.Contains("suspended")
               || m.Contains("disabled");
    }
}
