namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Render-pipeline pass state that changes the graphics pipeline of a mesh draw.
/// </summary>
/// <remarks>
/// A draw captures these flags when its pass queues it. Pipeline warm-up and command
/// recording run later, often on other threads and after other pipelines changed the
/// ambient rendering state. Both must build the pipeline key from the captured flags,
/// or they build different keys for one draw and the recorded frame misses the
/// pipeline that the warm-up compiled.
/// </remarks>
[Flags]
internal enum EVulkanMeshDrawPassStateFlags : byte
{
    None = 0,
    ShadowPass = 1 << 0,
    DepthNormalMaterialVariants = 1 << 1,
    DirectionalCascadeLayeredShadowPass = 1 << 2,
    PointLightLayeredShadowPass = 1 << 3,
}
