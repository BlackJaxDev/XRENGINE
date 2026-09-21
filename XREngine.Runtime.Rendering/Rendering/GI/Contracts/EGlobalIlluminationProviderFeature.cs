namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Editor-facing surfaces exposed by a GI provider descriptor.
/// These flags describe registration metadata only; static support remains the
/// authority for whether the provider can allocate or execute.
/// </summary>
[Flags]
public enum EGlobalIlluminationProviderFeature
{
    None = 0,
    Authoring = 1 << 0,
    DebugPresentation = 1 << 1,
    Baking = 1 << 2,
}
