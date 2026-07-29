using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Aggregate deformation dispatch diagnostics.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedDeformationDispatchTelemetry(
    uint JobCount,
    ulong VertexCount,
    ulong OutputBytes,
    uint DispatchCount,
    uint FamilyOverflowCount,
    uint AdmissionOverflowCount,
    double GpuMilliseconds);
