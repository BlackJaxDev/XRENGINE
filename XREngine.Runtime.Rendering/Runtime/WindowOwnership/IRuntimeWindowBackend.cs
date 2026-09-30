using System.Collections.Generic;
using XREngine.Data.Vectors;

namespace XREngine.Rendering;

/// <summary>
/// Owns one native desktop window. Mutations, input capture, event pumping, and disposal run on
/// <see cref="OwnerThreadId"/>; render dispatch runs on the graphics owner declared by the host.
/// </summary>
public interface IRuntimeWindowBackend : IDisposable
{
    RuntimeWindowBackendKind Kind { get; }
    RuntimeWindowBackendOwnershipInfo Ownership { get; }
    RuntimeGraphicsApiKind GraphicsApi { get; }
    int OwnerThreadId { get; }
    long LifetimeGeneration { get; }
    WindowSurfaceSnapshot Surface { get; }
    WindowEventSnapshot Events { get; }
    WindowInputSnapshot Input { get; }
    WindowInputSnapshot ConsumeInput();
    WindowInputSnapshot ConsumeUiInput();
    WindowInputSnapshot ConsumeUiInput(List<WindowInputEvent> orderedDestination);
    IRuntimeWindowGlContext? GlContext { get; }
    IRuntimeWindowVulkanSurface? VulkanSurface { get; }
    nint PlatformWindowHandle { get; }
    nint OperatingSystemWindowHandle { get; }
    IVector2 ClientScreenPosition { get; }
    ReadOnlyMemory<RuntimeDesktopMonitor> Monitors { get; }
    string Title { get; }
    bool IsClosing { get; }

    void Initialize(IRuntimeWindowEventSink sink);
    void PumpEvents();
    void DispatchRender();
    void RequestSize(IVector2 size);
    void RequestPosition(IVector2 position);
    void RequestClientScreenPosition(IVector2 position);
    void RequestTitle(string title);
    void RequestState(EWindowState state);
    void RequestFocus();
    void RequestVisibility(bool visible);
    void RequestCursorCapture(bool captured);
    void SetVSync(bool enabled);
    void RequestClose();
    bool TryCancelClose();
    void SetInteractiveResizeStrategy(EInteractiveWindowResizeStrategy strategy);
    /// <summary>Keeps the native window and graphics context alive when safe GPU teardown was abandoned.</summary>
    void RetainAbandonedResources();
}
