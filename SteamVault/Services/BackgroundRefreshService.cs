using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Periodic account review while app is open (SAM Background refresh).
/// </summary>
public sealed class BackgroundRefreshService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AccountReviewService _review = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string, LogLevel>? Log;
    public event Action<SteamAccount>? AccountUpdated;
    public event Action? CycleCompleted;

    public bool IsRunning => _loop is { IsCompleted: false };

    public BackgroundRefreshService(AppSettings settings) => _settings = settings;

    public void Start(
        Func<IReadOnlyList<SteamAccount>> accountsProvider,
        Func<SteamAccount, SteamSession?> sessionResolver)
    {
        Stop();
        if (!_settings.BackgroundRefreshEnabled) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            // first delay so UI settles
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            catch { return; }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(accountsProvider, sessionResolver, token);
                    CycleCompleted?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke($"BG refresh error: {ex.Message}", LogLevel.Warning);
                }

                var minutes = Math.Clamp(_settings.BackgroundRefreshMinutes, 5, 24 * 60);
                try { await Task.Delay(TimeSpan.FromMinutes(minutes), token); }
                catch { break; }
            }
        }, token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task RunCycleAsync(
        Func<IReadOnlyList<SteamAccount>> accountsProvider,
        Func<SteamAccount, SteamSession?> sessionResolver,
        CancellationToken ct = default)
    {
        var accounts = accountsProvider().ToList();
        if (accounts.Count == 0) return;

        Log?.Invoke($"Background refresh: {accounts.Count} accounts…", LogLevel.Info);

        var progress = new Progress<string>(m => Log?.Invoke(m, LogLevel.Info));
        await _review.ReviewManyAsync(
            accounts,
            _settings.SteamWebApiKey,
            sessionResolver,
            includeGcpd: _settings.IncludeCs2GcpdInReview,
            progress,
            ct);

        foreach (var a in accounts)
        {
            if (a.Review?.BanChanged == true && _settings.NotifyOnBanChange)
                Log?.Invoke($"⚠ BAN CHANGE: {a.Login} · {a.Review.BadgeSummary}", LogLevel.Error);
            AccountUpdated?.Invoke(a);
        }

        Log?.Invoke("Background refresh: complete", LogLevel.Success);
    }

    public void Dispose() => Stop();
}
