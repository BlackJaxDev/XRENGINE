namespace XREngine.Components.Animation;

/// <summary>
/// Serialized body segment used to derive the model-root body center.
/// </summary>
public sealed class HumanoidAvatarBodySegment
{
    /// <summary>Bone-local point at the start of the segment.</summary>
    public HumanoidAvatarBodyPoint? Start { get; set; }

    /// <summary>Bone-local point at the end of the segment.</summary>
    public HumanoidAvatarBodyPoint? End { get; set; }

    /// <summary>Normalized position of the segment center between start and end.</summary>
    public float CenterFraction { get; set; }

    /// <summary>Positive contribution of this segment to the body center.</summary>
    public float MassFraction { get; set; }
}
