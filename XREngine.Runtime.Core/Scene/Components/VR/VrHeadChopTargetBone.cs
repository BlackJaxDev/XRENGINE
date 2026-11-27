using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Components.VR;

/// <summary>
/// Controls whether one bone or node under the avatar head shrinks with first-person head hiding
/// or remains visible at full or partial scale.
/// </summary>
public sealed class VrHeadChopTargetBone : XRBase
{
    private SceneNode? _target;
    private float _scaleFactor = 1.0f;
    private EVrHeadChopApplyCondition _applyCondition = EVrHeadChopApplyCondition.AlwaysApply;

    /// <summary>
    /// Target bone or child node inside the avatar head hierarchy.
    /// </summary>
    public SceneNode? Target
    {
        get => _target;
        set => SetField(ref _target, value);
    }

    /// <summary>
    /// Visibility scale factor while head hiding is active. Zero shrinks the node with the head;
    /// one keeps the node at its full unchopped scale and head-relative position.
    /// </summary>
    public float ScaleFactor
    {
        get => _scaleFactor;
        set => SetField(ref _scaleFactor, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f);
    }

    /// <summary>
    /// Runtime mode condition that enables this bone override.
    /// </summary>
    public EVrHeadChopApplyCondition ApplyCondition
    {
        get => _applyCondition;
        set => SetField(ref _applyCondition, value);
    }

    /// <summary>
    /// Returns true when <see cref="ApplyCondition"/> matches the active VR state.
    /// </summary>
    public bool MatchesCondition(bool isVrActive)
        => _applyCondition switch
        {
            EVrHeadChopApplyCondition.AlwaysApply => true,
            EVrHeadChopApplyCondition.VrOnly => isVrActive,
            EVrHeadChopApplyCondition.DesktopOnly => !isVrActive,
            _ => false,
        };
}
