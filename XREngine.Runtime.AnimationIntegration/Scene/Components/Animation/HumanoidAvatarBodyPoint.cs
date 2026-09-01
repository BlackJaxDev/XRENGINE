using System.Numerics;

namespace XREngine.Components.Animation;

/// <summary>
/// Serialized body-orientation landmark in model-native bone-local units.
/// </summary>
public sealed class HumanoidAvatarBodyPoint
{
    /// <summary>Semantic bone role that owns <see cref="LocalPosition"/>.</summary>
    public EHumanoidAvatarBoneRole Role { get; set; }

    /// <summary>Position relative to <see cref="Role"/> in model-native bone-local units.</summary>
    public Vector3 LocalPosition { get; set; }
}
