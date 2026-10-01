using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Audio.WebAudio;

/// <summary>Browser-owned Web Audio operations; all handles belong to one output context.</summary>
internal static partial class WebAudioImports
{
    [JSImport("open", "xrengine.engineAudio")]
    internal static partial int Open();
    [JSImport("close", "xrengine.engineAudio")]
    internal static partial void Close(int context);
    [JSImport("sampleRate", "xrengine.engineAudio")]
    internal static partial int SampleRate(int context);
    [JSImport("isOpen", "xrengine.engineAudio")]
    internal static partial bool IsOpen(int context);
    [JSImport("unlock", "xrengine.engineAudio")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    internal static partial Task<bool> UnlockAsync();
    [JSImport("state", "xrengine.engineAudio")]
    internal static partial string State();
    [JSImport("listenerPosition", "xrengine.engineAudio")]
    internal static partial void ListenerPosition(int context, float x, float y, float z);
    [JSImport("listenerVelocity", "xrengine.engineAudio")]
    internal static partial void ListenerVelocity(int context, float x, float y, float z);
    [JSImport("listenerOrientation", "xrengine.engineAudio")]
    internal static partial void ListenerOrientation(int context, float fx, float fy, float fz, float ux, float uy, float uz);
    [JSImport("listenerGain", "xrengine.engineAudio")]
    internal static partial void ListenerGain(int context, float gain);
    [JSImport("createSource", "xrengine.engineAudio")]
    internal static partial int CreateSource(int context);
    [JSImport("destroySource", "xrengine.engineAudio")]
    internal static partial void DestroySource(int context, int source);
    [JSImport("createBuffer", "xrengine.engineAudio")]
    internal static partial int CreateBuffer(int context);
    [JSImport("destroyBuffer", "xrengine.engineAudio")]
    internal static partial void DestroyBuffer(int context, int buffer);
    [JSImport("uploadBuffer", "xrengine.engineAudio")]
    internal static partial void UploadBuffer(int context, int buffer, [JSMarshalAs<JSType.MemoryView>] Span<byte> pcm,
        int frequency, int channels, int format);
    [JSImport("play", "xrengine.engineAudio")]
    internal static partial void Play(int context, int source);
    [JSImport("stop", "xrengine.engineAudio")]
    internal static partial void Stop(int context, int source);
    [JSImport("pause", "xrengine.engineAudio")]
    internal static partial void Pause(int context, int source);
    [JSImport("rewind", "xrengine.engineAudio")]
    internal static partial void Rewind(int context, int source);
    [JSImport("setSourceBuffer", "xrengine.engineAudio")]
    internal static partial void SetSourceBuffer(int context, int source, int buffer);
    [JSImport("sourcePosition", "xrengine.engineAudio")]
    internal static partial void SourcePosition(int context, int source, float x, float y, float z);
    [JSImport("sourceVelocity", "xrengine.engineAudio")]
    internal static partial void SourceVelocity(int context, int source, float x, float y, float z);
    [JSImport("sourceGain", "xrengine.engineAudio")]
    internal static partial void SourceGain(int context, int source, float gain);
    [JSImport("sourcePitch", "xrengine.engineAudio")]
    internal static partial void SourcePitch(int context, int source, float pitch);
    [JSImport("sourceLooping", "xrengine.engineAudio")]
    internal static partial void SourceLooping(int context, int source, bool loop);
    [JSImport("isSourcePlaying", "xrengine.engineAudio")]
    internal static partial bool IsSourcePlaying(int context, int source);
    [JSImport("sampleOffset", "xrengine.engineAudio")]
    internal static partial int SampleOffset(int context, int source);
}
