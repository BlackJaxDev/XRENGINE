using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>Captures one managed output step without claiming a native draw.</summary>
internal struct VulkanWorldUiStepExpectation
{
    internal EUIWorldOutputStage Stage;
    internal XRTexture? Source;
    internal XRFrameBuffer? Destination;
    internal XRMeshRenderer? Mesh;
    internal XRMaterial? Material;
    internal BoundingRectangle Region;
    internal ulong Token;
    internal bool Authored;
}
