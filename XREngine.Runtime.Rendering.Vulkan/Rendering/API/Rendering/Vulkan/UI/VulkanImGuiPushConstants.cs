using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

[StructLayout(LayoutKind.Sequential)]
internal struct VulkanImGuiPushConstants
{
    internal Vector2 Scale;
    internal Vector2 Translate;
}
