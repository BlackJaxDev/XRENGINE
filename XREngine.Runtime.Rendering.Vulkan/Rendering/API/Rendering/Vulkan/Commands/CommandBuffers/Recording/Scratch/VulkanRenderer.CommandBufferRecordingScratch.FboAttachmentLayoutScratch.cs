using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal sealed partial class CommandBufferRecordingScratch
    {
        public sealed class FboAttachmentLayoutScratch
        {
            public ImageLayout[] Layouts { get; set; } = [];
        }
    }
}

