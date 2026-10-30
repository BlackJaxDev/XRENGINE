using System.Diagnostics;
using XREngine.Audio;
using XREngine.Data;

namespace XREngine;

/// <summary>
/// Host capabilities used by runtime audio components without depending on the engine facade.
/// </summary>
public interface IRuntimeAudioIntegrationServices
{
    int SampleRate { get; }
    long ElapsedTicks { get; }
    float UpdateDeltaSeconds { get; }
    string? ProjectDirectory { get; }
    ListenerContext NewListener(string? name);
    AudioData? LoadAudioData(string path);
}

/// <summary>
/// Process-wide host boundary for audio integration components.
/// </summary>
public static class RuntimeAudioIntegrationServices
{
    private sealed class DefaultRuntimeAudioIntegrationServices : IRuntimeAudioIntegrationServices
    {
        private readonly AudioManager _audio = new();

        public int SampleRate => _audio.SampleRate;
        public long ElapsedTicks => Stopwatch.GetTimestamp();
        public float UpdateDeltaSeconds => 0.0f;
        public string? ProjectDirectory => null;
        public ListenerContext NewListener(string? name) => _audio.NewListener(name);
        public AudioData? LoadAudioData(string path) => null;
    }

    private static IRuntimeAudioIntegrationServices _current = new DefaultRuntimeAudioIntegrationServices();
    private static readonly object Gate = new();
    private static long _generation;

    public static IRuntimeAudioIntegrationServices Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            using IDisposable change = RuntimeSourcePublicationServices.BeginInstallationChange();
            lock (Gate)
            {
                Volatile.Write(ref _current, value);
                _generation = unchecked(_generation + 1);
            }
        }
    }

    /// <summary>Captures the audio host installation, including a reinstall of the same instance.</summary>
    public static IRuntimeAudioIntegrationServices Capture(out long generation)
    {
        lock (Gate)
        {
            generation = _generation;
            return _current;
        }
    }

    /// <summary>Checks whether the captured audio host installation is current.</summary>
    public static bool IsCurrent(IRuntimeAudioIntegrationServices host, long generation)
    {
        lock (Gate)
            return ReferenceEquals(_current, host) && _generation == generation;
    }

    public static long SecondsToElapsedTicks(float seconds)
        => (long)(Math.Max(0.0f, seconds) * Stopwatch.Frequency);
}
