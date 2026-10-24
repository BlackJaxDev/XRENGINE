using XREngine.Data.Vectors;
using XREngine.Input.Devices;

namespace XREngine.Rendering;

/// <summary>Receives native-window events in order on the owning window thread.</summary>
public interface IRuntimeWindowEventSink
{
    void SurfaceChanged(WindowSurfaceSnapshot snapshot);
    void FocusChanged(bool focused);
    void FileDropped(string[] paths);
    void KeyDown(EKey key);
    bool CloseRequested();
    void InteractiveResizeStarted();
    void InteractiveResizeUpdated(IVector2 framebufferExtent);
    void InteractiveResizeEnded();
    void RepaintRequested();
    void RenderRequested(double deltaSeconds);

    /// <summary>Starts one native OpenGL dispatch and returns its presentation token.</summary>
    ulong BeginOpenGlRenderDispatch() => 0UL;

    /// <summary>Checks whether all required output work for this dispatch completed.</summary>
    bool CanPresentOpenGlRenderDispatch(ulong dispatchToken) => true;

    /// <summary>Reports whether this exact dispatch completed a native buffer swap.</summary>
    void CompleteOpenGlRenderDispatch(ulong dispatchToken, bool presented) { }
}
