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
}
