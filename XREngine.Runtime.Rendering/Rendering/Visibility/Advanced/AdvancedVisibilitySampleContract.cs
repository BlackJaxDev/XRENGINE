using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>Freezes raw-sample encoding independently of the canonical resolved integer images.</summary>
public static class AdvancedVisibilitySampleContract
{
    /// <summary>Structural encoding bit outside the visibility, reconstruction, and material-export ranges.</summary>
    public const ulong PackedUInt16FeatureBit = 1UL << 57;

    /// <summary>Uses only the frozen profile; an inactive configured MSAA count has no effect.</summary>
    public static EAdvancedVisibilitySampleEncoding Select(RenderPipelineResourceProfile profile)
        => profile.AntiAliasingMode != EAntiAliasingMode.Msaa || profile.MsaaSampleCount <= 1u
            ? EAdvancedVisibilitySampleEncoding.None
            : (profile.FeatureMask & PackedUInt16FeatureBit) != 0
                ? EAdvancedVisibilitySampleEncoding.PackedUInt16
                : EAdvancedVisibilitySampleEncoding.PlanarUInt32;
}
