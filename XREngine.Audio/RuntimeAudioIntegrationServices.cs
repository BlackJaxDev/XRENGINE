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

    public static IRuntimeAudioIntegrationServices Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static long SecondsToElapsedTicks(float seconds)
        => (long)(Math.Max(0.0f, seconds) * Stopwatch.Frequency);
}