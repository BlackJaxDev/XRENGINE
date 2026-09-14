using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Immutable capture state for one source-camera view of a native projective mirror.
/// </summary>
internal readonly record struct AdvancedProjectiveMirrorView(
    XRTexture2D? texture,
    Matrix4x4 reflectedViewProjection,
    ulong sourceCameraIdentity,
    ulong sourceContentGeneration,
    AdvancedMutableTexturePublicationLifetime? canonicalPublicationLifetime,
    bool valid,
    bool framebufferYDown)
{
    internal XRTexture2D? Texture { get; } = texture;
    internal Matrix4x4 ReflectedViewProjection { get; } = reflectedViewProjection;
    internal ulong SourceCameraIdentity { get; } = sourceCameraIdentity;
    internal ulong SourceContentGeneration { get; } = sourceContentGeneration;
    internal AdvancedMutableTexturePublicationLifetime? CanonicalPublicationLifetime { get; } = canonicalPublicationLifetime;
    internal bool Valid { get; } = valid;
    internal bool FramebufferYDown { get; } = framebufferYDown;
}
