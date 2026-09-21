namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Host-owned surface and composition bindings supplied to a provider without exposing a concrete pipeline or framebuffer factory.
/// </summary>
public sealed record GlobalIlluminationHostResources(
    string DepthTexture,
    string NormalTexture,
    string AlbedoTexture,
    string RmseTexture,
    string AmbientOcclusionTexture,
    string CompositionTarget);
