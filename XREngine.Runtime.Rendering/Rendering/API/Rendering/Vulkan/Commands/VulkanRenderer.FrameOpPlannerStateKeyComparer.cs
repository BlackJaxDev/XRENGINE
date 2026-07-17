namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private sealed class FrameOpPlannerStateKeyComparer : IEqualityComparer<FrameOpPlannerStateKey>
    {
        public static FrameOpPlannerStateKeyComparer Instance { get; } = new();

        public bool Equals(FrameOpPlannerStateKey x, FrameOpPlannerStateKey y)
            => x.ContextKind == y.ContextKind &&
               x.PipelineIdentity == y.PipelineIdentity &&
               x.ViewportIdentity == y.ViewportIdentity &&
               x.DisplayWidth == y.DisplayWidth &&
               x.DisplayHeight == y.DisplayHeight &&
               x.InternalWidth == y.InternalWidth &&
               x.InternalHeight == y.InternalHeight &&
               x.OutputFrameBufferIdentity == y.OutputFrameBufferIdentity &&
               x.OutputTargetIdentity == y.OutputTargetIdentity &&
               x.ResourceRegistrySignature == y.ResourceRegistrySignature &&
               x.PassMetadataSignature == y.PassMetadataSignature &&
               x.ResourceGeneration == y.ResourceGeneration &&
               x.SubmissionQueueFamily == y.SubmissionQueueFamily;

        public int GetHashCode(FrameOpPlannerStateKey value)
        {
            unchecked
            {
                int hash = (int)value.ContextKind;
                hash = (hash * 397) ^ value.PipelineIdentity;
                hash = (hash * 397) ^ value.ViewportIdentity;
                hash = (hash * 397) ^ (int)value.DisplayWidth;
                hash = (hash * 397) ^ (int)value.DisplayHeight;
                hash = (hash * 397) ^ (int)value.InternalWidth;
                hash = (hash * 397) ^ (int)value.InternalHeight;
                hash = (hash * 397) ^ value.OutputFrameBufferIdentity;
                hash = (hash * 397) ^ value.OutputTargetIdentity;
                hash = (hash * 397) ^ value.ResourceRegistrySignature;
                hash = (hash * 397) ^ value.PassMetadataSignature;
                hash = (hash * 397) ^ value.ResourceGeneration.GetHashCode();
                return (hash * 397) ^ (int)value.SubmissionQueueFamily;
            }
        }
    }
}
