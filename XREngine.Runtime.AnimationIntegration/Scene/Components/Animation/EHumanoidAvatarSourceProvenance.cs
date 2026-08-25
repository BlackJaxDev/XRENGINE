namespace XREngine.Components.Animation;

/// <summary>
/// Declares whether an avatar definition was authored from a source artifact or
/// directly from a runtime-created scene skeleton.
/// </summary>
public enum EHumanoidAvatarSourceProvenance
{
    /// <summary>The definition has not yet declared a trustworthy source contract.</summary>
    Unknown,

    /// <summary>The skeleton was authored directly in XRENGINE and has no source model artifact.</summary>
    RuntimeAuthoredSkeleton,

    /// <summary>The skeleton was imported from a fingerprinted model source.</summary>
    ImportedModel,
}
