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
    uint IndirectIndexedCount,
    uint CpuDirectStaticIndexedCount,
    uint CpuDirectPreSkinnedCount,
    ulong StructuralGeneration,
    bool RequiresPrimaryRerecord,
    bool RequiresCpuCount)
{
    public uint TraditionalFallbackCount => IndirectIndexedCount;
    public uint CpuDiagnosticCount
        => checked(CpuDirectStaticIndexedCount + CpuDirectPreSkinnedCount);
}
