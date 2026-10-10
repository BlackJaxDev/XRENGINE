using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Browser-only draw association for immutable source-basis metadata; never a canonical/persisted record.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct WebGpuAdvancedGeometryBasisRecord(
    AdvancedGpuHandle Draw,
    AdvancedGpuHandle Geometry,
    ulong GeometryRevision,
    uint VertexWordOffset,
    uint VertexCount,
    ulong SourceVersion,
    ulong MeshVersion);
