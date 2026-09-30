namespace XREngine.Rendering.VideoStreaming.Interfaces;

/// <summary>Creates media sessions after the decoder backend has initialized.</summary>
public interface IHlsReferenceRuntime
{
    bool EnsureStarted();

    IMediaStreamSession CreateSession();
}
