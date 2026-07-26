using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkRenderProgram
    {
        private readonly struct ComputeUniformBuffer(
            Silk.NET.Vulkan.Buffer buffer,
            DeviceMemory memory,
            uint size,
            void* mapped)
        {
            public Silk.NET.Vulkan.Buffer Buffer { get; } = buffer;
            public DeviceMemory Memory { get; } = memory;
            public uint Size { get; } = size;
            public void* Mapped { get; } = mapped;
        }

    }

    }
