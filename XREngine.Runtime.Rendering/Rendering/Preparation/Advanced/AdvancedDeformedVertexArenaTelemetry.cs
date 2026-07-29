using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Per-frame deformation arena capacity and temporal diagnostics.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformedVertexArenaTelemetry(
    ulong StorageGeneration,
    uint VertexCapacity,
    uint HighWaterVertices,
    uint PendingVertexCapacity,
    uint AllocationFailureCount,
    uint CapacityGrowthCount,
    uint GrowthDeferralCount,
    uint SlotReuseDeferralCount,
    uint VelocityInvalidationCount,
    int RetiredGenerationCount);
