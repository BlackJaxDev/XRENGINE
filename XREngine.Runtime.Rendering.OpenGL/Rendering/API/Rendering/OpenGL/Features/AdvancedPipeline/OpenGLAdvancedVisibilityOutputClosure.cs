namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Frozen OpenGL image identities for one Advanced stage request.  Texture
/// references keep the registry generation alive while the executor uses the
/// captured native names.
/// </summary>
internal readonly record struct OpenGLAdvancedVisibilityOutputClosure(
    XRTexture Identity,
    uint IdentityId,
    XRTexture Metadata,
    uint MetadataId,
    XRTexture Selection,
    uint SelectionId,
    XRTexture Depth,
    uint DepthId,
    XRTexture DepthPyramid,
    uint DepthPyramidId,
    XRTexture? AmbientOcclusion,
    uint AmbientOcclusionId,
    XRTexture? Hdr,
    uint HdrId,
    XRTexture? Velocity,
    uint VelocityId,
    XRTexture? ReactiveMask,
    uint ReactiveMaskId,
    XRTexture? ShadingDiagnostics,
    uint ShadingDiagnosticsId,
    uint Width,
    uint Height,
    uint LayerCount,
    uint NativeViewIndex)
{
    internal bool IsValid
        => IdentityId != 0u && MetadataId != 0u && SelectionId != 0u &&
           DepthId != 0u && DepthPyramidId != 0u && Width != 0u && Height != 0u &&
           LayerCount != 0u && NativeViewIndex < LayerCount;

    internal bool HasNativeComputeOutputs
        => AmbientOcclusionId != 0u && HdrId != 0u && VelocityId != 0u &&
           ReactiveMaskId != 0u && ShadingDiagnosticsId != 0u;
}
