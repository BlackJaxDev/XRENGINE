namespace XREngine.Components.Capture.Lights;

/// <summary>Defines the roughness coordinates stored by a completed probe generation.</summary>
public enum ELightProbePrefilterConvention
{
    /// <summary>Stores perceptual roughness uniformly across the mip range.</summary>
    PerceptualRoughness,

    /// <summary>Stores Unity reflection levels zero through six without remapping.</summary>
    UnityMip,
}
