namespace XREngine.Rendering;

/// <summary>Point-in-time copy of opt-in directional-shadow pipeline receipts.</summary>
public sealed record DirectionalShadowPipelineDiagnosticsSnapshot(
    bool Enabled,
    DirectionalShadowPipelineReceipt[] Receipts);
