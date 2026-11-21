namespace XREngine.Cutscenes;

/// <summary>A clip that makes one camera the active camera while the clip plays.</summary>
public class CameraCutClip : CutsceneClip
{
    private int _cameraBindingSlot;

    /// <summary>The index of the binding that holds the camera.</summary>
    public int CameraBindingSlot
    {
        get => _cameraBindingSlot;
        set => SetField(ref _cameraBindingSlot, value);
    }
}
