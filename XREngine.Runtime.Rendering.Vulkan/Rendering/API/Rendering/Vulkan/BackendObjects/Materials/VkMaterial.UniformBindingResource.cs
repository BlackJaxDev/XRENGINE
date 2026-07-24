using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public partial class VkMaterial
        {
            /// <summary>
            /// Tracks a single material-owned uniform buffer binding, including per-frame
            /// Vulkan buffer handles and their backing device memory.
            /// </summary>
            private sealed class UniformBindingResource
            {
                /// <summary>The shader uniform name this resource is bound to.</summary>
                public required string Name { get; init; }

                /// <summary>The material <see cref="ShaderVar"/> whose value is uploaded each frame for legacy single-uniform bindings.</summary>
                public ShaderVar? Parameter { get; init; }

                /// <summary>Reflected std140/std430 block metadata used when the shader compiler rewrote loose uniforms into a UBO.</summary>
                public AutoUniformBlockInfo? ReflectedBlock { get; init; }

                /// <summary>Size in bytes of the uniform buffer.</summary>
                public required uint Size { get; init; }

                /// <summary>Per-frame Vulkan buffer handles.</summary>
                public required Silk.NET.Vulkan.Buffer[] Buffers { get; init; }

                /// <summary>Per-frame device memory backing the corresponding <see cref="Buffers"/> entries.</summary>
                public required DeviceMemory[] Memories { get; init; }
            }
        }
    }
}
