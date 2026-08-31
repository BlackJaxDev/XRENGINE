namespace XREngine.Rendering.Vulkan;

/// <summary>Immutable cold-path observation of one exact native allocation generation.</summary>
public readonly record struct VulkanNativeBufferLifetimeDiagnostic(
    bool Found,
    bool PendingRetirement,
    bool Destroyed,
    bool RetirementReady,
    int RecordedReferences,
    int DescriptorReferences,
    int TemplateReferences,
    int QueuedReferences,
    ulong LastGraphicsSequence,
    ulong LastTransferSequence,
    ulong CompletedGraphicsSequence);
