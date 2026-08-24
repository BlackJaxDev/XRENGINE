using System.Text;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class CommandBufferRecordingScratch
{
    public sealed class FboAttachmentLayoutScratch
    {
        public ImageLayout[] Layouts { get; set; } = [];
    }
}

