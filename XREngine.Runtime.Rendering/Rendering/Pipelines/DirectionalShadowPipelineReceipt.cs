namespace XREngine.Rendering;

/// <summary>Fixed-capacity rendering-layer receipt for a directional-shadow atlas pipeline invocation.</summary>
public readonly record struct DirectionalShadowPipelineReceipt(
    EDirectionalShadowPipelineReceiptStage Stage,
    int PipelineInstanceId,
    int TargetIdentity,
    string TargetName,
    string? Failure);
