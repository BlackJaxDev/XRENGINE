using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Bounded GPU-only early/late visibility work. Counts and indirect arguments
/// remain in GPU buffers and never become a primary-recording dependency.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedVisibilityDispatchPlan(
    ulong ViewHistoryKey,
    uint CandidateCapacity,
    uint EarlyWorkGroupCount,
    uint LateWorkGroupCount,
    uint EarlyIndirectArgumentOffset,
    uint DeferredCandidateOffset,
    uint LateIndirectArgumentOffset,
    uint PersistentStateOffset,
    uint GpuCounterOffset,
    bool UsesPreviousDepthPyramid,
    bool LateTestsDeferredOnly,
    bool RequiresCpuCount,
    bool RequiresReadback);
