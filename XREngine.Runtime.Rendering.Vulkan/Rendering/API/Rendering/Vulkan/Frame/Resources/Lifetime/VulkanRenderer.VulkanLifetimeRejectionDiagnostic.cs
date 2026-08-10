namespace XREngine.Rendering.Vulkan;

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
               $"retirementTicket=gfx:{RetirementTicket.GraphicsSequence}/transfer:{RetirementTicket.TransferSequence}/other:{RetirementTicket.OtherSequence}/generation:{RetirementTicket.ResourceGeneration}/external:{RetirementTicket.ExternalOwnershipPending}/pins:{RetirementTicket.PinSet?.Count ?? 0} state={State} reason={Reason}";
}
