using System.Numerics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Holds the pan and zoom of one Workbench canvas.
/// </summary>
public sealed class RenderPipelineCanvasViewState
{
    /// <summary>The default pan offset in canvas units.</summary>
    public static readonly Vector2 DefaultPan = new(40f, 40f);

    /// <summary>Gets or sets the pan offset in canvas units.</summary>
    public Vector2 Pan { get; set; } = DefaultPan;

    /// <summary>Gets or sets the zoom factor.</summary>
    public float Zoom { get; set; } = 1f;

    /// <summary>Gets or sets a canvas point that the next draw scrolls into view, or null.</summary>
    public Vector2? PendingFocus { get; set; }

    /// <summary>Restores the default pan and zoom.</summary>
    public void Reset()
    {
        Pan = DefaultPan;
        Zoom = 1f;
        PendingFocus = null;
    }

    /// <summary>Converts a canvas point to a screen point.</summary>
    public Vector2 ToScreen(Vector2 canvasOrigin, Vector2 world)
        => canvasOrigin + ((world + Pan) * Zoom);
}
