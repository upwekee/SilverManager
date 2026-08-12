using System.Collections.Concurrent;
using Avalonia.Platform;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SteamVault.Services;

public enum Sfx
{
    Startup,
    Click,
    Select,
    Success,
    Error,
    Panel,
    Nav,
    Done
}

/// <summary>
/// Soft UI SFX from Botanica V4 pack (bundled under Assets/sfx).
/// Low volume, few cues, non-blocking.
/// </summary>
public sealed class SoundService : IDisposable
{
    private readonly ConcurrentDictionary<Sfx, byte[]> _cache = new();
    private readonly object _gate = new();
    private IWavePlayer? _player;
    private WaveStream? _stream;
    private DateTime _lastClick = DateTime.MinValue;

    public bool Enabled { get; set; } = true;
    /// <summary>0..1 — default quiet</summary>
    public float Volume { get; set; } = 0.22f;

    private static readonly Dictionary<Sfx, string> Map = new()
    {
        [Sfx.Startup] = "avares://SilverManager/Assets/sfx/startup.wav",
        [Sfx.Click] = "avares://SilverManager/Assets/sfx/click.wav",
        [Sfx.Select] = "avares://SilverManager/Assets/sfx/select.wav",
        [Sfx.Success] = "avares://SilverManager/Assets/sfx/success.wav",
        [Sfx.Error] = "avares://SilverManager/Assets/sfx/error.wav",
        [Sfx.Panel] = "avares://SilverManager/Assets/sfx/panel.wav",
        [Sfx.Nav] = "avares://SilverManager/Assets/sfx/nav.wav",
        [Sfx.Done] = "avares://SilverManager/Assets/sfx/done.wav",
    };

    public void Play(Sfx sfx, bool debounceClick = false)
    {
        if (!Enabled) return;
        if (debounceClick && sfx is Sfx.Click or Sfx.Select or Sfx.Nav)
        {
            if ((DateTime.UtcNow - _lastClick).TotalMilliseconds < 80) return;
            _lastClick = DateTime.UtcNow;
        }

        try
        {
            var data = Load(sfx);
            if (data == null || data.Length == 0) return;

            // Play on thread pool so UI never blocks
            _ = Task.Run(() => PlayBytes(data));
        }
        catch
        {
            // never crash UI for sound
        }
    }

    private byte[]? Load(Sfx sfx)
    {
        if (_cache.TryGetValue(sfx, out var cached)) return cached;
        if (!Map.TryGetValue(sfx, out var uri)) return null;
        try
        {
            using var s = AssetLoader.Open(new Uri(uri));
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            _cache[sfx] = bytes;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void PlayBytes(byte[] data)
    {
        lock (_gate)
        {
            try
            {
                _player?.Stop();
                _player?.Dispose();
                _stream?.Dispose();

                var ms = new MemoryStream(data, writable: false);
                var reader = new WaveFileReader(ms);
                // Volume wrapper
                ISampleProvider sample = reader.ToSampleProvider();
                var vol = new VolumeSampleProvider(sample) { Volume = Math.Clamp(Volume, 0f, 1f) };

                var output = new WaveOutEvent { DesiredLatency = 100 };
                output.Init(vol);
                output.PlaybackStopped += (_, _) =>
                {
                    try
                    {
                        output.Dispose();
                        reader.Dispose();
                        ms.Dispose();
                    }
                    catch { /* */ }
                };
                _player = output;
                _stream = reader;
                output.Play();
            }
            catch
            {
                // ignore decode/device errors
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _player?.Stop(); } catch { /* */ }
            _player?.Dispose();
            _stream?.Dispose();
        }
    }
}
