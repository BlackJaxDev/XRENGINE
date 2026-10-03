using Silk.NET.Windowing;
using XREngine.Data.Vectors;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Presents a test-owned hidden Silk window as a desktop window backend for GPU tests that only
/// need its GL context. The test keeps ownership of the native window and destroys it itself;
/// every other window operation is outside these tests and reports so.
/// </summary>
internal sealed class SilkSharedWindowTestBackend(IWindow window) : IRuntimeWindowBackend
{
    private readonly SilkGlContextTestAdapter? _glContext =
        window.GLContext is { } context ? new SilkGlContextTestAdapter(context) : null;

    public IRuntimeWindowGlContext? GlContext => _glContext;
    public IRuntimeWindowVulkanSurface? VulkanSurface => null;
    public RuntimeGraphicsApiKind GraphicsApi => RuntimeGraphicsApiKind.OpenGL;
    public string Title => window.Title;
    public bool IsClosing => window.IsClosing;

    public RuntimeWindowBackendKind Kind => throw Unsupported();
    public RuntimeWindowBackendOwnershipInfo Ownership => throw Unsupported();
    public int OwnerThreadId => throw Unsupported();
    public long LifetimeGeneration => throw Unsupported();
    public WindowSurfaceSnapshot Surface => throw Unsupported();
    public WindowEventSnapshot Events => throw Unsupported();
    public WindowInputSnapshot Input => throw Unsupported();
    public nint PlatformWindowHandle => throw Unsupported();
    public nint OperatingSystemWindowHandle => throw Unsupported();
    public IVector2 ClientScreenPosition => throw Unsupported();
    public ReadOnlyMemory<RuntimeDesktopMonitor> Monitors => throw Unsupported();

    public WindowInputSnapshot ConsumeInput() => throw Unsupported();
    public WindowInputSnapshot ConsumeUiInput() => throw Unsupported();
    public WindowInputSnapshot ConsumeUiInput(List<WindowInputEvent> orderedDestination) => throw Unsupported();
    public void Initialize(IRuntimeWindowEventSink sink) => throw Unsupported();
    public void PumpEvents() => throw Unsupported();
    public void DispatchRender() => throw Unsupported();
    public void RequestSize(IVector2 size) => throw Unsupported();
    public void RequestPosition(IVector2 position) => throw Unsupported();
    public void RequestClientScreenPosition(IVector2 position) => throw Unsupported();
    public void RequestTitle(string title) => throw Unsupported();
    public void RequestState(EWindowState state) => throw Unsupported();
    public void RequestFocus() => throw Unsupported();
    public void RequestVisibility(bool visible) => throw Unsupported();
    public void RequestCursorCapture(bool captured) => throw Unsupported();
    public void SetVSync(bool enabled) => throw Unsupported();
    public void RequestClose() => throw Unsupported();
    public bool TryCancelClose() => throw Unsupported();
    public void SetInteractiveResizeStrategy(EInteractiveWindowResizeStrategy strategy) => throw Unsupported();

    // The test disposes the native window through its own helper after the shared context stops.
    public void RetainAbandonedResources() { }
    public void Dispose() { }

    private static NotSupportedException Unsupported()
        => new("The shared-context test backend only exposes its GL context.");
}
