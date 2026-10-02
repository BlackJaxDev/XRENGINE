namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one exact attachment resolve until either framebuffer's physical bindings change.</summary>
internal readonly record struct WebGpuColorResolve(WebGpuFrameBuffer Destination, ulong DestinationRevision,
    int SourceSlot, int DestinationSlot, AbstractRenderAPIObject SourceOwner,
    AbstractRenderAPIObject DestinationOwner, int Command);
