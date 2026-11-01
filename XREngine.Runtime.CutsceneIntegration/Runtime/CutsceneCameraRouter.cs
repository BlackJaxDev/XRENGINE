using XREngine.Components;
using XREngine.Rendering;

namespace XREngine.Cutscenes;

/// <summary>
/// Shows the camera that a cutscene selects on one desktop viewport.
/// The router claims the camera of the viewport, so fallback code cannot replace it.
/// It resets the temporal history of the viewport once for each discontinuity of the player.
/// The router does not touch the eye cameras of a headset.
/// </summary>
public sealed class CutsceneCameraRouter
{
    private readonly CutscenePlayer _player;
    private readonly Action<CameraComponent?> _onActiveCameraChanged;
    private readonly Action<CutsceneDiscontinuity> _onDiscontinuity;
    private XRViewport? _viewport;

    /// <summary>Creates a router for a player.</summary>
    /// <param name="player">The player that selects the camera.</param>
    public CutsceneCameraRouter(CutscenePlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;

        // The delegates are created once, so a frame never allocates.
        _onActiveCameraChanged = OnActiveCameraChanged;
        _onDiscontinuity = OnDiscontinuity;
    }

    /// <summary>The viewport that shows the camera, or null if the router is detached.</summary>
    public XRViewport? Viewport => _viewport;

    /// <summary>
    /// Starts to show the selected camera on a viewport. If the router is attached to another viewport, it detaches first.
    /// </summary>
    /// <param name="viewport">The desktop viewport.</param>
    public void Attach(XRViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (ReferenceEquals(_viewport, viewport))
            return;

        Detach();
        _viewport = viewport;
        _player.ActiveCameraChanged += _onActiveCameraChanged;
        _player.Discontinuity += _onDiscontinuity;
        OnActiveCameraChanged(_player.ActiveCamera);
    }

    /// <summary>Stops the routing and gives the previous camera back to the viewport. A call on a detached router does nothing.</summary>
    public void Detach()
    {
        XRViewport? viewport = _viewport;
        if (viewport is null)
            return;

        _viewport = null;
        _player.ActiveCameraChanged -= _onActiveCameraChanged;
        _player.Discontinuity -= _onDiscontinuity;
        viewport.ReleaseCameraClaim(this);
    }

    private void OnActiveCameraChanged(CameraComponent? camera)
    {
        XRViewport? viewport = _viewport;
        if (viewport is null)
            return;

        // A gap between shots, or a shot without a camera, shows the previous camera.
        if (camera is null)
            viewport.ReleaseCameraClaim(this);
        else
            viewport.ClaimCamera(this, camera);
    }

    private void OnDiscontinuity(CutsceneDiscontinuity discontinuity)
        => _viewport?.InvalidateTemporalHistory();
}
