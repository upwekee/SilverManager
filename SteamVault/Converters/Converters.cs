using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SteamVault.Models;

namespace SteamVault.Converters;

public static class AppConverters
{
    public static readonly IValueConverter BoolToOpacity = new FuncValueConverter<bool, double>(b => b ? 1.0 : 0.35);
    public static readonly IValueConverter InvertBool = new FuncValueConverter<bool, bool>(b => !b);
    public static readonly IValueConverter NullToFalse = new FuncValueConverter<object?, bool>(v => v != null);

    public static readonly IValueConverter StatusToBrush = new FuncValueConverter<AccountStatus, IBrush>(s => s switch
    {
        AccountStatus.Online => SolidColorBrush.Parse("#FFFFFF"),
        AccountStatus.Connecting or AccountStatus.Busy => SolidColorBrush.Parse("#C9C9CF"),
        AccountStatus.Error => SolidColorBrush.Parse("#E0575F"),
        _ => SolidColorBrush.Parse("#3A3A42")
    });

    /// <summary>Status-bar dot: bright while working, muted at rest.</summary>
    public static readonly IValueConverter BusyDot = new FuncValueConverter<bool, IBrush>(b =>
        b ? SolidColorBrush.Parse("#EDEDF0") : SolidColorBrush.Parse("#3A3A42"));

    public static readonly IValueConverter LogLevelToBrush = new FuncValueConverter<LogLevel, IBrush>(l => l switch
    {
        LogLevel.Success => SolidColorBrush.Parse("#FFFFFF"),
        LogLevel.Error => SolidColorBrush.Parse("#E0575F"),
        LogLevel.Warning => SolidColorBrush.Parse("#C9C9CF"),
        _ => SolidColorBrush.Parse("#6C6C75")
    });

    public static readonly IValueConverter Money = new FuncValueConverter<decimal, string>(v => $"${v:0.00}");
    public static readonly IValueConverter HexBrush = new FuncValueConverter<string?, IBrush>(hex =>
    {
        if (string.IsNullOrWhiteSpace(hex)) return SolidColorBrush.Parse("#EDEDF0");
        try { return SolidColorBrush.Parse(hex.StartsWith('#') ? hex : "#" + hex); }
        catch { return SolidColorBrush.Parse("#EDEDF0"); }
    });

    public static readonly IValueConverter SelectedBg = new FuncValueConverter<bool, IBrush>(b =>
        b ? SolidColorBrush.Parse("#1E1E22") : SolidColorBrush.Parse("#151517"));

    public static readonly IValueConverter SelectedBorder = new FuncValueConverter<bool, IBrush>(b =>
        b ? SolidColorBrush.Parse("#6C6C75") : SolidColorBrush.Parse("#26262C"));

    private const string RarityFallback = "#6C6C75";

    private static string? NormalizeRarityHex(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        var hex = c.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        // Steam sends RRGGBB; reject anything we cannot parse rather than throwing per row.
        if (hex.Length is not (7 or 9)) return null;
        try { SolidColorBrush.Parse(hex); return hex; }
        catch { return null; }
    }

    /// <summary>Official CS item-quality colours, matched loosely against the localized tag.</summary>
    private static string? RarityHexFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.ToLowerInvariant();
        if (n.Contains("contraband")) return "#E4AE39";
        if (n.Contains("covert") || n.Contains("extraordinary")) return "#EB4B4B";
        if (n.Contains("classified") || n.Contains("exotic")) return "#D32CE6";
        if (n.Contains("restricted") || n.Contains("remarkable")) return "#8847FF";
        if (n.Contains("mil-spec") || n.Contains("milspec") || n.Contains("high grade")) return "#4B69FF";
        if (n.Contains("industrial")) return "#5E98D9";
        if (n.Contains("consumer") || n.Contains("base grade")) return "#B0C3D9";
        return null;
    }

    /// <summary>
    /// Accepts either a Steam hex tag colour or a localized rarity name and resolves both,
    /// so a single binding drives the accent.
    /// </summary>
    public static readonly IValueConverter RarityAccent = new FuncValueConverter<string?, IBrush>(v =>
        SolidColorBrush.Parse(NormalizeRarityHex(v) ?? RarityHexFromName(v) ?? RarityFallback));

    public static readonly IValueConverter NavIndexToInventory = new FuncValueConverter<int, bool>(i => i == 0);
    public static readonly IValueConverter NavIndexToTrade = new FuncValueConverter<int, bool>(i => i == 1);
    public static readonly IValueConverter ZeroToTrue = new FuncValueConverter<int, bool>(i => i == 0);
    public static readonly IValueConverter NonZeroToTrue = new FuncValueConverter<int, bool>(i => i > 0);
    public static readonly IValueConverter StringNotEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>Colour readiness by meaning, ranked by brightness: white ready,
    /// silver fixable, grey pending, red blocked.</summary>
    public static readonly IValueConverter ReadinessBrush = new FuncValueConverter<string?, IBrush>(r => r switch
    {
        "Ready" => SolidColorBrush.Parse("#FFFFFF"),
        "Blocked" => SolidColorBrush.Parse("#E0575F"),
        "Proxy failed" => SolidColorBrush.Parse("#E0575F"),
        "No maFile" or "Missing maFile" => SolidColorBrush.Parse("#C9C9CF"),
        "Not scanned" or "Empty inventory" or "Load inventory" => SolidColorBrush.Parse("#A2A2A9"),
        null or "" => SolidColorBrush.Parse("#6C6C75"),
        _ => SolidColorBrush.Parse("#C9C9CF")
    });

    /// <summary>True = bright OK, false = muted warning.</summary>
    public static readonly IValueConverter ReadyStatusBrush = new FuncValueConverter<bool, IBrush>(ok =>
        ok ? SolidColorBrush.Parse("#EDEDF0") : SolidColorBrush.Parse("#E0575F"));

    public static readonly IValueConverter NavIs = new NavIndexEqualsConverter();

    public static readonly IValueConverter AdvancedLabel =
        new FuncValueConverter<bool, string>(v => v ? "▾ Hide advanced" : "▸ Advanced tools");
    public static readonly IValueConverter AdvancedChevron =
        new FuncValueConverter<bool, string>(v => v ? "▾" : "▸");

    public static readonly IValueConverter TransitionOpacity =
        new FuncValueConverter<bool, double>(v => v ? 0.92 : 1.0);

    /// <summary>BarRatio 0..1 × ConverterParameter (max px width) → Width.</summary>
    public static readonly IValueConverter RatioToWidth = new RatioWidthConverter();
}

/// <summary>Converts 0..1 ratio to pixel width using ConverterParameter as max width.</summary>
public sealed class RatioWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            _ => 0d
        };
        var max = 100d;
        if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            max = p;
        return Math.Max(2, Math.Clamp(ratio, 0, 1) * max);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Highlights the active segmented filter pill by comparing the current filter name.</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NavIndexEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is int nav && parameter != null && int.TryParse(parameter.ToString(), out var want))
            return nav == want;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
