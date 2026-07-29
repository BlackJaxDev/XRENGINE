using System.Collections.ObjectModel;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Represents one compiled batch of graphics passes sharing a compatible attachment signature.
    /// </summary>
    internal sealed class VulkanCompiledPassBatch
    {
        private readonly List<int> _passIndices = [];

        /// <summary>
        /// Initializes a compiled pass batch descriptor.
        /// </summary>
        internal VulkanCompiledPassBatch(int batchIndex, ERenderGraphPassStage stage, string attachmentSignature)
        {
            BatchIndex = batchIndex;
            Stage = stage;
            AttachmentSignature = attachmentSignature;
        }

        /// <summary>Monotonic index of this batch in compilation order.</summary>
        public int BatchIndex { get; }

        /// <summary>Render-graph stage shared by this batch.</summary>
        public ERenderGraphPassStage Stage { get; }

        /// <summary>Compatibility signature derived from attachment usages.</summary>
        public string AttachmentSignature { get; }

        /// <summary>Read-only ordered pass indices contained in this batch.</summary>
        public ReadOnlyCollection<int> PassIndices => _passIndices.AsReadOnly();

        /// <summary>
        /// Appends a pass index to this batch.
        /// </summary>
        internal void AddPass(int passIndex)
            => _passIndices.Add(passIndex);
    }
}
