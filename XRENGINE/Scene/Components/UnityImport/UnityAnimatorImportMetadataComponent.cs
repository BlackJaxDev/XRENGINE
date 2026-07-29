using XREngine.Scene.Prefabs;

namespace XREngine.Components;

/// <summary>
/// Unity Animator settings that are meaningful to imported avatar tooling but
/// do not yet have a one-to-one runtime animator implementation.
/// </summary>
[Serializable]
public sealed class UnityAnimatorImportMetadataComponent : XRComponent
{
    private UnityAssetIdentity? _controller;
    private bool _applyRootMotion;
    private int _cullingMode;
    private int _updateMode;
    private bool _hasTransformHierarchy = true;

    public UnityAssetIdentity? Controller
    {
        get => _controller;
        set => SetField(ref _controller, value);
    }

    public bool ApplyRootMotion
    {
        get => _applyRootMotion;
        set => SetField(ref _applyRootMotion, value);
    }

    public int CullingMode
    {
        get => _cullingMode;
        set => SetField(ref _cullingMode, value);
    }

    public int UpdateMode
    {
        get => _updateMode;
        set => SetField(ref _updateMode, value);
    }

    public bool HasTransformHierarchy
    {
        get => _hasTransformHierarchy;
        set => SetField(ref _hasTransformHierarchy, value);
    }
}
