using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Admits validated direct triangle coverage on the Vulkan backend.</summary>
public sealed partial class VulkanRenderer : IAuthoredUdimTriangleCoverageBackendCapability
{
    bool IAuthoredUdimTriangleCoverageBackendCapability.SupportsUdimTriangleCoverage
        => SupportsGeometryShader;
}
