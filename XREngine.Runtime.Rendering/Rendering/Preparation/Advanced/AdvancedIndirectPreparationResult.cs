using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Structural indirect-plan diagnostics. Runtime visible counts are excluded.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedIndirectPreparationResult(
    uint PayloadCount,
    uint RangeCount,
    uint StaticMeshletCount,
    uint SkinnedMeshletCount,
    uint TraditionalFallbackCount,
    uint CpuDiagnosticCount,
    ulong StructuralGeneration,
    bool RequiresPrimaryRerecord,
    bool RequiresCpuCount);
