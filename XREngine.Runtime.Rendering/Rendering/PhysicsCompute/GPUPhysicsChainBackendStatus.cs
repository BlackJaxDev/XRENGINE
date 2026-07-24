namespace XREngine.Rendering.Compute;

/// <summary>
/// Last observed capability state for explicitly requested batched GPU physics-chain work.
/// A failed state is diagnostic-only and never authorizes a CPU simulation fallback.
/// </summary>
public sealed record GPUPhysicsChainBackendStatus(
    GPUPhysicsChainBackendState State,
    string BackendName,
    bool CanDispatchGpu,
    bool CpuFallbackUsed,
    string Diagnostic)
{
    public static GPUPhysicsChainBackendStatus NotEvaluated { get; } = new(
        GPUPhysicsChainBackendState.NotEvaluated,
        "None",
        CanDispatchGpu: false,
        CpuFallbackUsed: false,
        "GPU physics-chain backend capability has not been evaluated.");
}
