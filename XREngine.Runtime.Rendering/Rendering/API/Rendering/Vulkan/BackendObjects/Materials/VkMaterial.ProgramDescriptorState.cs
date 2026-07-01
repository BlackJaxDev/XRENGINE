using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        public partial class VkMaterial
        {
            /// <summary>
            /// Holds all Vulkan descriptor resources that have been allocated for a specific
            /// <see cref="VkRenderProgram"/>. A separate state is maintained per program because
            /// different programs may declare different descriptor set layouts.
            /// </summary>
            private sealed class ProgramDescriptorState
            {
                /// <summary>The render program this state was created for.</summary>
                public required VkRenderProgram Program { get; init; }

                /// <summary>Snapshot of the program's descriptor binding metadata at creation time.</summary>
                public required IReadOnlyList<DescriptorBindingInfo> Bindings { get; init; }

                /// <summary>
                /// Per-frame descriptor sets. Indexed as <c>[frameIndex][setIndex]</c>.
                /// One full copy per swap-chain image avoids write-after-read hazards.
                /// </summary>
                public required DescriptorSet[][] DescriptorSets { get; init; }

                /// <summary>
                /// Per-frame descriptor heap push-data payloads. Populated alongside descriptor
                /// set writes and used only when <c>VK_EXT_descriptor_heap</c> is active.
                /// </summary>
                public DescriptorHeapPushDataPayload[] DescriptorHeapPushData { get; init; } = [];

                /// <summary>
                /// Uniform buffer resources keyed by <c>(set, binding)</c>.
                /// Only material-owned uniform bindings appear here; engine-managed uniforms are excluded.
                /// </summary>
                public required Dictionary<(uint set, uint binding), UniformBindingResource> UniformBindings { get; init; }

                /// <summary>
                /// True when descriptor reflection produced at least one material-owned parameter,
                /// sampler, image, or texel-buffer binding for this material to service.
                /// </summary>
                public required bool HasMaterialParameterOrSamplerBindings { get; init; }

                /// <summary>Number of swap-chain images (frames in flight) at the time of creation.</summary>
                public required int FrameCount { get; init; }

                /// <summary>Number of descriptor set layouts declared by the program.</summary>
                public required int SetCount { get; init; }

                /// <summary>
                /// Hash of the binding layout used to detect when the program's descriptor schema
                /// has changed, requiring the state to be rebuilt.
                /// </summary>
                public required ulong SchemaFingerprint { get; init; }

                /// <summary>
                /// Hash of concrete Vulkan texture descriptor handles written into the descriptor sets.
                /// </summary>
                public ulong ResourceFingerprint;

                /// <summary>The Vulkan descriptor pool from which all sets in this state were allocated.</summary>
                public DescriptorPool DescriptorPool;

                /// <summary>
                /// When <c>true</c>, the descriptor writes need to be re-issued
                /// (e.g. after a texture or parameter change).
                /// </summary>
                public bool Dirty = true;
            }
        }
    }
}
