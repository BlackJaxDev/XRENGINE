using System.Collections.Generic;
using XREngine.Scene;

namespace XREngine.Components.VR;

/// <summary>
/// Configures first-person head hiding overrides for selected descendant nodes under the avatar head.
/// Nodes listed in <see cref="TargetBones"/> with a nonzero scale factor stay visible when the head
/// bone scales down for first-person VR.
/// </summary>
public sealed class VrHeadChopComponent : XRComponent
{
    private float _globalScaleFactor = 1.0f;
    private List<VrHeadChopTargetBone> _targetBones = [];

    /// <summary>
    /// Global blend weight for this component in the range 0 to 1. Zero disables all overrides on
    /// this component; one applies each target bone's configured <see cref="VrHeadChopTargetBone.ScaleFactor"/>.
    /// </summary>
    public float GlobalScaleFactor
    {
        get => _globalScaleFactor;
        set => SetField(ref _globalScaleFactor, float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : 0.0f);
    }

    /// <summary>
    /// Descendant head bones or attachment nodes whose first-person scale overrides default head hiding.
    /// </summary>
    public List<VrHeadChopTargetBone> TargetBones
    {
        get => _targetBones;
        set => SetField(ref _targetBones, value ?? []);
    }

    /// <summary>
    /// Reads the effective scale factor for <paramref name="node"/> when this component is active
    /// and contains a matching target entry.
    /// </summary>
    public bool TryGetEffectiveScaleFactor(SceneNode node, bool isVrActive, out float scaleFactor)
    {
        scaleFactor = 0.0f;
        if (!IsActiveInHierarchy || _globalScaleFactor <= 0.0f)
            return false;

        List<VrHeadChopTargetBone> targets = _targetBones;
        bool matched = false;
        float bestScale = 0.0f;
        for (int i = 0; i < targets.Count; ++i)
        {
            VrHeadChopTargetBone entry = targets[i];
            if (entry is null || !ReferenceEquals(entry.Target, node) || !entry.MatchesCondition(isVrActive))
                continue;

            float effective = Math.Clamp(entry.ScaleFactor * _globalScaleFactor, 0.0f, 1.0f);
            if (!matched || effective > bestScale)
            {
                bestScale = effective;
                matched = true;
            }
        }

        scaleFactor = bestScale;
        return matched;
    }
}
