namespace XREngine.Rendering.UI;

/// <summary>
/// Tracks which native window owns the desktop pointer for Dear ImGui multi-viewport input.
/// </summary>
/// <remarks>
/// Each native window reports pointer events only while the pointer is over that window or
/// while that window captures the mouse. Popup and combo viewports use
/// <c>NoFocusOnClick</c>, so the main window stays focused while the user clicks in them. The
/// main window keeps its last pointer position after the pointer leaves it. That stale position
/// must not replace the position that the popup window reports, or ImGui sees the click outside
/// the popup.
/// <para>
/// The window that reported the most recent pointer activity owns the pointer. When a platform
/// window and the main window both report activity in one frame, the platform window wins,
/// because the platform-window events are queued after the main-window events.
/// </para>
/// </remarks>
public sealed class ImGuiPointerOwnerTracker
{
    /// <summary>The owner value that identifies the main window.</summary>
    public const uint MainWindow = 0;

    private float _lastMainPointerX = float.NaN;
    private float _lastMainPointerY = float.NaN;
    private bool _platformActivityThisFrame;

    /// <summary>
    /// Gets the ImGui viewport ID of the platform window that owns the pointer, or
    /// <see cref="MainWindow"/> when the main window owns it.
    /// </summary>
    public uint OwnerViewportId { get; private set; } = MainWindow;

    /// <summary>Gets whether the main window owns the pointer.</summary>
    public bool MainWindowOwnsPointer => OwnerViewportId == MainWindow;

    /// <summary>
    /// Starts a new input frame. Call this before platform-window input is replayed.
    /// </summary>
    public void BeginFrame()
        => _platformActivityThisFrame = false;

    /// <summary>
    /// Records pointer, button, or wheel input from a platform window.
    /// </summary>
    public void NotePlatformPointerActivity(uint viewportId)
    {
        if (viewportId == MainWindow)
            return;

        OwnerViewportId = viewportId;
        _platformActivityThisFrame = true;
    }

    /// <summary>
    /// Returns pointer ownership to the main window when the owning platform window is gone.
    /// </summary>
    public void ReleasePlatformWindow(uint viewportId)
    {
        if (OwnerViewportId == viewportId)
            OwnerViewportId = MainWindow;
    }

    /// <summary>
    /// Records the current main-window pointer position and returns whether the main window
    /// owns the pointer.
    /// </summary>
    /// <param name="x">The main-window pointer X position in client coordinates.</param>
    /// <param name="y">The main-window pointer Y position in client coordinates.</param>
    public bool UpdateMainPointer(float x, float y)
    {
        // Floating-point equality is intended: the values are copied from native events, and
        // any change means that the main window received a new pointer event.
        bool moved = x != _lastMainPointerX || y != _lastMainPointerY;
        _lastMainPointerX = x;
        _lastMainPointerY = y;

        if (moved && !_platformActivityThisFrame)
            OwnerViewportId = MainWindow;

        return MainWindowOwnsPointer;
    }
}
