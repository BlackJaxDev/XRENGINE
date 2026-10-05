using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Components.VR;

public partial class VRPlayerCharacterComponent
{
    private VrSpectatorFollowComponent? _spectator;
    public VrSpectatorFollowComponent? Spectator
    {
        get => _spectator;
        set
        {
            if (ReferenceEquals(_spectator, value))
                return;
            RestoreSpectatorDesktop();
            SetField(ref _spectator, value);
            _spectatorRoutingPending = true;
        }
    }
    private bool _spectatorEnabled;
    private bool _showSpectatorOnDesktop = true;
    private bool _spectatorRoutingPending;
    private XRViewport? _spectatorDesktopViewport;
    private CameraComponent? _previousDesktopCamera;
    private CameraComponent? _installedSpectatorCamera;

    public bool ShowSpectatorOnDesktop
    {
        get => _showSpectatorOnDesktop;
        set
        {
            if (SetField(ref _showSpectatorOnDesktop, value))
                _spectatorRoutingPending = true;
        }
    }

    /// <summary>Enables independent follow and desktop output without changing any pawn/controller ownership.</summary>
    public bool SpectatorEnabled
    {
        get => _spectatorEnabled;
        set
        {
            SetField(ref _spectatorEnabled, value);
            _spectatorRoutingPending = true;
            if (Spectator is { } spectator)
            {
                spectator.IsActive = value;
                if (spectator.Output is { } output)
                    output.IsActive = value;
                spectator.ResetFollow();
            }
            if (RuntimeVrStateServices.PlayerSettings is { } settings)
                settings.VrSpectatorEnabled = value;
        }
    }

    private void ApplySpectatorDesktopRouting()
    {
        if (!_spectatorRoutingPending)
            return;
        if (!SpectatorEnabled || !ShowSpectatorOnDesktop)
        {
            RestoreSpectatorDesktop();
            if (Spectator?.Output is { } output)
                output.IsActive = SpectatorEnabled;
            _spectatorRoutingPending = false;
            return;
        }
        if (Spectator?.Camera is null)
        {
            RestoreSpectatorDesktop();
            return;
        }
        if (RuntimePlayerControllerServices.Current?.GetLocalPlayer(ELocalPlayerIndex.One)?.Viewport is XRViewport viewport)
            _spectatorRoutingPending = !RouteSpectatorDesktop(viewport);
    }

    /// <summary>Selects one explicit desktop viewport while retaining its exact prior camera, including null.</summary>
    public bool RouteSpectatorDesktop(XRViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (Spectator?.Camera is not { } camera || ReferenceEquals(viewport, RuntimeEngine.VRState.LeftEyeViewport)
            || ReferenceEquals(viewport, RuntimeEngine.VRState.RightEyeViewport) || ReferenceEquals(viewport, RuntimeEngine.VRState.StereoViewport))
            return false;
        if (_installedSpectatorCamera is not null
            && (!ReferenceEquals(viewport, _spectatorDesktopViewport) || !ReferenceEquals(camera, _installedSpectatorCamera)))
            RestoreSpectatorDesktop();
        if (_installedSpectatorCamera is null && !ReferenceEquals(viewport.CameraComponent, camera))
        {
            _spectatorDesktopViewport = viewport;
            _previousDesktopCamera = viewport.CameraComponent;
            _installedSpectatorCamera = camera;
        }
        Spectator.RouteDesktop(viewport, camera);
        return true;
    }

    private void RestoreSpectatorDesktop()
    {
        if (_spectatorDesktopViewport is { } viewport && _installedSpectatorCamera is { } installed
            && ReferenceEquals(viewport.CameraComponent, installed))
        {
            CameraComponent? previous = _previousDesktopCamera is { IsDestroyed: false } live ? live : null;
            viewport.CameraComponent = previous;
            previous?.Camera.InvalidateTemporalHistory();
        }
        _spectatorDesktopViewport = null;
        _previousDesktopCamera = null;
        _installedSpectatorCamera = null;
    }
}
