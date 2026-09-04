using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>Segments in one publication's packed native handle-lookup image.</summary>
internal readonly record struct VulkanAdvancedSceneLookupSegments(
    AdvancedGpuLookupSegment Draws,
    AdvancedGpuLookupSegment Instances,
    AdvancedGpuLookupSegment Geometry,
    AdvancedGpuLookupSegment Transforms,
    AdvancedGpuLookupSegment Deformations,
    AdvancedGpuLookupSegment RenderStates,
    AdvancedGpuLookupSegment EditorIdentities,
    AdvancedGpuLookupSegment Materials,
    AdvancedGpuLookupSegment ShadingKernels,
    AdvancedGpuLookupSegment MaterialLayouts,
    AdvancedGpuLookupSegment Textures,
    AdvancedGpuLookupSegment Samplers,
    AdvancedGpuLookupSegment Shadows);
