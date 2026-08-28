namespace XREngine.Rendering.Commands;

/// <summary>
/// Resident record table that owns a canonical dirty range.
/// </summary>
public enum EBackendReadyCanonicalOwner : byte
{
    None,
    Scene,
    Draw,
    Instance,
    Transform,
    Deformation,
    RenderState,
    Material,
    Geometry,
    EditorIdentity,
    Texture,
    Sampler,
    MaterialLayout,
    ShadingKernel,
    PipelineLayout,
    Pipeline,
    DescriptorLayout,
    DescriptorTable,
    RenderPass,
    Output,
    Shader,
    Shadow,
    Probe,
    Light,
    Environment,
    Decal,
    GiResource,
}
