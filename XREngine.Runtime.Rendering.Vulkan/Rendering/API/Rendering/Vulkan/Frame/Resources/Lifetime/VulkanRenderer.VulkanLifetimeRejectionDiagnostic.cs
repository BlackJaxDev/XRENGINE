namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanLifetimeRejectionDiagnostic(
        VulkanResourceLifetimeKey Resource,
        string Owner,
        ulong OldGeneration,
        ulong NewGeneration,
        string Output,
        ulong CommandBufferHandle,
        VulkanRetirementTicket RetirementTicket,
        EVulkanResourceLifetimeState State,
        string Reason)
    {
        public override string ToString()
            => $"resource={Resource} owner={Owner} oldGeneration={OldGeneration} newGeneration={NewGeneration} " +
               $"output={Output} commandBuffer=0x{CommandBufferHandle:X} " +
               $"retirementTicket={DescribeVulkanRetirementTicket(RetirementTicket)} state={State} reason={Reason}";
    }
}
