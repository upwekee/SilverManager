namespace SteamVault.Services;

/// <summary>
/// Pause / cancel / resume for long batch jobs (transfer, drain, review).
/// </summary>
public sealed class JobController
{
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private volatile bool _pauseRequested;
    private TaskCompletionSource? _resumeTcs;

    public bool IsRunning { get; private set; }
    public bool IsPaused => _pauseRequested;
    public string Name { get; private set; } = "";
    public int Done { get; set; }
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Fail { get; set; }
    public int Skipped { get; set; }
    public decimal ValueUsd { get; set; }
    public string Current { get; set; } = "";
    public DateTime StartedAt { get; private set; }

    public string ProgressText =>
        !IsRunning ? "idle"
        : IsPaused ? $"paused · {Done}/{Total} · {Current}"
        : $"{Done}/{Total} · ok {Ok} · fail {Fail} · ${ValueUsd:0.00} · {Current}";

    public string EtaText
    {
        get
        {
            if (!IsRunning || Done <= 0 || Total <= 0) return "—";
            var elapsed = DateTime.UtcNow - StartedAt;
            var per = elapsed.TotalSeconds / Done;
            var left = Math.Max(0, Total - Done) * per;
            if (left < 60) return $"~{left:0}s";
            if (left < 3600) return $"~{left / 60:0}m";
            return $"~{left / 3600:0.0}h";
        }
    }

    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public CancellationToken Start(string name, int total)
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _pauseRequested = false;
            _resumeTcs = null;
            IsRunning = true;
            Name = name;
            Done = 0;
            Total = Math.Max(0, total);
            Ok = 0;
            Fail = 0;
            Skipped = 0;
            ValueUsd = 0;
            Current = "";
            StartedAt = DateTime.UtcNow;
            return _cts.Token;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _pauseRequested = true;
            _resumeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _pauseRequested = false;
            _resumeTcs?.TrySetResult();
            _resumeTcs = null;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pauseRequested = false;
            _resumeTcs?.TrySetResult();
            _cts?.Cancel();
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (_pauseRequested)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (!_pauseRequested) return;
                wait = _resumeTcs?.Task ?? Task.Delay(200, ct);
            }
            await wait;
        }
        ct.ThrowIfCancellationRequested();
    }

    public void Finish()
    {
        lock (_gate)
        {
            IsRunning = false;
            _pauseRequested = false;
            Current = "done";
        }
    }
}
