namespace XREngine.Rendering.Vulkan;

/// <summary>
/// The values that a mesh draw's descriptor proof depended on when it last passed. When a later
/// frame finds the same stamp for the same draw key and descriptor frame slot, nothing that the
/// proof reads has changed, so the draw reuses its descriptor allocation without building the
/// owner key, resolving published signatures, or resolving and hashing frame-source images.
/// </summary>
/// <param name="ImageRevision">Lifetime-tracker revision of images, image views and samplers.</param>
/// <param name="BufferRevision">Lifetime-tracker revision of native buffer publications.</param>
/// <param name="ProgramLinkGeneration">Link generation of the draw's program.</param>
/// <param name="MaterialLayoutVersion">Binding layout version of the draw's material.</param>
/// <param name="MaterialResourceVersion">Binding resource version of the draw's material.</param>
/// <param name="ViewFamilyIdentity">Descriptor view family of the draw's materialization.</param>
/// <param name="PipelineIdentity">Identity of the render pipeline that resolves frame-source textures.</param>
/// <param name="PoolHandle">Descriptor pool of the allocation; a pool reset gives a new state.</param>
/// <param name="TopologyGeneration">Topology generation of the allocation.</param>
/// <param name="ContentGeneration">Content generation of the allocation (advanced by descriptor writes).</param>
internal readonly record struct DescriptorProofStamp(
    ulong ImageRevision,
    ulong BufferRevision,
    ulong ProgramLinkGeneration,
    ulong MaterialLayoutVersion,
    ulong MaterialResourceVersion,
    int ViewFamilyIdentity,
    int PipelineIdentity,
    ulong PoolHandle,
    ulong TopologyGeneration,
    ulong ContentGeneration);
