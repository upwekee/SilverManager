using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace SteamVault.Converters;

/// <summary>
/// Loads remote images with a simple memory cache (Steam CDN icons).
/// </summary>
public sealed class UrlBitmapConverter : IValueConverter
{
    public static readonly UrlBitmapConverter Instance = new();

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> InFlight = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var url = value as string;
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Cache.TryGetValue(url, out var cached))
            return cached;

        if (InFlight.TryAdd(url, 0))
        {
            _ = LoadAsync(url);
        }

        return null;
    }

    private static async Task LoadAsync(string url)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(url);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new Bitmap(ms);
            Cache[url] = bmp;
            // force UI refresh by re-setting is heavy; Avalonia won't auto-refresh converter.
            // Items re-render on property change — trigger dummy on UI if needed later.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // no-op: next layout pass / scroll may pick it up; also store for rebinds
            });
        }
        catch
        {
            Cache[url] = null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
