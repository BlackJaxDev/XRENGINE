using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Engine-readable avatar metadata converted from a Unity/VRChat avatar descriptor.
/// This component stores references and authored defaults; it does not execute SDK code.
/// </summary>
[Serializable]
public sealed class UnityAvatarDescriptorComponent : XRComponent
{
    private TransformBase? _avatarRoot;
    private Vector3 _viewPosition;
    private UnityAvatarLipSyncMode _lipSyncMode;
    private TransformBase? _jawBone;
    private Quaternion _jawClosedRotation = Quaternion.Identity;
    private Quaternion _jawOpenRotation = Quaternion.Identity;
    private ModelComponent? _visemeRenderer;
    private string _mouthOpenBlendShapeName = string.Empty;
    private List<string> _visemeBlendShapeNames = [];
    private UnityAvatarEyeLookMetadata _eyeLook = new();
    private List<UnityAvatarAnimationLayer> _animationLayers = [];
    private UnityAssetIdentity? _animationPreset;

    public TransformBase? AvatarRoot
    {
        get => _avatarRoot;
        set => SetField(ref _avatarRoot, value);
    }

    public Vector3 ViewPosition
    {
        get => _viewPosition;
        set => SetField(ref _viewPosition, value);
    }

    public UnityAvatarLipSyncMode LipSyncMode
    {
        get => _lipSyncMode;
        set => SetField(ref _lipSyncMode, value);
    }

    public TransformBase? JawBone
    {
        get => _jawBone;
        set => SetField(ref _jawBone, value);
    }

    public Quaternion JawClosedRotation
    {
        get => _jawClosedRotation;
        set => SetField(ref _jawClosedRotation, value);
    }

    public Quaternion JawOpenRotation
    {
        get => _jawOpenRotation;
        set => SetField(ref _jawOpenRotation, value);
    }

    public ModelComponent? VisemeRenderer
    {
        get => _visemeRenderer;
        set => SetField(ref _visemeRenderer, value);
    }

    public string MouthOpenBlendShapeName
    {
        get => _mouthOpenBlendShapeName;
        set => SetField(ref _mouthOpenBlendShapeName, value ?? string.Empty);
    }

    public List<string> VisemeBlendShapeNames
    {
        get => _visemeBlendShapeNames;
        set => SetField(ref _visemeBlendShapeNames, value ?? []);
    }

    public UnityAvatarEyeLookMetadata EyeLook
    {
        get => _eyeLook;
        set => SetField(ref _eyeLook, value ?? new UnityAvatarEyeLookMetadata());
    }

    public List<UnityAvatarAnimationLayer> AnimationLayers
    {
        get => _animationLayers;
        set => SetField(ref _animationLayers, value ?? []);
    }

    public UnityAssetIdentity? AnimationPreset
    {
        get => _animationPreset;
        set => SetField(ref _animationPreset, value);
    }
}
