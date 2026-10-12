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
    [JSImport("isReady", "xrengine.engineAudio")]
    internal static partial bool IsReady();
    [JSImport("hasActivationFailure", "xrengine.engineAudio")]
    internal static partial bool HasActivationFailure();
    [JSImport("activationFailure", "xrengine.engineAudio")]
    internal static partial string ActivationFailure();
    [JSImport("setPageActive", "xrengine.engineAudio")]
    internal static partial void SetPageActive(bool active);
    [JSImport("setSurfaceActive", "xrengine.engineAudio")]
    internal static partial void SetSurfaceActive(bool active);
    [JSImport("beginSpatialUpdates", "xrengine.engineAudio")]
    internal static partial int BeginSpatialUpdates();
    [JSImport("endSpatialUpdates", "xrengine.engineAudio")]
    internal static partial void EndSpatialUpdates(int batch);
    [JSImport("listenerProperty", "xrengine.engineAudio")]
    internal static partial double ListenerProperty(int context, int property);
    [JSImport("setListenerProperty", "xrengine.engineAudio")]
    internal static partial void SetListenerProperty(int context, int property, double value);
    [JSImport("sourceFloatProperty", "xrengine.engineAudio")]
    internal static partial double SourceFloatProperty(int context, int source, int property);
    [JSImport("setSourceFloatProperty", "xrengine.engineAudio")]
    internal static partial void SetSourceFloatProperty(int context, int source, int property, double value);
    [JSImport("sourceQueueOffset", "xrengine.engineAudio")]
    internal static partial double SourceQueueOffset(int context, int source, int unit);
    [JSImport("seekSource", "xrengine.engineAudio")]
    internal static partial void SeekSource(int context, int source, int unit, double value);
    [JSImport("sourceRelative", "xrengine.engineAudio")]
    internal static partial void SourceRelative(int context, int source, bool relative);
    [JSImport("sourceDirection", "xrengine.engineAudio")]
    internal static partial void SourceDirection(int context, int source, float x, float y, float z);
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
    [JSImport("queueBuffers", "xrengine.engineAudio")]
    internal static partial void QueueBuffers(int context, int source, [JSMarshalAs<JSType.MemoryView>] Span<int> buffers);
    [JSImport("unqueueProcessedBuffers", "xrengine.engineAudio")]
    internal static partial int UnqueueProcessedBuffers(int context, int source,
        [JSMarshalAs<JSType.MemoryView>] Span<int> output, int maximum);
    [JSImport("buffersProcessed", "xrengine.engineAudio")]
    internal static partial int BuffersProcessed(int context, int source);
    [JSImport("buffersQueued", "xrengine.engineAudio")]
    internal static partial int BuffersQueued(int context, int source);
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
