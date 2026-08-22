using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using SteamVault.Models;
// ConfirmationItem, TradeOfferItem

namespace SteamVault.Services;

/// <summary>
/// Per-account Steam session.
/// Login: SteamRE/SteamKit Samples/000_Authentication + 002_WebCookie.
/// Own trade URL: same path as DoctorMcKay node-steam-user —
/// <c>Econ.GetTradeOfferAccessToken#1</c> over the CM connection (no HTML scrape).
/// Community pages still use steamLoginSecure for inventory / offers.
/// </summary>
public sealed class SteamSession : IDisposable
{
    private readonly SteamAccount _account;
    private SteamClient? _client;
    private CallbackManager? _manager;
    private SteamUser? _user;
    private CancellationTokenSource? _callbackCts;
    private Task? _callbackLoop;
    private readonly object _gate = new();

    public string AccountId => _account.Id;
    public string? SteamId64 { get; private set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? SessionId { get; private set; }
    public bool IsOnline { get; private set; }
    public CookieContainer Cookies { get; } = new();

    public SteamSession(SteamAccount account) => _account = account;

    /// <summary>Optional global fallback proxy from settings.</summary>
    public static string? GlobalDefaultProxy { get; set; }

    private SteamClient CreateClientWithProxy()
    {
        var proxyStr = !string.IsNullOrWhiteSpace(_account.Proxy) ? _account.Proxy : GlobalDefaultProxy;
        var proxy = ProxyHelper.TryCreate(proxyStr);
        if (proxy == null)
            return new SteamClient();

        var config = SteamConfiguration.Create(b =>
        {
            b.WithHttpClientFactory(() =>
            {
                var handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                    AutomaticDecompression = DecompressionMethods.All,
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(handler);
            });
        });
        return new SteamClient(config);
    }

    public async Task LoginAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_account.SharedSecret))
            throw new InvalidOperationException("Missing shared_secret (maFile)");

        // Already online — reuse
        if (IsOnline && _client?.IsConnected == true && !string.IsNullOrEmpty(SteamId64))
        {
            progress?.Report("Already online");
            return;
        }

        await SteamTotp.AlignTimeAsync(ct);
        progress?.Report("Connecting to Steam…");

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        lock (_gate)
        {
            DisposeClient();
            _client = CreateClientWithProxy();
            _manager = new CallbackManager(_client);
            _user = _client.GetHandler<SteamUser>()!;
            IsOnline = false;

            void HandleConnected(SteamClient.ConnectedCallback cb)
            {
                OnConnectedAsync(tcs, progress).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        tcs.TrySetException(t.Exception.InnerException ?? t.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            void HandleDisconnected(SteamClient.DisconnectedCallback cb)
            {
                if (!cb.UserInitiated && !tcs.Task.IsCompleted)
                    tcs.TrySetException(new Exception("Disconnected from Steam before login (network/IP ban?)"));
                if (cb.UserInitiated == false)
                    IsOnline = false;
            }

            void HandleLoggedOn(SteamUser.LoggedOnCallback cb) => OnLoggedOn(cb, tcs, progress);

            _manager.Subscribe<SteamClient.ConnectedCallback>(HandleConnected);
            _manager.Subscribe<SteamClient.DisconnectedCallback>(HandleDisconnected);
            _manager.Subscribe<SteamUser.LoggedOnCallback>(HandleLoggedOn);

            _callbackCts = new CancellationTokenSource();
            var token = _callbackCts.Token;
            var mgr = _manager;
            _callbackLoop = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
                    catch { /* teardown */ }
                }
            }, token);

            _client.Connect();
        }

        // 45s is enough; 90 felt like "stuck"
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var reg2 = linked.Token.Register(() =>
            tcs.TrySetException(new TimeoutException(
                "Login timed out (45s). Check login/password/maFile, network, and Steam Guard.")));

        await tcs.Task;
    }

    private async Task OnConnectedAsync(TaskCompletionSource<bool> tcs, IProgress<string>? progress)
    {
        try
        {
            progress?.Report("Signing in (credentials + 2FA)…");
            var authSession = await _client!.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = _account.Login,
                    Password = _account.Password,
                    IsPersistentSession = false,
                    Authenticator = new TotpAuthenticator(_account.SharedSecret!),
                });

            progress?.Report("Waiting for token…");
            var poll = await authSession.PollingWaitForResultAsync();

            AccessToken = poll.AccessToken;
            RefreshToken = poll.RefreshToken;

            progress?.Report("LogOn with access token…");
            _user!.LogOn(new SteamUser.LogOnDetails
            {
                Username = poll.AccountName,
                AccessToken = poll.RefreshToken,
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb, TaskCompletionSource<bool> tcs, IProgress<string>? progress)
    {
        if (cb.Result != EResult.OK)
        {
            tcs.TrySetException(new Exception($"LogOn failed: {cb.Result} / {cb.ExtendedResult}"));
            return;
        }

        if (cb.ClientSteamID is null)
        {
            tcs.TrySetException(new Exception("LogOn OK, but SteamID is empty"));
            return;
        }

        SteamId64 = cb.ClientSteamID.ConvertToUInt64().ToString();
        _account.SteamId64 = SteamId64;
        _account.DeviceId = SteamTotp.EnsureDeviceId(_account.DeviceId, SteamId64);

        // sessionid: Steam expects a simple hex/token string (not base64 with +/)
        SessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();
        ApplyWebCookies();

        IsOnline = true;
        progress?.Report("Online");
        tcs.TrySetResult(true);
    }

    private void ApplyWebCookies()
    {
        if (string.IsNullOrEmpty(SteamId64) || string.IsNullOrEmpty(AccessToken)) return;

        // steamLoginSecure = "{steam64}||{jwt access token}" — required for community pages
        // including /profiles/{id}/tradeoffers/privacy where the trade URL lives.
        var steamLoginSecure = $"{SteamId64}||{AccessToken}";
        SessionId ??= Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
            .ToLowerInvariant();

        void Set(string domain)
        {
            try
            {
                // Clear previous community cookies for this domain so stale tokens don't win.
                try
                {
                    var existing = Cookies.GetCookies(new Uri($"https://{domain.TrimStart('.')}"));
                    foreach (Cookie old in existing)
                    {
                        old.Expired = true;
                        old.Value = "";
                    }
                }
                catch { /* ignore */ }

                Cookies.Add(new Cookie("sessionid", SessionId, "/", domain) { Secure = true });
                Cookies.Add(new Cookie("steamLoginSecure", steamLoginSecure, "/", domain)
                {
                    Secure = true,
                    HttpOnly = true
                });
            }
            catch { /* domain issues */ }
        }

        Set("steamcommunity.com");
        Set(".steamcommunity.com");
        Set("store.steampowered.com");
        Set(".steampowered.com");
    }

    public HttpClient CreateHttpClient() => CreateInventoryHttpClient(useCookies: true);

    /// <summary>
    /// HTTP client for community / inventory. Proxy = account.Proxy, else Settings default.
    /// </summary>
    private HttpClient CreateInventoryHttpClient(bool useCookies)
    {
        var proxyStr = !string.IsNullOrWhiteSpace(_account.Proxy) ? _account.Proxy : GlobalDefaultProxy;
        var proxy = ProxyHelper.TryCreate(proxyStr);
        var handler = new HttpClientHandler
        {
            CookieContainer = useCookies ? Cookies : new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = useCookies,
            AllowAutoRedirect = true,
            Proxy = proxy,
            UseProxy = proxy != null,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,application/json,text/plain,*/*;q=0.8");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://steamcommunity.com/");
        return http;
    }

    public string GetEffectiveProxyDescription()
    {
        if (!string.IsNullOrWhiteSpace(_account.Proxy))
            return $"Proxy: {ProxyHelper.Mask(_account.Proxy)} (Личный)";
        if (!string.IsNullOrWhiteSpace(GlobalDefaultProxy))
            return $"Default Proxy: {ProxyHelper.Mask(GlobalDefaultProxy)} (Дефолтный)";
        return "Direct IP (без прокси)";
    }

    /// <summary>
    /// Fetch CS2 inventory. Tries public endpoint first (no login), then authenticated.
    /// </summary>
    public async Task<List<InventoryItem>> GetCs2InventoryAsync(CancellationToken ct = default)
    {
        var steamId = SteamId64 ?? _account.SteamId64;
        if (string.IsNullOrEmpty(steamId))
            throw new InvalidOperationException("Missing SteamID64 — sign in to the account or add steamid to maFile");

        // If account has credentials, ensure session login first so Steam returns FULL inventory including Trade-Protected items
        if (!IsOnline && (!string.IsNullOrEmpty(_account.Password) || _account.HasMaFile))
        {
            try
            {
                await LoginAsync(ct: ct);
            }
            catch { /* fallback to public fetch if login fails */ }
        }

        if (IsOnline)
        {
            try
            {
                return await FetchInventoryJsonAsync(steamId, useCookies: true, ct);
            }
            catch { /* fallback to public below */ }
        }

        // Public unauthenticated fallback
        Exception? publicErr = null;
        try
        {
            var publicInv = await FetchInventoryJsonAsync(steamId, useCookies: false, ct);
            // If public fetch returned items, return them
            if (publicInv.Count > 0) return publicInv;
        }
        catch (Exception ex)
        {
            publicErr = ex;
        }

        if (!IsOnline)
        {
            // Attempt login once more if public fetch failed
            if (!string.IsNullOrEmpty(_account.Password) || _account.HasMaFile)
            {
                await LoginAsync(ct: ct);
                return await FetchInventoryJsonAsync(steamId, useCookies: true, ct);
            }

            throw new InvalidOperationException(
                $"Inventory is not publicly available ({publicErr?.Message}). Sign in, then load it again.");
        }

        return await FetchInventoryJsonAsync(steamId, useCookies: true, ct);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

    private async Task<List<InventoryItem>> FetchInventoryJsonAsync(string steamId, bool useCookies, CancellationToken ct)
    {
        // Always honor account.Proxy / DefaultProxy — even for public inventory (no cookies).
        // Previously the public path used a plain HttpClient and ignored proxies entirely.
        using var http = useCookies
            ? CreateHttpClient()
            : CreateInventoryHttpClient(useCookies: false);

        // paginate — Steam often returns 1000 max per request
        var all = new List<InventoryItem>();
        string? startAsset = null;
        var pages = 0;
        do
        {
            pages++;
            if (pages > 8) break;
            var url =
                $"https://steamcommunity.com/inventory/{steamId}/730/2?l=english&count=2000";
            if (!string.IsNullOrEmpty(startAsset))
                url += $"&start_assetid={startAsset}";

            HttpResponseMessage resp;
            try
            {
                resp = await http.GetAsync(url, ct);
            }
            catch (Exception ex)
            {
                throw new Exception($"Network error fetching inventory: {ex.Message}", ex);
            }

            using (resp)
            {
                // Auto-retry once on HTTP 429 (RateLimit)
                if ((int)resp.StatusCode == 429)
                {
                    await Task.Delay(2500, ct);
                    using var retryResp = await http.GetAsync(url, ct);
                    var retryBody = await retryResp.Content.ReadAsStringAsync(ct);

                    if (!retryResp.IsSuccessStatusCode)
                        throw new Exception($"Inventory HTTP {(int)retryResp.StatusCode}: {Trim(retryBody, 100)}");

                    if (string.IsNullOrWhiteSpace(retryBody) || retryBody.TrimStart().StartsWith('<'))
                        throw new Exception("Steam returned HTML instead of JSON (rate limit / login wall)");

                    using var retryDoc = JsonDocument.Parse(retryBody);
                    var retryRoot = retryDoc.RootElement;

                    var retryBatch = ParseInventoryPage(retryRoot);
                    all.AddRange(retryBatch);

                    var retryMore = retryRoot.TryGetProperty("more_items", out var rmi) &&
                               (rmi.ValueKind == JsonValueKind.Number ? rmi.GetInt32() == 1 : rmi.GetBoolean());
                    startAsset = retryMore && retryRoot.TryGetProperty("last_assetid", out var rla)
                        ? rla.ToString()
                        : null;
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 401)
                    throw new Exception("Inventory is private — click Login");

                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"Inventory HTTP {(int)resp.StatusCode}: {Trim(body, 100)}");

                if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<'))
                    throw new Exception("Steam returned HTML instead of JSON (rate limit / login wall)");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("success", out var success))
                {
                    var ok = success.ValueKind == JsonValueKind.Number
                        ? success.GetInt32() == 1
                        : success.ValueKind == JsonValueKind.True;
                    if (!ok)
                        throw new Exception("success=0 — inventory is private or Steam returned an error");
                }

                var batch = ParseInventoryPage(root);
                all.AddRange(batch);

                var more = root.TryGetProperty("more_items", out var mi) &&
                           (mi.ValueKind == JsonValueKind.Number ? mi.GetInt32() == 1 : mi.GetBoolean());
                startAsset = more && root.TryGetProperty("last_assetid", out var la)
                    ? la.ToString()
                    : null;
            }
        } while (!string.IsNullOrEmpty(startAsset));

        return all;
    }

    private List<InventoryItem> ParseInventoryPage(JsonElement root)
    {
        var descMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("descriptions", out var descs) && descs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in descs.EnumerateArray())
            {
                var classId = d.GetProperty("classid").GetString() ?? "";
                var instanceId = d.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "0" : "0";
                descMap[$"{classId}_{instanceId}"] = d;
            }
        }

        var items = new List<InventoryItem>();
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var a in assets.EnumerateArray())
        {
            var classId = a.GetProperty("classid").GetString() ?? "";
            var instanceId = a.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "0" : "0";
            var assetId = a.GetProperty("assetid").GetString() ?? "";
            descMap.TryGetValue($"{classId}_{instanceId}", out var desc);
            if (desc.ValueKind == JsonValueKind.Undefined)
                descMap.TryGetValue($"{classId}_0", out desc);

            var marketHash = GetStr(desc, "market_hash_name") ?? GetStr(desc, "market_name") ?? GetStr(desc, "name") ?? "Unknown";
            var icon = GetStr(desc, "icon_url_large") ?? GetStr(desc, "icon_url");
            var tradable = GetInt(desc, "tradable") == 1;
            var marketable = GetInt(desc, "marketable") == 1;
            var rarity = "";
            string? rarityColor = null;
            var exterior = "";
            var type = GetStr(desc, "type") ?? "";
            var marketRest = GetInt(desc, "market_tradable_restriction");
            DateTime? tradableAfter = null;
            double? floatValue = null;
            var stickers = new List<string>();

            if (desc.ValueKind != JsonValueKind.Undefined && desc.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    var cat = GetStr(tag, "category") ?? "";
                    var name = GetStr(tag, "localized_tag_name") ?? GetStr(tag, "name") ?? "";
                    if (cat.Equals("Rarity", StringComparison.OrdinalIgnoreCase))
                    {
                        rarity = name;
                        var c = GetStr(tag, "color");
                        if (!string.IsNullOrEmpty(c)) rarityColor = c.StartsWith('#') ? c : $"#{c}";
                    }
                    else if (cat.Equals("Exterior", StringComparison.OrdinalIgnoreCase))
                        exterior = name;
                }
            }

            // descriptions: float, stickers, exterior fallback
            if (desc.ValueKind != JsonValueKind.Undefined &&
                desc.TryGetProperty("descriptions", out var dlines) &&
                dlines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in dlines.EnumerateArray())
                {
                    var raw = GetStr(line, "value") ?? "";
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var plain = StripHtml(raw);

                    if (string.IsNullOrEmpty(exterior) &&
                        plain.StartsWith("Exterior:", StringComparison.OrdinalIgnoreCase))
                        exterior = plain["Exterior:".Length..].Trim();

                    // Float Value: 0.123456789  |  Float: 0.12
                    if (floatValue == null &&
                        (plain.Contains("Float Value", StringComparison.OrdinalIgnoreCase) ||
                         plain.StartsWith("Float:", StringComparison.OrdinalIgnoreCase)))
                    {
                        floatValue = TryParseFloat(plain);
                    }

                    // Sticker: Name  /  Sticker: Name (Holo)
                    if (plain.Contains("Sticker:", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var part in plain.Split(["Sticker:", "sticker:"], StringSplitOptions.RemoveEmptyEntries))
                        {
                            var sn = part.Split(['\n', '\r', '<'])[0].Trim().TrimEnd(',', ';');
                            if (sn.Length is > 1 and < 80 && !stickers.Contains(sn, StringComparer.OrdinalIgnoreCase))
                                stickers.Add(sn);
                        }
                    }
                    // Patch: Name (similar)
                    if (plain.Contains("Patch:", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = plain.IndexOf("Patch:", StringComparison.OrdinalIgnoreCase);
                        var sn = plain[(idx + 6)..].Split(['\n', '\r', '<'])[0].Trim();
                        if (sn.Length is > 1 and < 80 && !stickers.Contains(sn, StringComparer.OrdinalIgnoreCase))
                            stickers.Add("Patch: " + sn);
                    }
                }
            }

            // Trade hold / Trade protection check (CS2 7-day trade protection support)
            bool isTradeProtectedTag = false;
            if (desc.ValueKind != JsonValueKind.Undefined)
            {
                // Check tags for "Trade Protected"
                if (desc.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tagsEl.EnumerateArray())
                    {
                        var tagVal = GetStr(tag, "localized_tag_name") ?? GetStr(tag, "name") ?? "";
                        if (tagVal.Contains("Trade Protected", StringComparison.OrdinalIgnoreCase) ||
                            tagVal.Contains("Trade-Protected", StringComparison.OrdinalIgnoreCase))
                        {
                            isTradeProtectedTag = true;
                            break;
                        }
                    }
                }

                var searchArrays = new[] { "owner_descriptions", "descriptions" };
                foreach (var propName in searchArrays)
                {
                    if (desc.TryGetProperty(propName, out var arrayEl) && arrayEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var itemEl in arrayEl.EnumerateArray())
                        {
                            var val = GetStr(itemEl, "value") ?? "";
                            if (val.Contains("trade-protected", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("Trade Protected", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("transferred until", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("Tradable After", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("Available after", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("передаваемым", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("Доступно после", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("можно обменять", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("until", StringComparison.OrdinalIgnoreCase) ||
                                val.Contains("защищен", StringComparison.OrdinalIgnoreCase))
                            {
                                tradableAfter = TryParseTradableAfter(val);
                                if (tradableAfter.HasValue) break;
                            }
                        }
                    }
                    if (tradableAfter.HasValue) break;
                }
            }

            // Fallback for trade-locked skins without explicit date text:
            if (!tradable && !tradableAfter.HasValue && (isTradeProtectedTag || !string.IsNullOrEmpty(exterior) || marketable))
            {
                tradableAfter = DateTime.UtcNow.AddDays(7);
            }

            items.Add(new InventoryItem
            {
                AccountId = _account.Id,
                AccountLogin = _account.Login,
                AssetId = assetId,
                ClassId = classId,
                InstanceId = instanceId,
                MarketHashName = marketHash,
                Name = GetStr(desc, "name") ?? marketHash,
                Type = type,
                Rarity = rarity,
                RarityColor = rarityColor,
                Exterior = exterior,
                ImageUrl = string.IsNullOrEmpty(icon)
                    ? null
                    : $"https://community.cloudflare.steamstatic.com/economy/image/{icon}",
                Tradable = tradable,
                Marketable = marketable,
                Amount = int.TryParse(GetStr(a, "amount") ?? "1", out var am) ? am : 1,
                MarketTradableRestriction = marketRest,
                TradableAfter = tradableAfter,
                FloatValue = floatValue,
                Stickers = stickers
            });
        }

        items.RemoveAll(i => i.IsPermanentlyUntradable);
        return items;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }

    private static double? TryParseFloat(string text)
    {
        // pull first 0.xxxx number
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(0\.\d+)");
        if (!m.Success) return null;
        if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1)
            return f;
        return null;
    }

    public async Task<(string OfferId, string Status)> SendTradeAsync(
        string tradeUrl,
        IReadOnlyList<string> assetIds,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsOnline) throw new InvalidOperationException("Account is offline");
        var parsed = ParseTradeUrl(tradeUrl);

        progress?.Report($"Building offer ({assetIds.Count} items)…");

        var me = new
        {
            assets = assetIds.Select(id => new
            {
                appid = 730,
                contextid = "2",
                amount = 1,
                assetid = id
            }).ToArray(),
            currency = Array.Empty<object>(),
            ready = false
        };
        var them = new { assets = Array.Empty<object>(), currency = Array.Empty<object>(), ready = false };

        var form = new Dictionary<string, string>
        {
            ["sessionid"] = SessionId ?? "",
            ["serverid"] = "1",
            ["partner"] = parsed.PartnerSteam64,
            ["tradeoffermessage"] = $"SilverManager · {assetIds.Count} item(s)",
            ["json_tradeoffer"] = JsonSerializer.Serialize(new
            {
                newversion = true,
                version = 3,
                me,
                them
            }),
            ["captcha"] = "",
            ["trade_offer_create_params"] = string.IsNullOrEmpty(parsed.Token)
                ? "{}"
                : JsonSerializer.Serialize(new { trade_offer_access_token = parsed.Token })
        };

        using var http = CreateHttpClient();
        using var content = new FormUrlEncodedContent(form);
        var referer =
            $"https://steamcommunity.com/tradeoffer/new/?partner={parsed.PartnerAccountId}&token={parsed.Token}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/tradeoffer/new/send");
        req.Content = content;
        req.Headers.Referrer = new Uri(referer);
        req.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");

        progress?.Report("Sending trade offer…");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("strError", out var err) && err.ValueKind == JsonValueKind.String)
        {
            var msg = err.GetString();
            if (!string.IsNullOrEmpty(msg)) throw new Exception(msg);
        }

        var offerId = root.TryGetProperty("tradeofferid", out var tid) ? tid.GetString() : null;
        if (string.IsNullOrEmpty(offerId))
            throw new Exception($"Failed to create trade: {body.AsSpan(0, Math.Min(200, body.Length))}");

        var needsMobile = root.TryGetProperty("needs_mobile_confirmation", out var nmc) && nmc.GetBoolean();
        var status = needsMobile ? "pending" : "sent";

        if (needsMobile)
        {
            progress?.Report($"Confirming offer #{offerId}…");
            await Task.Delay(2000, ct);
            var ok = await ConfirmTradeAsync(offerId, ct);
            status = ok ? "confirmed" : "pending_confirmation";
        }

        return (offerId, status);
    }

    public async Task<bool> ConfirmTradeAsync(string offerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_account.IdentitySecret))
            throw new InvalidOperationException("Missing identity_secret");

        await SteamTotp.AlignTimeAsync(ct);
        var deviceId = SteamTotp.EnsureDeviceId(_account.DeviceId, SteamId64!);
        _account.DeviceId = deviceId;

        // Fetch confirmations — SteamAuth mobileconf/getlist
        var time = SteamTotp.GetSteamTime();
        var confHash = SteamTotp.GenerateConfirmationHash(_account.IdentitySecret, time, "conf");
        var listUrl =
            $"https://steamcommunity.com/mobileconf/getlist?p={Uri.EscapeDataString(deviceId)}" +
            $"&a={SteamId64}&k={confHash}&t={time}&m=react&tag=conf";

        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "com.valvesoftware.android.steam.community");

        var listJson = await http.GetStringAsync(listUrl, ct);
        using var listDoc = JsonDocument.Parse(listJson);
        var listRoot = listDoc.RootElement;
        if (!listRoot.TryGetProperty("success", out var okEl) || !okEl.GetBoolean())
        {
            var msg = listRoot.TryGetProperty("message", out var m) ? m.GetString() : listJson;
            throw new Exception($"Confirmations: {msg}");
        }

        if (!listRoot.TryGetProperty("conf", out var confs))
            return false;

        foreach (var conf in confs.EnumerateArray())
        {
            var creator = conf.TryGetProperty("creator_id", out var c) ? c.ToString() : "";
            // creator_id matches trade offer id for trade confirmations
            if (!string.Equals(creator, offerId, StringComparison.Ordinal))
                continue;

            var cid = conf.GetProperty("id").ToString();
            var key = conf.GetProperty("nonce").ToString();

            var acceptTime = SteamTotp.GetSteamTime();
            var acceptHash = SteamTotp.GenerateConfirmationHash(_account.IdentitySecret, acceptTime, "accept");
            var acceptUrl =
                $"https://steamcommunity.com/mobileconf/ajaxop?op=allow&p={Uri.EscapeDataString(deviceId)}" +
                $"&a={SteamId64}&k={acceptHash}&t={acceptTime}&m=react&tag=accept&cid={cid}&ck={key}";

            var acceptJson = await http.GetStringAsync(acceptUrl, ct);
            using var accDoc = JsonDocument.Parse(acceptJson);
            return accDoc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        }

        // Never approve unrelated pending trade confirmations. The caller may retry this
        // exact offer later; a missing confirmation is not evidence to accept another one.
        return false;
    }

    public async Task<List<ConfirmationItem>> GetConfirmationsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_account.IdentitySecret))
            throw new InvalidOperationException("Missing identity_secret");
        await SteamTotp.AlignTimeAsync(ct);
        var deviceId = SteamTotp.EnsureDeviceId(_account.DeviceId, SteamId64!);
        _account.DeviceId = deviceId;
        var time = SteamTotp.GetSteamTime();
        var confHash = SteamTotp.GenerateConfirmationHash(_account.IdentitySecret, time, "conf");
        var listUrl =
            $"https://steamcommunity.com/mobileconf/getlist?p={Uri.EscapeDataString(deviceId)}" +
            $"&a={SteamId64}&k={confHash}&t={time}&m=react&tag=conf";
        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "com.valvesoftware.android.steam.community");
        var listJson = await http.GetStringAsync(listUrl, ct);
        using var listDoc = JsonDocument.Parse(listJson);
        var root = listDoc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
            throw new Exception(root.TryGetProperty("message", out var m) ? m.GetString() ?? "conf fail" : "conf fail");

        var list = new List<ConfirmationItem>();
        if (!root.TryGetProperty("conf", out var confs)) return list;
        foreach (var conf in confs.EnumerateArray())
        {
            list.Add(new ConfirmationItem
            {
                AccountId = _account.Id,
                AccountLogin = _account.Login,
                ConfId = conf.GetProperty("id").ToString(),
                Key = conf.GetProperty("nonce").ToString(),
                Type = conf.TryGetProperty("type", out var t) ? t.GetInt32() : 0,
                Headline = conf.TryGetProperty("headline", out var h) ? h.GetString() ?? "" : "",
                Summary = conf.TryGetProperty("summary", out var s)
                    ? (s.ValueKind == JsonValueKind.Array
                        ? string.Join(" · ", s.EnumerateArray().Select(x => x.GetString()))
                        : s.GetString() ?? "")
                    : "",
                CreatorId = conf.TryGetProperty("creator_id", out var c) ? c.ToString() : ""
            });
        }
        return list;
    }

    public async Task<bool> RespondConfirmationAsync(string confId, string key, bool accept, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_account.IdentitySecret)) throw new InvalidOperationException("Missing identity_secret");
        await SteamTotp.AlignTimeAsync(ct);
        var deviceId = SteamTotp.EnsureDeviceId(_account.DeviceId, SteamId64!);
        var op = accept ? "allow" : "cancel";
        var tag = accept ? "accept" : "reject";
        var time = SteamTotp.GetSteamTime();
        var hash = SteamTotp.GenerateConfirmationHash(_account.IdentitySecret, time, tag);
        var url =
            $"https://steamcommunity.com/mobileconf/ajaxop?op={op}&p={Uri.EscapeDataString(deviceId)}" +
            $"&a={SteamId64}&k={hash}&t={time}&m=react&tag={tag}&cid={confId}&ck={key}";
        using var http = CreateHttpClient();
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
    }

    /// <summary>
    /// Ensure we have a usable JWT for IEconService / steamLoginSecure.
    /// SteamKit sometimes returns empty AccessToken on poll — derive from refresh token.
    /// </summary>
    public async Task EnsureAccessTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(AccessToken) && AccessToken.Contains('.'))
            return;
        if (string.IsNullOrWhiteSpace(RefreshToken) || _client is null || SteamId64 is null)
            throw new InvalidOperationException("No refresh token — sign in again");

        if (!ulong.TryParse(SteamId64, out var sid))
            throw new InvalidOperationException("Bad SteamID64");

        var steamId = new SteamID(sid);
        var tokens = await _client.Authentication.GenerateAccessTokenForAppAsync(steamId, RefreshToken, allowRenewal: false);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            throw new InvalidOperationException("Steam did not return an access token");
        AccessToken = tokens.AccessToken;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            RefreshToken = tokens.RefreshToken;
        ApplyWebCookies();
    }

    public async Task<List<TradeOfferItem>> GetTradeOffersAsync(CancellationToken ct = default)
    {
        if (!IsOnline) throw new InvalidOperationException("Offline — sign in first");
        await EnsureAccessTokenAsync(ct);

        using var http = CreateHttpClient();
        var api =
            $"https://api.steampowered.com/IEconService/GetTradeOffers/v1/?access_token={Uri.EscapeDataString(AccessToken!)}" +
            "&get_sent_offers=0&get_received_offers=1&active_only=1&historical_only=0&get_descriptions=0&language=english&time_historical_cutoff=";

        string body;
        try
        {
            using var resp = await http.GetAsync(api, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"GetTradeOffers HTTP {(int)resp.StatusCode}: {Trim(body, 160)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new Exception($"GetTradeOffers failed: {ex.Message}", ex);
        }

        var list = new List<TradeOfferItem>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("response", out var respEl))
        {
            // Sometimes API returns { "response": {} } when token is wrong audience
            throw new Exception("GetTradeOffers: empty response (access token may be invalid — re-login)");
        }

        void ParseOffers(JsonElement arr, bool incoming)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            foreach (var o in arr.EnumerateArray())
            {
                var state = o.TryGetProperty("trade_offer_state", out var st)
                    ? (st.ValueKind == JsonValueKind.Number ? st.GetInt32() : int.TryParse(st.ToString(), out var si) ? si : 0)
                    : 0;
                // 2 = Active, 9 = CreatedNeedsConfirmation (still show so user can confirm)
                if (state is not (2 or 9)) continue;

                var their = CountOfferItems(o, "items_to_receive");
                var mine = CountOfferItems(o, "items_to_give");
                var offerId = o.TryGetProperty("tradeofferid", out var tid)
                    ? tid.ToString().Trim('"')
                    : "";
                if (string.IsNullOrEmpty(offerId)) continue;

                var partnerAcc = o.TryGetProperty("accountid_other", out var p)
                    ? p.ToString().Trim('"')
                    : "0";
                var partner64 = ulong.TryParse(partnerAcc, out var accId)
                    ? (accId + 76561197960265728UL).ToString()
                    : "";

                list.Add(new TradeOfferItem
                {
                    AccountId = _account.Id,
                    AccountLogin = _account.Login,
                    OfferId = offerId,
                    PartnerSteam64 = partner64,
                    IsIncoming = incoming,
                    State = state == 9 ? "Needs confirmation" : "Active",
                    TheirItems = their,
                    MyItems = mine,
                    Message = o.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : ""
                });
            }
        }

        if (respEl.TryGetProperty("trade_offers_received", out var recv))
            ParseOffers(recv, true);
        // Optional: sent offers not needed on Incoming page
        if (respEl.TryGetProperty("trade_offers_sent", out var sent))
            ParseOffers(sent, false);

        return list;
    }

    private static int CountOfferItems(JsonElement offer, string prop)
    {
        if (!offer.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return 0;
        var n = 0;
        foreach (var it in arr.EnumerateArray())
        {
            var amount = 1;
            if (it.TryGetProperty("amount", out var a))
            {
                if (a.ValueKind == JsonValueKind.Number) amount = Math.Max(1, a.GetInt32());
                else if (int.TryParse(a.ToString(), out var ai)) amount = Math.Max(1, ai);
            }
            n += amount;
        }
        return n;
    }

    public async Task<bool> AcceptTradeOfferAsync(string offerId, string? partnerSteam64 = null, CancellationToken ct = default)
    {
        ApplyWebCookies();
        using var http = CreateHttpClient();
        var form = new Dictionary<string, string>
        {
            ["sessionid"] = SessionId ?? "",
            ["serverid"] = "1",
            ["tradeofferid"] = offerId,
            ["captcha"] = ""
        };
        if (!string.IsNullOrWhiteSpace(partnerSteam64))
            form["partner"] = partnerSteam64;

        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://steamcommunity.com/tradeoffer/{offerId}/accept");
        req.Content = content;
        req.Headers.Referrer = new Uri($"https://steamcommunity.com/tradeoffer/{offerId}");
        req.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("strError", out var err))
            {
                var msg = err.GetString();
                if (!string.IsNullOrWhiteSpace(msg))
                    throw new Exception(msg);
            }
            if (doc.RootElement.TryGetProperty("needs_mobile_confirmation", out var nmc) && nmc.GetBoolean())
            {
                await Task.Delay(1200, ct);
                return await ConfirmTradeAsync(offerId, ct);
            }
            return doc.RootElement.TryGetProperty("tradeid", out _) || resp.IsSuccessStatusCode;
        }
        catch (JsonException)
        {
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Accept failed HTTP {(int)resp.StatusCode}: {Trim(body, 120)}");
            return resp.IsSuccessStatusCode;
        }
    }

    public async Task<bool> DeclineTradeOfferAsync(string offerId, CancellationToken ct = default)
    {
        using var http = CreateHttpClient();
        var form = new Dictionary<string, string>
        {
            ["sessionid"] = SessionId ?? ""
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync($"https://steamcommunity.com/tradeoffer/{offerId}/decline", content, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Own Unique Trade URL for this account.
    /// Primary path (node-steam-user / bots): <c>Econ.GetTradeOfferAccessToken#1</c> over CM —
    /// no community cookies, no HTML. Fallback: DoctorMcKay getTradeURL HTML scrape.
    /// </summary>
    public async Task<string> GetOwnTradeUrlAsync(CancellationToken ct = default)
    {
        var sid = !string.IsNullOrWhiteSpace(SteamId64) ? SteamId64
            : !string.IsNullOrWhiteSpace(_account.SteamId64) ? _account.SteamId64
            : null;

        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("SteamID64 is unknown — sign in first");

        SteamId64 = sid;
        _account.SteamId64 = sid;

        // ── 1) CM unified message (same as DoctorMcKay/node-steam-user econ.js) ──
        Exception? unifiedErr = null;
        if (IsOnline && _client is { IsConnected: true })
        {
            try
            {
                var viaCm = await GetTradeUrlViaUnifiedAsync(ct);
                if (!string.IsNullOrWhiteSpace(viaCm))
                    return viaCm;
            }
            catch (Exception ex)
            {
                unifiedErr = ex;
            }
        }

        // ── 2) HTML fallback: node-steamcommunity getTradeURL ──
        // Resolve /my → 302 to /profiles/… or /id/…, then GET …/tradeoffers/privacy
        ApplyWebCookies();
        try
        {
            var viaHtml = await GetTradeUrlViaPrivacyPageAsync(sid, ct);
            if (!string.IsNullOrWhiteSpace(viaHtml))
                return viaHtml;
        }
        catch (Exception htmlEx)
        {
            var bits = new List<string>();
            if (unifiedErr != null) bits.Add($"CM: {unifiedErr.Message}");
            bits.Add($"HTML: {htmlEx.Message}");
            throw new Exception(string.Join(" | ", bits), htmlEx);
        }

        throw new Exception(
            (unifiedErr != null ? $"CM: {unifiedErr.Message}. " : "") +
            "Trade URL not found. Sign in again and ensure trade offers are unlocked on the account.");
    }

    /// <summary>
    /// node-steam-user: <c>Econ.GetTradeOfferAccessToken#1</c> → build
    /// <c>https://steamcommunity.com/tradeoffer/new/?partner={accountId}&amp;token={token}</c>
    /// </summary>
    private async Task<string> GetTradeUrlViaUnifiedAsync(CancellationToken ct)
    {
        var unified = _client!.GetHandler<SteamUnifiedMessages>()
            ?? throw new InvalidOperationException("SteamUnifiedMessages handler is missing");

        var econ = unified.CreateService<Econ>();
        var request = new CEcon_GetTradeOfferAccessToken_Request { generate_new_token = false };

        // AsyncJob is awaitable (SteamKit2 sample 013_UnifiedMessages)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var response = await econ.GetTradeOfferAccessToken(request).ToTask().WaitAsync(timeout.Token);

        if (response.Result != EResult.OK)
            throw new Exception($"Econ.GetTradeOfferAccessToken → {response.Result}");

        var token = response.Body.trade_offer_access_token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            // No token yet — same as changeTradeURL: ask Steam to mint one.
            request = new CEcon_GetTradeOfferAccessToken_Request { generate_new_token = true };
            response = await econ.GetTradeOfferAccessToken(request).ToTask().WaitAsync(timeout.Token);
            if (response.Result != EResult.OK)
                throw new Exception($"Econ.GetTradeOfferAccessToken(generate) → {response.Result}");
            token = response.Body.trade_offer_access_token?.Trim();
        }

        if (string.IsNullOrEmpty(token))
            throw new Exception("Steam returned empty trade_offer_access_token");

        if (!ulong.TryParse(SteamId64, out var sid64))
            throw new InvalidOperationException("SteamID64 is not numeric");

        // partner = accountid (32-bit), not steam64 — DoctorMcKay / every trade link
        var accountId = sid64 - 76561197960265728UL;
        return $"https://steamcommunity.com/tradeoffer/new/?partner={accountId}&token={token}";
    }

    /// <summary>
    /// node-steamcommunity getTradeURL: GET /my (no redirect follow) → profile path →
    /// GET {profile}/tradeoffers/privacy → regex partner+token.
    /// </summary>
    private async Task<string> GetTradeUrlViaPrivacyPageAsync(string sid, CancellationToken ct)
    {
        using var http = CreateHttpClient();

        // Resolve profile vanity/path the same way DoctorMcKay does (_myProfile).
        string? profilePath = null;
        try
        {
            using var noRedirectHandler = CreateHandler(allowAutoRedirect: false);
            using var noRedirectHttp = new HttpClient(noRedirectHandler) { Timeout = TimeSpan.FromSeconds(25) };
            noRedirectHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            using var myResp = await noRedirectHttp.GetAsync("https://steamcommunity.com/my", ct);
            if ((int)myResp.StatusCode is >= 300 and < 400
                && myResp.Headers.Location is { } loc)
            {
                var locStr = loc.IsAbsoluteUri ? loc.AbsoluteUri : loc.OriginalString;
                var m = Regex.Match(locStr, @"steamcommunity\.com(/(?:id|profiles)/[^/?#]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                    profilePath = m.Groups[1].Value.TrimEnd('/');
            }
        }
        catch { /* fall through to /profiles/{sid} */ }

        profilePath ??= $"/profiles/{sid}";

        var pages = new[]
        {
            $"https://steamcommunity.com{profilePath}/tradeoffers/privacy",
            $"https://steamcommunity.com/profiles/{sid}/tradeoffers/privacy",
            "https://steamcommunity.com/my/tradeoffers/privacy",
        };

        Exception? last = null;
        foreach (var page in pages)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, page);
                req.Headers.TryAddWithoutValidation("Referer", $"https://steamcommunity.com{profilePath}/");
                using var resp = await http.SendAsync(req, ct);
                var html = await resp.Content.ReadAsStringAsync(ct);

                if (LooksLikeSteamLoginPage(html))
                {
                    last = new Exception($"Community session not accepted (login wall) on {page}");
                    // Refresh cookies + try jwt finalizelogin once (steam-session style)
                    await EnsureCommunityCookiesAsync(ct);
                    continue;
                }

                // DoctorMcKay exact regex first, then our broader extractors.
                var m = Regex.Match(html,
                    @"https?://(?:www\.)?steamcommunity\.com/tradeoffer/new/?\?partner=\d+(?:&|&amp;)token=([a-zA-Z0-9\-_]+)",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var url = m.Value.Replace("&amp;", "&");
                    if (ParseTradeUrlSafe(url, out _))
                        return url;
                }

                if (TryExtractTradeUrl(html, out var extracted))
                    return extracted;

                last = new Exception($"Trade URL not in privacy HTML (HTTP {(int)resp.StatusCode})");
            }
            catch (Exception ex) { last = ex; }
        }

        // Last resort like changeTradeURL: POST newtradeurl
        try
        {
            var minted = await MintTradeUrlViaNewTradeUrlAsync(http, profilePath, sid, ct);
            if (!string.IsNullOrWhiteSpace(minted))
                return minted;
        }
        catch (Exception ex)
        {
            last = ex;
        }

        throw last ?? new Exception("Trade URL not found on privacy page");
    }

    /// <summary>node-steamcommunity changeTradeURL: POST {profile}/tradeoffers/newtradeurl</summary>
    private async Task<string?> MintTradeUrlViaNewTradeUrlAsync(HttpClient http, string profilePath, string sid, CancellationToken ct)
    {
        ApplyWebCookies();
        var form = new Dictionary<string, string> { ["sessionid"] = SessionId ?? "" };
        using var content = new FormUrlEncodedContent(form);
        var endpoints = new[]
        {
            $"https://steamcommunity.com{profilePath}/tradeoffers/newtradeurl",
            $"https://steamcommunity.com/profiles/{sid}/tradeoffers/newtradeurl",
        };
        foreach (var ep in endpoints)
        {
            using var resp = await http.PostAsync(ep, content, ct);
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            // Response is "\"token\"" (quoted string)
            var token = body.Trim().Trim('"').Trim();
            if (token.Length is >= 6 and <= 32 && Regex.IsMatch(token, @"^[a-zA-Z0-9\-_]+$"))
            {
                if (!ulong.TryParse(sid, out var sid64)) continue;
                var accountId = sid64 - 76561197960265728UL;
                return $"https://steamcommunity.com/tradeoffer/new/?partner={accountId}&token={token}";
            }
        }
        return null;
    }

    /// <summary>
    /// steam-session getWebCookies for browser: POST login.steampowered.com/jwt/finalizelogin
    /// with refresh token, then transfer set-cookie to community. Falls back to manual cookie.
    /// </summary>
    private async Task EnsureCommunityCookiesAsync(CancellationToken ct)
    {
        ApplyWebCookies();
        if (string.IsNullOrEmpty(RefreshToken) || string.IsNullOrEmpty(SteamId64))
            return;

        try
        {
            SessionId ??= Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
                .ToLowerInvariant();

            using var handler = new HttpClientHandler
            {
                CookieContainer = Cookies,
                UseCookies = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            var proxyStr = !string.IsNullOrWhiteSpace(_account.Proxy) ? _account.Proxy : GlobalDefaultProxy;
            var proxy = ProxyHelper.TryCreate(proxyStr);
            if (proxy != null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://steamcommunity.com");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://steamcommunity.com/");

            using var form = new MultipartFormDataContent
            {
                { new StringContent(RefreshToken), "nonce" },
                { new StringContent(SessionId), "sessionid" },
                { new StringContent("https://steamcommunity.com/login/home/?goto="), "redir" }
            };
            using var finalize = await http.PostAsync("https://login.steampowered.com/jwt/finalizelogin", form, ct);
            var json = await finalize.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (!doc.RootElement.TryGetProperty("transfer_info", out var transfers)
                || transfers.ValueKind != JsonValueKind.Array)
            {
                ApplyWebCookies();
                return;
            }

            foreach (var t in transfers.EnumerateArray())
            {
                if (!t.TryGetProperty("url", out var urlEl)) continue;
                var url = urlEl.GetString();
                if (string.IsNullOrEmpty(url)) continue;
                if (!t.TryGetProperty("params", out var p)) continue;

                var fields = new Dictionary<string, string>();
                foreach (var prop in p.EnumerateObject())
                    fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.ToString();

                using var transferContent = new FormUrlEncodedContent(fields);
                try { await http.PostAsync(url, transferContent, ct); }
                catch { /* one domain may fail */ }
            }

            // Keep sessionid stable across domains
            ApplyWebCookies();
        }
        catch
        {
            ApplyWebCookies();
        }
    }

    private HttpClientHandler CreateHandler(bool allowAutoRedirect)
    {
        var proxyStr = !string.IsNullOrWhiteSpace(_account.Proxy) ? _account.Proxy : GlobalDefaultProxy;
        var proxy = ProxyHelper.TryCreate(proxyStr);
        return new HttpClientHandler
        {
            CookieContainer = Cookies,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            AllowAutoRedirect = allowAutoRedirect,
            Proxy = proxy,
            UseProxy = proxy != null,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }

    private static bool LooksLikeSteamLoginPage(string html)
    {
        if (string.IsNullOrEmpty(html)) return true;
        // Logged-out privacy page is a login wall, not the token input.
        return html.Contains("signInForm", StringComparison.OrdinalIgnoreCase)
               || html.Contains("login_form", StringComparison.OrdinalIgnoreCase)
               || (html.Contains("g_steamID = false", StringComparison.OrdinalIgnoreCase))
               || html.Contains("javascript:Login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pull Unique Trade URL from privacy HTML. Steam puts it in
    /// <c>input#trade_offer_access_url</c> (and sometimes JS / plain text).
    /// </summary>
    private static bool TryExtractTradeUrl(string html, out string url)
    {
        url = "";
        if (string.IsNullOrEmpty(html)) return false;

        // 1) Canonical input field on /tradeoffers/privacy
        //    <input ... id="trade_offer_access_url" value="https://steamcommunity.com/tradeoffer/new/?partner=…&token=…">
        var patterns = new[]
        {
            @"id\s*=\s*[""']trade_offer_access_url[""'][^>]*value\s*=\s*[""']([^""']+)[""']",
            @"value\s*=\s*[""']([^""']+)[""'][^>]*id\s*=\s*[""']trade_offer_access_url[""']",
            @"name\s*=\s*[""']trade_offer_access_url[""'][^>]*value\s*=\s*[""']([^""']+)[""']",
            // JS / data attributes
            @"trade_offer_access_url[""']?\s*[:=]\s*[""'](https?://[^""']*tradeoffer/new/\?[^""']+)[""']",
            // Plain full URL (escaped or not)
            @"https?://steamcommunity\.com/tradeoffer/new/\?partner=\d+(?:&amp;|&)token=[\w\-]+",
            // Protocol-relative
            @"//steamcommunity\.com/tradeoffer/new/\?partner=\d+(?:&amp;|&)token=[\w\-]+",
            // Relative
            @"/tradeoffer/new/\?partner=\d+(?:&amp;|&)token=[\w\-]+",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(html, p, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) continue;
            var raw = (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value)
                .Replace("&amp;", "&")
                .Replace("\\u0026", "&")
                .Replace("\\/", "/")
                .Trim();

            if (raw.StartsWith("//", StringComparison.Ordinal))
                raw = "https:" + raw;
            else if (raw.StartsWith("/tradeoffer", StringComparison.OrdinalIgnoreCase))
                raw = "https://steamcommunity.com" + raw;
            else if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                     && raw.Contains("partner=", StringComparison.OrdinalIgnoreCase))
                raw = "https://steamcommunity.com/tradeoffer/new/?" + raw.TrimStart('?', '&');

            // Validate shape
            if (ParseTradeUrlSafe(raw, out _))
            {
                url = raw;
                return true;
            }
        }

        // 2) Last resort: partner + token as separate fields on the page
        var partner = Regex.Match(html, @"partner=(\d{5,})", RegexOptions.IgnoreCase);
        var token = Regex.Match(html, @"[?&]token=([\w\-]{6,})", RegexOptions.IgnoreCase);
        if (!token.Success)
            token = Regex.Match(html, @"[""']token[""']\s*[:=]\s*[""']([\w\-]{6,})[""']", RegexOptions.IgnoreCase);
        if (partner.Success && token.Success)
        {
            url = $"https://steamcommunity.com/tradeoffer/new/?partner={partner.Groups[1].Value}&token={token.Groups[1].Value}";
            return true;
        }

        return false;
    }

    private static bool ParseTradeUrlSafe(string tradeUrl, out TradeUrlInfo info)
    {
        info = default;
        try
        {
            info = ParseTradeUrl(tradeUrl);
            return true;
        }
        catch { return false; }
    }

    public readonly record struct TradeUrlInfo(string PartnerAccountId, string PartnerSteam64, string Token);

    public static TradeUrlInfo ParseTradeUrl(string tradeUrl)
    {
        var m = Regex.Match(tradeUrl.Trim(),
            @"steamcommunity\.com/tradeoffer/new/\?partner=(\d+)(?:&|&amp;)token=([\w-]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) throw new ArgumentException("Invalid Steam Trade Link");

        var accountId = m.Groups[1].Value;
        var steam64 = (ulong.Parse(accountId) + 76561197960265728UL).ToString();
        return new TradeUrlInfo(accountId, steam64, m.Groups[2].Value);
    }

    private static DateTime? TryParseTradableAfter(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = Regex.Match(text,
            @"(?:transferred until|until|Tradable/Marketable After|Tradable After|Available after|Can be traded after|передаваемым после|Доступно после|можно будет передать после|можно обменять после|после|до)\s+(.+?)(?:\s*GMT)?\s*$",
            RegexOptions.IgnoreCase);

        var raw = m.Success ? m.Groups[1].Value.Trim() : text;
        raw = Regex.Replace(raw, @"\s+GMT\s*$", "", RegexOptions.IgnoreCase).Trim();
        raw = raw.Replace("(", "").Replace(")", "").Trim();

        // Translate Russian month abbreviations to English for DateTime.TryParseExact
        raw = Regex.Replace(raw, @"\bянв\w*\b", "Jan", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bфев\w*\b", "Feb", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bмар\w*\b", "Mar", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bапр\w*\b", "Apr", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bмай\w*\b|\bмая\b", "May", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bиюн\w*\b", "Jun", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bиюл\w*\b", "Jul", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bавг\w*\b", "Aug", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bсен\w*\b", "Sep", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bокт\w*\b", "Oct", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bноя\w*\b", "Nov", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, @"\bдек\w*\b", "Dec", RegexOptions.IgnoreCase);

        string[] formats =
        [
            "M/d/yyyy, h:mm:ss tt",
            "M/d/yyyy h:mm:ss tt",
            "MM/dd/yyyy, hh:mm:ss tt",
            "MM/dd/yyyy hh:mm:ss tt",
            "dd.MM.yyyy, HH:mm:ss",
            "dd.MM.yyyy HH:mm:ss",
            "d.M.yyyy, H:mm:ss",
            "d.M.yyyy H:mm:ss",
            "ddd MMM dd HH:mm:ss yyyy",
            "MMM dd, yyyy HH:mm:ss",
            "MMM dd yyyy HH:mm:ss",
            "dd MMM yyyy HH:mm:ss",
            "MMM dd, yyyy",
            "MMM dd yyyy",
            "dd MMM yyyy",
            "yyyy-MM-dd HH:mm:ss"
        ];

        if (DateTime.TryParseExact(raw, formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return dt;

        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out dt))
            return dt.ToUniversalTime();

        return null;
    }

    private static string? GetStr(JsonElement el, string name)
    {
        if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return 0;
        if (!el.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.GetInt32();
        return int.TryParse(p.GetString(), out var i) ? i : 0;
    }

    /// <summary>Graceful logoff + drop TCP/callback loop (free RAM/sockets).</summary>
    public void Logout()
    {
        lock (_gate)
            DisposeClient();
        AccessToken = null;
        RefreshToken = null;
        SessionId = null;
    }

    private void DisposeClient()
    {
        try { _callbackCts?.Cancel(); } catch { /* */ }
        try { _user?.LogOff(); } catch { /* */ }
        try { _client?.Disconnect(); } catch { /* */ }
        _callbackCts?.Dispose();
        _callbackCts = null;
        _client = null;
        _manager = null;
        _user = null;
        IsOnline = false;
    }

    public void Dispose()
    {
        Logout();
        GC.SuppressFinalize(this);
    }
}

public sealed class SessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SteamSession> _sessions = new();

    public SteamSession GetOrCreate(SteamAccount account) =>
        _sessions.GetOrAdd(account.Id, _ => new SteamSession(account));

    public SteamSession? TryGet(string accountId) =>
        _sessions.TryGetValue(accountId, out var s) ? s : null;

    public int ActiveCount => _sessions.Count(kv => kv.Value.IsOnline);

    public void Remove(string accountId)
    {
        if (_sessions.TryRemove(accountId, out var s))
            s.Dispose();
    }

    /// <summary>Drop session for one account (logoff + dispose). Safe if missing.</summary>
    public bool Release(string accountId)
    {
        if (!_sessions.TryRemove(accountId, out var s)) return false;
        try { s.Logout(); } catch { /* */ }
        try { s.Dispose(); } catch { /* */ }
        return true;
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
