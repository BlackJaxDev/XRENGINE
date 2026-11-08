namespace XREngine;

/// <summary>
/// Identifies how the running process was built and where it reads its content.
/// </summary>
public enum EXRRuntimeBuildKind
{
    /// <summary>A development build. It reads loose files and permits reflective loading.</summary>
    Development,

    /// <summary>A published build. It reads the published archives through the published reader.</summary>
    Published,

    /// <summary>A published NativeAOT build. Production builds use this kind only.</summary>
    PublishedAot,

    /// <summary>
    /// A development build that reads the published archives. It permits reflective loading. It is for development and
    /// testing only, and it never counts as a published or production build.
    /// </summary>
    DevelopmentArchives,
}
