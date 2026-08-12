using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

/// <summary>
/// Account review snapshot (bans / level / CS2) — SAM-compatible fields.
/// </summary>
public partial class AccountReview : ObservableObject
{
    // Bans (Steam Web API GetPlayerBans)
    [ObservableProperty] private bool _vacBanned;
    [ObservableProperty] private int _vacBanCount;
    [ObservableProperty] private int _gameBanCount;
    [ObservableProperty] private bool _communityBanned;
    [ObservableProperty] private string _economyBan = "none";
    [ObservableProperty] private int _daysSinceLastBan;
    [ObservableProperty] private bool _hasAnyBan;

    // Profile
    [ObservableProperty] private int _steamLevel = -1;
    [ObservableProperty] private int _gamesCount = -1;
    [ObservableProperty] private int _playtimeMinutes;
    [ObservableProperty] private string? _countryCode;
    [ObservableProperty] private long _createdUnix;
    [ObservableProperty] private string? _profileUrl;

    // CS2 GCPD (when session available)
    [ObservableProperty] private int _premierRating = -1;
    [ObservableProperty] private int _premierWins = -1;
    [ObservableProperty] private int _wingmanRank = -1;
    [ObservableProperty] private int _cs2Level = -1;
    /// <summary>Best-effort CS2 XP toward next level (from GCPD HTML). -1 unknown.</summary>
    [ObservableProperty] private int _cs2Xp = -1;
    /// <summary>XP needed for next level if parsed. Often 5000 for weekly threshold context.</summary>
    [ObservableProperty] private int _cs2XpToLevel = -1;
    [ObservableProperty] private long _cooldownExpiresUnix;
    [ObservableProperty] private string? _cooldownReason;
    [ObservableProperty] private bool _prime;

    /// <summary>null=unknown, true=likely claimed this week, false=no drop event seen.</summary>
    [ObservableProperty] private bool? _weeklyDropClaimed;
    [ObservableProperty] private string _weeklyDropNote = "";
    [ObservableProperty] private DateTime? _weeklyDropCheckedAt;

    // Meta
    [ObservableProperty] private DateTime? _lastReviewedAt;
    [ObservableProperty] private bool _banChanged;
    [ObservableProperty] private string _badgeSummary = "";

    public string Cs2XpText
    {
        get
        {
            var lvl = Cs2Level >= 0 ? $"CS2 Lv{Cs2Level}" : null;
            var xp = Cs2Xp >= 0
                ? (Cs2XpToLevel > 0 ? $"{Cs2Xp}/{Cs2XpToLevel} XP" : $"{Cs2Xp} XP")
                : null;
            if (lvl != null && xp != null) return $"{lvl} · {xp}";
            if (lvl != null) return lvl;
            if (xp != null) return $"CS2 {xp}";
            return "CS2 XP: ?";
        }
    }

    public string WeeklyDropText => WeeklyDropClaimed switch
    {
        true => "Weekly: claimed ✓",
        false => "Weekly: not seen",
        _ => "Weekly: ?"
    };

    public void RecomputeBadge()
    {
        HasAnyBan = VacBanned || GameBanCount > 0 || CommunityBanned ||
                    (!string.IsNullOrEmpty(EconomyBan) && EconomyBan is not "none");
        var parts = new List<string>();
        if (VacBanned || VacBanCount > 0) parts.Add($"VAC×{Math.Max(1, VacBanCount)}");
        if (GameBanCount > 0) parts.Add($"Game×{GameBanCount}");
        if (CommunityBanned) parts.Add("Community");
        if (EconomyBan is not null and not "none") parts.Add($"Eco:{EconomyBan}");
        if (CooldownExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds()) parts.Add("CD");
        if (PremierRating >= 0) parts.Add($"PR {PremierRating}");
        if (SteamLevel >= 0) parts.Add($"Lv{SteamLevel}");
        if (Cs2Level >= 0) parts.Add($"CS2:{Cs2Level}");
        if (WeeklyDropClaimed == true) parts.Add("Drop✓");
        else if (WeeklyDropClaimed == false) parts.Add("Drop?");
        BadgeSummary = parts.Count > 0 ? string.Join(" · ", parts) : "clean";
        OnPropertyChanged(nameof(Cs2XpText));
        OnPropertyChanged(nameof(WeeklyDropText));
    }
}
