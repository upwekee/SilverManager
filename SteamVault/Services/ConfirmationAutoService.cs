using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Background auto-accept of mobile confirmations (market / trusted trades).
/// </summary>
public sealed class ConfirmationAutoService : IDisposable
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string, LogLevel>? Log;

    public ConfirmationAutoService(AppSettings settings) => _settings = settings;

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start(
        Func<IReadOnlyList<SteamAccount>> accounts,
        Func<SteamAccount, SteamSession?> sessions)
    {
        Stop();
        // Unknown trade confirmations are intentionally never auto-approved.
        if (!_settings.AutoConfirmMarket)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await CycleAsync(accounts, sessions, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke($"AutoConf: {ex.Message}", LogLevel.Warning);
                }

                var sec = Math.Clamp(_settings.AutoConfirmIntervalSeconds, 15, 600);
                try { await Task.Delay(TimeSpan.FromSeconds(sec), token).ConfigureAwait(false); }
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

    public async Task<int> CycleAsync(
        Func<IReadOnlyList<SteamAccount>> accounts,
        Func<SteamAccount, SteamSession?> sessions,
        CancellationToken ct = default)
    {
        var accepted = 0;
        foreach (var acc in accounts())
        {
            if (acc.IsBlocked) continue;
            var s = sessions(acc);
            if (s is not { IsOnline: true }) continue;
            if (string.IsNullOrEmpty(acc.IdentitySecret)) continue;

            List<ConfirmationItem> confs;
            try { confs = await s.GetConfirmationsAsync(ct).ConfigureAwait(false); }
            catch { continue; }

            foreach (var c in confs)
            {
                var allow = false;
                // type 3 = market listing (Steam mobile conf types)
                if (_settings.AutoConfirmMarket && c.Type == 3)
                    allow = true;
                // type 2 (trade) must stay pending for an exact-offer/manual check.
                // creator_id is an offer id, not a verified counterparty identity.
                if (!allow) continue;
                try
                {
                    if (await s.RespondConfirmationAsync(c.ConfId, c.Key, true, ct).ConfigureAwait(false))
                    {
                        accepted++;
                        Log?.Invoke($"{acc.Login}: auto-conf {c.TypeLabel} · {c.Headline}", LogLevel.Success);
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"{acc.Login}: conf fail {ex.Message}", LogLevel.Warning);
                }
                await Task.Delay(800, ct).ConfigureAwait(false);
            }
        }
        return accepted;
    }

    public void Dispose() => Stop();
}
