using XREngine.Components.Lights;

namespace XREngine.Components.VR;

/// <summary>Chooses the desktop camera of a VR pawn without changing possession or headset cameras.</summary>
public sealed class VrSpectatorOutputComponent : XRComponent
{
    private PawnComponent? _pawn;
    private VRPlayerCharacterComponent? _player;
    private CameraComponent? _firstPersonCamera;
    private CameraComponent? _spectatorCamera;
    private CameraComponent? _editorCamera;
    private VrSpectatorFollowComponent? _follow;
    private VrSpectatorCapturePairComponent? _capture;
    private EVrDesktopView _desktopView;

    public PawnComponent? Pawn
    {
        get => _pawn;
        set => SetField(ref _pawn, value);
    }

    public VRPlayerCharacterComponent? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value))
                return;
            if (_player is not null)
                _player.TrackingDiscontinuity -= ResetViewHistory;
            SetField(ref _player, value);
            if (_player is not null && IsActiveInHierarchy)
                _player.TrackingDiscontinuity += ResetViewHistory;
        }
    }

    public CameraComponent? FirstPersonCamera
    {
        get => _firstPersonCamera;
        set => SetField(ref _firstPersonCamera, value);
    }

    public CameraComponent? SpectatorCamera
    {
        get => _spectatorCamera;
        set => SetField(ref _spectatorCamera, value);
    }

    public CameraComponent? EditorCamera
    {
        get => _editorCamera;
        set
        {
            if (!SetField(ref _editorCamera, value))
                return;
            ApplyDesktopView();
        }
    }

    public VrSpectatorFollowComponent? Follow
    {
        get => _follow;
        set => SetField(ref _follow, value);
    }

    /// <summary>Optional completion-gated texture output for external capture consumers.</summary>
    public VrSpectatorCapturePairComponent? Capture
    {
        get => _capture;
        set => SetField(ref _capture, value);
    }

    /// <summary>Controls the desktop viewport owned by the pawn's local controller.</summary>
    public EVrDesktopView DesktopView
    {
        get => _desktopView;
        set
        {
            if (!SetField(ref _desktopView, value))
                return;
            ResetViewHistory();
            ApplyDesktopView();
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        if (Player is not null)
            Player.TrackingDiscontinuity += ResetViewHistory;
        ApplyDesktopView();
    }

    protected override void OnComponentDeactivated()
    {
        if (Player is not null)
            Player.TrackingDiscontinuity -= ResetViewHistory;
        if (Capture is not null)
            Capture.CaptureEnabled = false;
        base.OnComponentDeactivated();
    }

    private void ResetViewHistory()
    {
        Follow?.ResetFollow();
        SpectatorCamera?.Camera.InvalidateTemporalHistory();
        Capture?.ResetOutputAfterCut();
    }

    public void ApplyDesktopView()
    {
        if (Pawn is null)
            return;

        bool textureOutput = DesktopView == EVrDesktopView.TextureOutput;
        if (Capture is { } capture)
        {
            capture.DesktopCamera = SpectatorCamera;
            capture.DesktopSpectatorActive = DesktopView == EVrDesktopView.Spectator;
            capture.CaptureEnabled = textureOutput;
        }

        CameraComponent? camera = DesktopView switch
        {
            EVrDesktopView.Spectator => SpectatorCamera,
            EVrDesktopView.EditorCamera => EditorCamera ?? FirstPersonCamera,
            // A live first-person desktop viewport keeps the window render loop running
            // while the sole spectator scene render writes the offscreen texture.
            EVrDesktopView.TextureOutput => FirstPersonCamera,
            _ => FirstPersonCamera,
        };
        if (camera is null || ReferenceEquals(Pawn.CameraComponent, camera))
            return;

        ResetViewHistory();
        Pawn.CameraComponent = camera;
    }
}
