using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanIndirectDrawState(
    XRRenderProgram Program,
    XRMaterial Material,
    Matrix4x4 ModelMatrix);
