using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Producer-independent visibility payload. Static, skinned, meshlet,
/// traditional, and CPU diagnostic paths preserve this exact identity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 96)]
public readonly record struct AdvancedVisibilityPayload(
    AdvancedGpuHandle Draw,
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material,
    AdvancedSceneGeometryOffsets GeometryOffsets,
    uint PrimitiveSection,
    uint InstanceCount,
    uint FirstIndex,
    uint IndexCount,
    uint VertexCount,
    uint RasterStateClass,
    EAdvancedMaterialCoverageMode Coverage,
    uint CullMode,
    uint PrimitiveTopology,
    EAdvancedVisibilityPayloadFlags Flags)
{
    public AdvancedVisibilityPayload(
        AdvancedGpuHandle Draw,
        AdvancedGpuHandle Geometry,
        AdvancedGpuHandle Material,
        AdvancedSceneGeometryOffsets GeometryOffsets,
        uint PrimitiveSection,
        uint InstanceCount,
        uint FirstIndex,
        uint IndexCount,
        uint VertexCount,
        uint RasterStateClass,
        EAdvancedMaterialCoverageMode Coverage,
        uint CullMode,
        uint PrimitiveTopology,
        bool Skinned,
        bool MeshletsResident,
        bool ForceCpuDiagnostic,
        EAdvancedVelocityValidityReason TemporalReason)
        : this(
            Draw,
            Geometry,
            Material,
            GeometryOffsets,
            PrimitiveSection,
            InstanceCount,
            FirstIndex,
            IndexCount,
            VertexCount,
            RasterStateClass,
            Coverage,
            CullMode,
            PrimitiveTopology,
            PackFlags(
                Skinned,
                MeshletsResident,
                ForceCpuDiagnostic,
                TemporalReason))
    {
    }

    public bool Skinned
        => (Flags & EAdvancedVisibilityPayloadFlags.Skinned) != 0;
    public bool MeshletsResident
        => (Flags &
            EAdvancedVisibilityPayloadFlags.MeshletsResident) != 0;
    public bool ForceCpuDiagnostic
        => (Flags &
            EAdvancedVisibilityPayloadFlags.ForceCpuDiagnostic) != 0;
    public EAdvancedVelocityValidityReason TemporalReason
        => AdvancedReconstructionTemporalFlags.DecodeVelocityReason((uint)Flags);

    private static EAdvancedVisibilityPayloadFlags PackFlags(
        bool skinned,
        bool meshletsResident,
        bool forceCpuDiagnostic,
        EAdvancedVelocityValidityReason temporalReason)
    {
        EAdvancedVisibilityPayloadFlags flags =
            EAdvancedVisibilityPayloadFlags.None;
        if (skinned)
            flags |= EAdvancedVisibilityPayloadFlags.Skinned;
        if (meshletsResident)
            flags |=
                EAdvancedVisibilityPayloadFlags.MeshletsResident;
        if (forceCpuDiagnostic)
            flags |=
                EAdvancedVisibilityPayloadFlags.ForceCpuDiagnostic;
        return (EAdvancedVisibilityPayloadFlags)
            AdvancedReconstructionTemporalFlags.PackVelocityReason(
                (uint)flags,
                temporalReason);
    }
}
