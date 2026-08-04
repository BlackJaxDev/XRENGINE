namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Describes the semantic access associated with a Vulkan image layout.
    /// </summary>
    internal enum EVulkanImageAccessIntent : byte
    {
        /// <summary>No prior contents or access contract exists.</summary>
        Undefined,

        /// <summary>The presentation engine reads the image.</summary>
        Present,

        /// <summary>A render pass reads or writes a color attachment.</summary>
        ColorAttachment,

        /// <summary>A render pass reads or writes depth or stencil data.</summary>
        DepthStencilAttachment,

        /// <summary>A shader samples the image without writing it.</summary>
        SampledRead,

        /// <summary>A shader or depth test reads depth or stencil data.</summary>
        DepthStencilRead,

        /// <summary>A shader performs general storage-image reads or writes.</summary>
        StorageReadWrite,

        /// <summary>A transfer command reads from the image.</summary>
        TransferRead,

        /// <summary>A transfer command writes to the image.</summary>
        TransferWrite,
    }
}
