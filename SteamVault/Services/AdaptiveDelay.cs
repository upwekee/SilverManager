namespace SteamVault.Services;

/// <summary>Backoff delays when Steam rate-limits or errors spike.</summary>
public sealed class AdaptiveDelay
{
    private int _baseMs;
    private int _currentMs;
    private int _streak;

    public AdaptiveDelay(int baseMs = 2200)
    {
        _baseMs = Math.Max(400, baseMs);
        _currentMs = _baseMs;
    }

    public int CurrentMs => _currentMs;

    public void OnSuccess()
    {
        _streak = Math.Max(0, _streak - 1);
        // slowly decay toward base, never below base
        _currentMs = Math.Max(_baseMs, (int)(_currentMs * 0.9));
    }

    public bool IsRateLimit(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        var m = message.ToLowerInvariant();
        return m.Contains("too many") || m.Contains("rate limit") || m.Contains("429") ||
               m.Contains("try again later") || m.Contains("http 429") ||
               m.Contains("throttl") || m.Contains("exceeded");
    }

    public void OnRateLimitOrError(string? message = null)
    {
        _streak++;
        var hard = IsRateLimit(message);
        var factor = hard ? 2.8 : 1.45;
        _currentMs = Math.Min(60_000, (int)(_currentMs * factor) + (hard ? 2500 : 500) * _streak);
        if (hard)
            _currentMs = Math.Max(_currentMs, 12_000); // at least 12s after rate limit
    }

    /// <summary>Extra cooldown used before retrying a rate-limited account.</summary>
    public int RateLimitCooldownMs => Math.Clamp(_currentMs * 2, 15_000, 90_000);

    public Task WaitAsync(CancellationToken ct) => Task.Delay(_currentMs, ct);

    public Task WaitCooldownAsync(CancellationToken ct) =>
        Task.Delay(RateLimitCooldownMs, ct);
}
