using XREngine.Audio;
using XREngine.Data;

namespace XREngine;

internal sealed class EngineRuntimeAudioIntegrationServices : IRuntimeAudioIntegrationServices
{
    public int SampleRate => Engine.Audio.SampleRate;
    public long ElapsedTicks => Engine.ElapsedTicks;
    public float UpdateDeltaSeconds => Engine.Delta;
    public string? ProjectDirectory => Engine.CurrentProject?.ProjectDirectory;
    public ListenerContext NewListener(string? name) => Engine.Audio.NewListener(name);
    public AudioData? LoadAudioData(string path) => Engine.Assets.Load<AudioData>(path);
}