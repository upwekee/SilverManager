using System.Text.Json;
using System.Text.RegularExpressions;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Account review via Steam Web API + optional GCPD scrape.
/// Logic aligned with luminary-cloud/steam-account-manager (ban_check, summaries, gcpd).
/// </summary>
public sealed class AccountReviewService
{
    public async Task ReviewAsync(
        SteamAccount account,
        string apiKey,
        SteamSession? session,
        bool includeGcpd,
        CancellationToken ct = default)
    {
        account.Review ??= new AccountReview();
        var prevVac = account.Review.VacBanned;
        var prevGame = account.Review.GameBanCount;
        var prevEco = account.Review.EconomyBan;

        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrEmpty(account.SteamId64))
        {
            await ApplyBansAndProfileAsync(account, apiKey, ct);
        }
        else if (session is { IsOnline: true } && !string.IsNullOrEmpty(session.SteamId64))
        {
            // without API key still can set steamid from session
            account.SteamId64 ??= session.SteamId64;
        }

        if (includeGcpd)
        {
            if (session is { IsOnline: true })
            {
                try { await ApplyGcpdAsync(account, session, ct); }
                catch (Exception ex)
                {
                    // Report instead of silently swallowing: the UI showed "?" with no reason before.
                    account.Review.WeeklyDropNote =
                        "GCPD failed: " + (ex.Message.Length > 80 ? ex.Message[..80] : ex.Message);
                }
            }
            else
            {
                account.Review.WeeklyDropNote = "needs an online session (Open sessions first)";
            }
        }

        account.Review.LastReviewedAt = DateTime.Now;
        account.Review.BanChanged =
            account.Review.VacBanned != prevVac ||
            account.Review.GameBanCount != prevGame ||
            !string.Equals(account.Review.EconomyBan, prevEco, StringComparison.OrdinalIgnoreCase);
        account.Review.RecomputeBadge();
        account.OnReviewChanged();
    }

    public async Task ReviewManyAsync(
        IEnumerable<SteamAccount> accounts,
        string apiKey,
        Func<SteamAccount, SteamSession?> sessionResolver,
        bool includeGcpd,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var list = accounts.Where(a => !string.IsNullOrEmpty(a.SteamId64) || a.HasMaFile).ToList();
        if (list.Count == 0) return;

        // batch bans/summaries by API (up to 100)
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var withIds = list.Where(a => !string.IsNullOrEmpty(a.SteamId64)).ToList();
            for (var i = 0; i < withIds.Count; i += 100)
            {
                var batch = withIds.Skip(i).Take(100).ToList();
                progress?.Report($"WebAPI review {i + 1}–{Math.Min(i + 100, withIds.Count)}/{withIds.Count}");
                await ApplyBansBatchAsync(batch, apiKey, ct);
                await ApplySummariesBatchAsync(batch, apiKey, ct);
                foreach (var a in batch)
                {
                    try
                    {
                        if (ulong.TryParse(a.SteamId64, out var sid))
                        {
                            a.Review ??= new AccountReview();
                            a.Review.SteamLevel = await FetchSteamLevelAsync(apiKey, sid, ct) ?? a.Review.SteamLevel;
                            var games = await FetchOwnedGamesAsync(apiKey, sid, ct);
                            if (games != null)
                            {
                                a.Review.GamesCount = games.Value.Count;
                                a.Review.PlaytimeMinutes = games.Value.Playtime;
                            }
                        }
                    }
                    catch { /* per-account optional */ }

                    a.Review!.LastReviewedAt = DateTime.Now;
                    a.Review.RecomputeBadge();
                    a.OnReviewChanged();
                }
                await Task.Delay(400, ct);
            }
        }

        if (includeGcpd)
        {
            var n = 0;
            foreach (var a in list)
            {
                n++;
                var s = sessionResolver(a);
                if (s is not { IsOnline: true }) continue;
                progress?.Report($"GCPD {n}/{list.Count}: {a.Login}");
                try { await ApplyGcpdAsync(a, s, ct); a.Review!.RecomputeBadge(); a.OnReviewChanged(); }
                catch { /* */ }
                await Task.Delay(500, ct);
            }
        }
    }

    private static async Task ApplyBansAndProfileAsync(SteamAccount account, string apiKey, CancellationToken ct)
    {
        account.Review ??= new AccountReview();
        await ApplyBansBatchAsync([account], apiKey, ct);
        await ApplySummariesBatchAsync([account], apiKey, ct);
        if (ulong.TryParse(account.SteamId64, out var sid))
        {
            account.Review.SteamLevel = await FetchSteamLevelAsync(apiKey, sid, ct) ?? -1;
            var games = await FetchOwnedGamesAsync(apiKey, sid, ct);
            if (games != null)
            {
                account.Review.GamesCount = games.Value.Count;
                account.Review.PlaytimeMinutes = games.Value.Playtime;
            }
        }
    }

    private static async Task ApplyBansBatchAsync(List<SteamAccount> accounts, string apiKey, CancellationToken ct)
    {
        var ids = string.Join(",", accounts.Select(a => a.SteamId64).Where(x => !string.IsNullOrEmpty(x)));
        if (string.IsNullOrEmpty(ids)) return;

        var url =
            $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={Uri.EscapeDataString(apiKey)}&steamids={ids}";
        using var http = CreateHttp();
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("players", out var players)) return;

        var map = accounts.Where(a => a.SteamId64 != null)
            .ToDictionary(a => a.SteamId64!, a => a, StringComparer.Ordinal);
        foreach (var p in players.EnumerateArray())
        {
            var sid = p.GetProperty("SteamId").GetString();
            if (sid == null || !map.TryGetValue(sid, out var acc)) continue;
            acc.Review ??= new AccountReview();
            var prevVac = acc.Review.VacBanned;
            var prevGame = acc.Review.GameBanCount;
            acc.Review.VacBanned = p.GetProperty("VACBanned").GetBoolean();
            acc.Review.VacBanCount = p.GetProperty("NumberOfVACBans").GetInt32();
            acc.Review.GameBanCount = p.GetProperty("NumberOfGameBans").GetInt32();
            acc.Review.CommunityBanned = p.GetProperty("CommunityBanned").GetBoolean();
            acc.Review.EconomyBan = p.TryGetProperty("EconomyBan", out var e) ? e.GetString() ?? "none" : "none";
            acc.Review.DaysSinceLastBan = p.TryGetProperty("DaysSinceLastBan", out var d) ? d.GetInt32() : 0;
            acc.Review.BanChanged = acc.Review.VacBanned != prevVac || acc.Review.GameBanCount != prevGame;
        }
    }

    private static async Task ApplySummariesBatchAsync(List<SteamAccount> accounts, string apiKey, CancellationToken ct)
    {
        var ids = string.Join(",", accounts.Select(a => a.SteamId64).Where(x => !string.IsNullOrEmpty(x)));
        if (string.IsNullOrEmpty(ids)) return;

        var url =
            $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(apiKey)}&steamids={ids}";
        using var http = CreateHttp();
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var resp) ||
            !resp.TryGetProperty("players", out var players)) return;

        var map = accounts.Where(a => a.SteamId64 != null)
            .ToDictionary(a => a.SteamId64!, a => a, StringComparer.Ordinal);
        foreach (var p in players.EnumerateArray())
        {
            var sid = p.GetProperty("steamid").GetString();
            if (sid == null || !map.TryGetValue(sid, out var acc)) continue;
            acc.Review ??= new AccountReview();
            acc.PersonaName = p.TryGetProperty("personaname", out var pn) ? pn.GetString() : acc.PersonaName;
            acc.AvatarUrl = p.TryGetProperty("avatarfull", out var av) ? av.GetString() : acc.AvatarUrl;
            acc.Review.ProfileUrl = p.TryGetProperty("profileurl", out var pu) ? pu.GetString() : null;
            acc.Review.CountryCode = p.TryGetProperty("loccountrycode", out var cc) ? cc.GetString() : null;
            acc.Review.CreatedUnix = p.TryGetProperty("timecreated", out var tc) ? tc.GetInt64() : 0;
        }
    }

    private static async Task<int?> FetchSteamLevelAsync(string apiKey, ulong steamId, CancellationToken ct)
    {
        var url =
            $"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}";
        using var http = CreateHttp();
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("response", out var r) &&
            r.TryGetProperty("player_level", out var lv))
            return lv.GetInt32();
        return null;
    }

    private static async Task<(int Count, int Playtime)?> FetchOwnedGamesAsync(string apiKey, ulong steamId, CancellationToken ct)
    {
        var url =
            $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}&include_played_free_games=1";
        using var http = CreateHttp();
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var r)) return null;
        var count = r.TryGetProperty("game_count", out var gc) ? gc.GetInt32() : 0;
        var play = 0;
        if (r.TryGetProperty("games", out var games))
            foreach (var g in games.EnumerateArray())
                if (g.TryGetProperty("playtime_forever", out var pt))
                    play += pt.GetInt32();
        return (count, play);
    }

    private static async Task ApplyGcpdAsync(SteamAccount account, SteamSession session, CancellationToken ct)
    {
        account.Review ??= new AccountReview();
        using var http = session.CreateHttpClient();
        // GCPD returns HTML — accept text/html
        http.DefaultRequestHeaders.Remove("Accept");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var sid = account.SteamId64 ?? session.SteamId64 ?? "";

        // Matchmaking tab
        var mmHtml = await FetchHtmlAsync(http,
        [
            "https://steamcommunity.com/my/gcpd/730?tab=matchmaking",
            $"https://steamcommunity.com/profiles/{sid}/gcpd/730?tab=matchmaking",
            "https://steamcommunity.com/gcpd/730?tab=matchmaking"
        ], ct);

        if (!string.IsNullOrEmpty(mmHtml) && !LooksLikeLogin(mmHtml))
        {
            ParseMatchmaking(mmHtml, account.Review);
        }

        // Account main — level / XP / prime
        var mainHtml = await FetchHtmlAsync(http,
        [
            "https://steamcommunity.com/my/gcpd/730?tab=accountmain",
            $"https://steamcommunity.com/profiles/{sid}/gcpd/730?tab=accountmain",
            "https://steamcommunity.com/gcpd/730?tab=accountmain"
        ], ct);

        if (!string.IsNullOrEmpty(mainHtml) && !LooksLikeLogin(mainHtml))
        {
            ParseAccountMain(mainHtml, account.Review);
        }
        else if (string.IsNullOrEmpty(mainHtml) || LooksLikeLogin(mainHtml))
        {
            account.Review.WeeklyDropNote = "GCPD login wall — session cookies?";
        }

        // Weekly care package heuristic via inventory history
        try { await ApplyWeeklyDropHeuristicAsync(account, session, http, ct); }
        catch (Exception ex)
        {
            account.Review.WeeklyDropClaimed = null;
            account.Review.WeeklyDropNote = "history: " + (ex.Message.Length > 60 ? ex.Message[..60] : ex.Message);
        }
        account.Review.WeeklyDropCheckedAt = DateTime.Now;
        account.Review.RecomputeBadge();
    }

    private static void ParseMatchmaking(string html, AccountReview r)
    {
        var table = ExtractTablePairs(html);
        foreach (var (label, value) in table)
        {
            var l = label.ToLowerInvariant();
            if ((l.Contains("premier") || l.Contains("csgo_premier")) &&
                int.TryParse(Digits(value), out var pr) && pr is >= 0 and <= 50000)
                r.PremierRating = pr;
            if (l.Contains("wingman") && int.TryParse(Digits(value), out var wr))
                r.WingmanRank = wr;
            if (l.Contains("cooldown") && long.TryParse(Digits(value), out var cdu) && cdu > 1_000_000_000)
                r.CooldownExpiresUnix = cdu;
        }

        var premier = Regex.Match(html, @"CSGO_Premier[^\d]{0,30}(\d{1,5})", RegexOptions.IgnoreCase);
        if (premier.Success && int.TryParse(premier.Groups[1].Value, out var p2))
            r.PremierRating = p2;
    }

    private static void ParseAccountMain(string html, AccountReview r)
    {
        var table = ExtractTablePairs(html);
        foreach (var (label, value) in table)
        {
            var l = label.ToLowerInvariant();
            var num = Digits(value);
            // CS2 / CSGO profile rank (level)
            if ((l.Contains("profile rank") || l.Contains("player level") || l.Contains("csgo_score") ||
                 l.Contains("account level") || l is "rank" or "level") &&
                int.TryParse(num, out var lvl) && lvl is >= 0 and <= 40)
                r.Cs2Level = lvl;

            // XP — "Experience", "XP", "Current XP"
            if ((l.Contains("experience") || l is "xp" || l.Contains("current xp") ||
                 l.Contains("player xp") || l.Contains("csgo xp")) &&
                int.TryParse(num, out var xp) && xp is >= 0 and < 10_000_000)
                r.Cs2Xp = xp;

            if (l.Contains("prime"))
                r.Prime = value.Contains("yes", StringComparison.OrdinalIgnoreCase) ||
                          value.Contains("true", StringComparison.OrdinalIgnoreCase) ||
                          value.Contains("1") ||
                          !value.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
        }

        // Fallback regexes on raw HTML
        if (r.Cs2Level < 0)
        {
            foreach (var pat in new[]
                     {
                         @"CSGO_Score[^\d]{0,40}(\d{1,2})",
                         @"Profile\s*Rank[^\d]{0,40}(\d{1,2})",
                         @"player_level[^\d]{0,40}(\d{1,2})",
                         @">\s*(\d{1,2})\s*<[^>]*>\s*Profile Rank",
                     })
            {
                var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var lv) && lv <= 40)
                {
                    r.Cs2Level = lv;
                    break;
                }
            }
        }

        if (r.Cs2Xp < 0)
        {
            foreach (var pat in new[]
                     {
                         @"CSGO_Score[^\d]{0,20}(\d{3,7})", // sometimes score is XP total
                         @"Experience(?:\s*Points)?[^\d]{0,40}(\d{1,7})",
                         @"player_xp[^\d]{0,40}(\d{1,7})",
                         @"(?:Current\s*)?XP[^\d]{0,30}(\d{2,7})",
                     })
            {
                var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var x) && x < 10_000_000)
                {
                    // don't overwrite level-looking tiny numbers into XP wrongly if already have level
                    if (x <= 40 && r.Cs2Level < 0) continue;
                    r.Cs2Xp = x;
                    break;
                }
            }
        }

        if (r.Cs2Xp >= 0 && r.Cs2XpToLevel < 0)
            r.Cs2XpToLevel = 5000;

        if (html.Contains("Prime Status", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("Prime Account", StringComparison.OrdinalIgnoreCase))
            r.Prime = !html.Contains("Upgrade to Prime", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Weekly drop: no official API. Inventory history rows after Wednesday reset
    /// that look like free grants (not market/trade).
    /// </summary>
    private static async Task ApplyWeeklyDropHeuristicAsync(
        SteamAccount account, SteamSession session, HttpClient http, CancellationToken ct)
    {
        account.Review ??= new AccountReview();
        var weekStart = GetCs2WeekStartUtc();
        var sid = account.SteamId64 ?? session.SteamId64;
        if (string.IsNullOrEmpty(sid))
        {
            account.Review.WeeklyDropClaimed = null;
            account.Review.WeeklyDropNote = "no steamid";
            return;
        }

        var urls = new[]
        {
            "https://steamcommunity.com/my/inventoryhistory/?app[]=730",
            $"https://steamcommunity.com/profiles/{sid}/inventoryhistory/?app[]=730",
            $"https://steamcommunity.com/profiles/{sid}/inventoryhistory/?ajax=1&app[]=730",
            $"https://steamcommunity.com/profiles/{sid}/inventoryhistory/"
        };

        string? html = null;
        foreach (var u in urls)
        {
            try
            {
                html = await FetchSingleHtmlAsync(http, u, ct);
                if (!string.IsNullOrEmpty(html) && html.Length > 80 && !LooksLikeLogin(html))
                    break;
            }
            catch { /* try next */ }
        }

        if (string.IsNullOrEmpty(html) || LooksLikeLogin(html))
        {
            account.Review.WeeklyDropClaimed = null;
            account.Review.WeeklyDropNote = "history unavailable (login/private)";
            return;
        }

        var weekStartUnix = new DateTimeOffset(weekStart).ToUnixTimeSeconds();
        var claimed = false;
        var note = "no drop-like grant this week";

        // tradehistoryrow blocks
        var rows = Regex.Matches(html,
            @"class\s*=\s*""[^""]*tradehistoryrow[^""]*""[\s\S]{0,2500}?(?=class\s*=\s*""[^""]*tradehistoryrow|</body>|$)",
            RegexOptions.IgnoreCase);

        if (rows.Count == 0)
        {
            // looser: any block with data-timestamp
            rows = Regex.Matches(html, @"data-timestamp\s*=\s*""(\d{10})""[\s\S]{0,800}", RegexOptions.IgnoreCase);
        }

        foreach (Match row in rows)
        {
            var block = row.Value;
            var tsM = Regex.Match(block, @"data-timestamp\s*=\s*""(\d{10})""", RegexOptions.IgnoreCase);
            long ts = 0;
            if (tsM.Success) long.TryParse(tsM.Groups[1].Value, out ts);
            // also try history date text — skip if we can't place in week and no ts
            if (ts > 0 && ts < weekStartUnix) continue;

            var lower = block.ToLowerInvariant();
            var isMarket = lower.Contains("market transaction") || lower.Contains("purchased on the community market");
            var isTrade = lower.Contains("trade offer") || lower.Contains("traded with") ||
                          lower.Contains("you traded") || lower.Contains("exchange");
            var isGift = lower.Contains("gift") && lower.Contains("received");
            var isUnlock = lower.Contains("unlocked a container") || lower.Contains("you unlocked");
            var isReceived = lower.Contains("you received") || lower.Contains("earned a new") ||
                             lower.Contains("dropped") || lower.Contains("care package") ||
                             lower.Contains("got an item") || lower.Contains("granted");

            if (isMarket || isTrade) continue;
            if (!isReceived && !isUnlock && !isGift) continue;

            // without timestamp still accept strong care-package wording this week hard to prove
            if (ts == 0 && !lower.Contains("care package") && !isUnlock)
                continue;

            claimed = true;
            note = ts > 0
                ? $"grant @ {DateTimeOffset.FromUnixTimeSeconds(ts):dd.MM HH:mm} UTC"
                : "grant row (no ts)";
            break;
        }

        // inventory items path: recent non-tradable containers often = fresh drops (weak signal)
        if (!claimed)
        {
            try
            {
                var inv = await session.GetCs2InventoryAsync(ct);
                var dropish = inv.Count(i =>
                    !i.Tradable &&
                    (i.Type.Contains("Container", StringComparison.OrdinalIgnoreCase) ||
                     i.MarketHashName.Contains("Case", StringComparison.OrdinalIgnoreCase) ||
                     i.MarketHashName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                     i.MarketHashName.Contains("Capsule", StringComparison.OrdinalIgnoreCase)) &&
                    i.MarketTradableRestriction is > 0 and <= 8);
                if (dropish > 0)
                {
                    // not sure claimed weekly — mark unknown with hint
                    note = $"{dropish} hold containers (possible recent drop) · history empty";
                    // leave claimed=false unless we only want hint
                }
            }
            catch { /* optional */ }
        }

        account.Review.WeeklyDropClaimed = claimed;
        account.Review.WeeklyDropNote = note + $" · week from {weekStart:dd.MM} UTC";
    }

    private static async Task<string?> FetchHtmlAsync(HttpClient http, string[] urls, CancellationToken ct)
    {
        foreach (var u in urls)
        {
            try
            {
                var html = await FetchSingleHtmlAsync(http, u, ct);
                if (!string.IsNullOrEmpty(html) && html.Length > 100)
                    return html;
            }
            catch { /* next */ }
        }
        return null;
    }

    private static async Task<string> FetchSingleHtmlAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*");
        using var resp = await http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static List<(string Label, string Value)> ExtractTablePairs(string html)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(html,
                     @"<tr[^>]*>\s*<t[dh][^>]*>(.*?)</t[dh]>\s*<t[dh][^>]*>(.*?)</t[dh]>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var a = StripTags(m.Groups[1].Value);
            var b = StripTags(m.Groups[2].Value);
            if (!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                list.Add((a.Trim(), b.Trim()));
        }
        return list;
    }

    private static string StripTags(string s) =>
        Regex.Replace(s, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();

    private static string Digits(string s)
    {
        var m = Regex.Match(s, @"-?\d+");
        return m.Success ? m.Value : "";
    }

    /// <summary>CS2 weekly reset is Wednesday ~01:00 UTC (approx; Valve can shift).</summary>
    public static DateTime GetCs2WeekStartUtc()
    {
        var now = DateTime.UtcNow;
        // Go back to most recent Wednesday 01:00 UTC
        var d = now.Date;
        while (d.DayOfWeek != DayOfWeek.Wednesday)
            d = d.AddDays(-1);
        var start = d.AddHours(1);
        if (now < start) start = start.AddDays(-7);
        return start;
    }

    /// <summary>
    /// True only for a real sign-in wall. Matching bare "login"/"password" rejected valid pages,
    /// because every Steam page footer contains those words.
    /// </summary>
    private static bool LooksLikeLogin(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        var strong =
            html.Contains("id=\"responsive_page_template_content\"", StringComparison.OrdinalIgnoreCase) &&
            html.Contains("loginForm", StringComparison.OrdinalIgnoreCase);
        return strong
               || html.Contains("name=\"password\"", StringComparison.OrdinalIgnoreCase)
               || html.Contains("Please sign in", StringComparison.OrdinalIgnoreCase)
               || html.Contains("/login/home", StringComparison.OrdinalIgnoreCase)
                  && html.Contains("Sign In", StringComparison.OrdinalIgnoreCase)
                  && !html.Contains("gcpd", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SteamVault/1.0");
        return h;
    }
}
