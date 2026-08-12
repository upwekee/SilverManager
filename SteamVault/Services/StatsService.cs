using System.Collections.ObjectModel;
using System.Text.Json;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Local statistics: snapshots + event counters for charts/periods.
/// </summary>
public sealed class StatsService
{
    private readonly string _path;
    private readonly string _totalsPath;
    private readonly List<StatsSnapshot> _history = new();
    private readonly object _lock = new();

    /// <summary>Lifetime total value of items successfully sent out (trades).</summary>
    public decimal LifetimeWithdrawnUsd { get; private set; }
    public int LifetimeWithdrawnItems { get; private set; }
    /// <summary>This app session only.</summary>
    public decimal SessionWithdrawnUsd { get; private set; }
    public int SessionWithdrawnItems { get; private set; }

    public StatsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamVault");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "stats.json");
        _totalsPath = Path.Combine(dir, "stats-totals.json");
        Load();
        LoadTotals();
    }

    public void RecordSnapshot(IEnumerable<SteamAccount> accounts, TradeAuditService? audit = null)
    {
        var list = accounts.ToList();
        var snap = new StatsSnapshot
        {
            Time = DateTime.Now,
            AccountCount = list.Count,
            BannedCount = list.Count(a => a.IsBlocked || a.HasBanFlag),
            OnlineCount = list.Count(a => a.Status == AccountStatus.Online),
            PortfolioUsd = list.Sum(a => a.InventoryValue),
        };

        if (audit != null)
        {
            var day = audit.Entries.Where(e => e.Time >= DateTime.Now.Date).ToList();
            snap.TradesOk = day.Count(e => e.Kind is "trade" or "conf-accept" or "accept" or "auto-accept");
            snap.TradesFail = day.Count(e => e.Kind is "error" or "trade-fail" or "skip-ban");
            snap.ItemsMoved = day.Where(e => e.Kind == "trade").Sum(e => ParseItems(e.Detail));
            snap.VolumeUsd = day.Where(e => e.ValueUsd.HasValue).Sum(e => e.ValueUsd!.Value);
        }

        lock (_lock)
        {
            _history.Add(snap);
            // keep ~90 days of hourly-ish data, cap size
            if (_history.Count > 5000)
                _history.RemoveRange(0, _history.Count - 4000);
            Save();
        }
    }

    public void RecordTradeEvent(bool ok, int items, decimal value)
    {
        // lightweight append as a micro-snapshot delta via last point
        lock (_lock)
        {
            if (ok && (items > 0 || value > 0))
            {
                LifetimeWithdrawnUsd += value;
                LifetimeWithdrawnItems += items;
                SessionWithdrawnUsd += value;
                SessionWithdrawnItems += items;
                SaveTotals();
            }

            var last = _history.LastOrDefault() ?? new StatsSnapshot { Time = DateTime.Now };
            var n = new StatsSnapshot
            {
                Time = DateTime.Now,
                AccountCount = last.AccountCount,
                BannedCount = last.BannedCount,
                OnlineCount = last.OnlineCount,
                // portfolio after withdraw is updated by caller via RecordSnapshot
                PortfolioUsd = Math.Max(0, last.PortfolioUsd - (ok ? value : 0)),
                TradesOk = last.TradesOk + (ok ? 1 : 0),
                TradesFail = last.TradesFail + (ok ? 0 : 1),
                ItemsMoved = last.ItemsMoved + (ok ? items : 0),
                VolumeUsd = last.VolumeUsd + (ok ? value : 0)
            };
            _history.Add(n);
            Save();
        }
    }

    public IReadOnlyList<StatsSnapshot> GetRange(StatsPeriod period)
    {
        var from = period switch
        {
            StatsPeriod.Hours24 => DateTime.Now.AddHours(-24),
            StatsPeriod.Days7 => DateTime.Now.AddDays(-7),
            StatsPeriod.Days30 => DateTime.Now.AddDays(-30),
            _ => DateTime.MinValue
        };
        lock (_lock)
            return _history.Where(h => h.Time >= from).OrderBy(h => h.Time).ToList();
    }

    public List<ChartBar> BuildPortfolioBars(StatsPeriod period)
    {
        var data = GetRange(period);
        if (data.Count == 0) return [];

        // bucket by hour / day depending on period
        IEnumerable<IGrouping<string, StatsSnapshot>> groups = period switch
        {
            StatsPeriod.Hours24 => data.GroupBy(d => d.Time.ToString("HH:00")),
            StatsPeriod.Days7 => data.GroupBy(d => d.Time.ToString("ddd")),
            StatsPeriod.Days30 => data.GroupBy(d => d.Time.ToString("dd.MM")),
            _ => data.GroupBy(d => d.Time.ToString("MM.yy"))
        };

        var bars = groups.Select(g =>
        {
            var last = g.Last();
            return new ChartBar
            {
                Label = g.Key,
                Value = (double)last.PortfolioUsd,
                ValueText = $"${last.PortfolioUsd:0.##}",
                Tip = $"${last.PortfolioUsd:0.00} · bans {last.BannedCount}"
            };
        }).ToList();

        var max = bars.Max(b => b.Value);
        if (max <= 0) max = 1;
        foreach (var b in bars)
            b.Normalized = b.Value / max;
        return bars;
    }

    public List<ChartBar> BuildVolumeBars(StatsPeriod period)
    {
        var data = GetRange(period);
        if (data.Count == 0) return [];
        var bars = Bucket(period, data, g => Math.Max(0, g.Last().VolumeUsd - g.First().VolumeUsd),
            (value, g) => $"${value:0.00} · {g.Last().ItemsMoved - g.First().ItemsMoved} items",
            v => $"${v:0.##}");
        Normalize(bars);
        return bars;
    }

    public decimal GetPortfolioDelta(StatsPeriod period)
    {
        var data = GetRange(period);
        return data.Count < 2 ? 0 : data.Last().PortfolioUsd - data.First().PortfolioUsd;
    }

    private static List<ChartBar> Bucket(StatsPeriod period, IReadOnlyList<StatsSnapshot> data,
        Func<IGrouping<string, StatsSnapshot>, decimal> value, Func<decimal, IGrouping<string, StatsSnapshot>, string> tip,
        Func<decimal, string>? valueText = null)
    {
        IEnumerable<IGrouping<string, StatsSnapshot>> groups = period switch
        {
            StatsPeriod.Hours24 => data.GroupBy(d => d.Time.ToString("HH:00")),
            StatsPeriod.Days7 => data.GroupBy(d => d.Time.ToString("ddd")),
            StatsPeriod.Days30 => data.GroupBy(d => d.Time.ToString("dd.MM")),
            _ => data.GroupBy(d => d.Time.ToString("MM.yy"))
        };
        return groups.Select(g =>
        {
            var v = value(g);
            // ValueText only labels the peak bar, so it stays empty unless a formatter is supplied
            return new ChartBar { Label = g.Key, Value = (double)v, Tip = tip(v, g), ValueText = valueText?.Invoke(v) ?? "" };
        }).ToList();
    }

    private static void Normalize(List<ChartBar> bars)
    {
        var max = Math.Max(1, bars.Count == 0 ? 1 : bars.Max(b => Math.Abs(b.Value)));
        foreach (var b in bars) b.Normalized = Math.Abs(b.Value) / max;
    }

    public List<ChartBar> BuildTradeBars(StatsPeriod period)
    {
        var data = GetRange(period);
        if (data.Count == 0) return [];

        IEnumerable<IGrouping<string, StatsSnapshot>> groups = period switch
        {
            StatsPeriod.Hours24 => data.GroupBy(d => d.Time.ToString("HH:00")),
            StatsPeriod.Days7 => data.GroupBy(d => d.Time.ToString("ddd")),
            StatsPeriod.Days30 => data.GroupBy(d => d.Time.ToString("dd.MM")),
            _ => data.GroupBy(d => d.Time.ToString("MM.yy"))
        };

        var bars = groups.Select(g =>
        {
            var ok = g.Max(x => x.TradesOk);
            var fail = g.Max(x => x.TradesFail);
            // show successes primarily
            return new ChartBar
            {
                Label = g.Key,
                Value = ok,
                ValueText = ok.ToString(),
                Tip = $"ok {ok} · fail {fail}"
            };
        }).ToList();

        var max = Math.Max(1, bars.Max(b => b.Value));
        foreach (var b in bars) b.Normalized = b.Value / max;
        return bars;
    }

    public (decimal portfolio, int bans, int tradesOk, int tradesFail, int items, decimal volume) Summarize(StatsPeriod period)
    {
        var data = GetRange(period);
        if (data.Count == 0) return (0, 0, 0, 0, 0, 0);
        var last = data.Last();
        var first = data.First();
        return (
            last.PortfolioUsd,
            last.BannedCount,
            Math.Max(0, last.TradesOk - first.TradesOk),
            Math.Max(0, last.TradesFail - first.TradesFail),
            Math.Max(0, last.ItemsMoved - first.ItemsMoved),
            Math.Max(0, last.VolumeUsd - first.VolumeUsd)
        );
    }

    private static int ParseItems(string detail)
    {
        // "confirmed · 12 items"
        var m = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"(\d+)\s*item");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<StatsSnapshot>>(File.ReadAllText(_path));
            if (list != null) _history.AddRange(list);
        }
        catch { /* */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { /* */ }
    }

    private sealed class TotalsDto
    {
        public decimal LifetimeWithdrawnUsd { get; set; }
        public int LifetimeWithdrawnItems { get; set; }
    }

    private void LoadTotals()
    {
        try
        {
            if (!File.Exists(_totalsPath)) return;
            var t = JsonSerializer.Deserialize<TotalsDto>(File.ReadAllText(_totalsPath));
            if (t == null) return;
            LifetimeWithdrawnUsd = t.LifetimeWithdrawnUsd;
            LifetimeWithdrawnItems = t.LifetimeWithdrawnItems;
        }
        catch { /* */ }
    }

    private void SaveTotals()
    {
        try
        {
            File.WriteAllText(_totalsPath, JsonSerializer.Serialize(new TotalsDto
            {
                LifetimeWithdrawnUsd = LifetimeWithdrawnUsd,
                LifetimeWithdrawnItems = LifetimeWithdrawnItems
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* */ }
    }
}
