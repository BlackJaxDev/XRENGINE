using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>
    /// Holds the state of one walk over the retained command rows.
    /// The walk fields carry data from one row to the next row.
    /// The row fields hold the inputs that the last evaluated row compared.
    /// A caller that records witnesses copies the row fields after each
    /// successful evaluation and does not read the live inputs again. Thus a
    /// witness always holds the exact value that the comparison used.
    /// </summary>
    private ref struct UnchangedRowGateScratch
    {
        /// <summary>The dispatcher of the covered rows, or null when no row is covered.</summary>
        public GPUPhysicsChainDispatcher? BoundsDispatcher;

        /// <summary>The material of the most recent header check.</summary>
        public XRMaterial? CheckedHeaderMaterial;

        /// <summary>The material handle of the most recent header check.</summary>
        public AdvancedGpuHandle CheckedHeaderHandle;

        /// <summary>The supported rows that passed.</summary>
        public int SupportedRows;

        /// <summary>The covered static rows that reused their draw plan.</summary>
        public int ReusedStaticPlans;

        /// <summary>The material layout selections of the unsupported rows.</summary>
        public int MaterialResolveAttempts;

        /// <summary>The plan source of the row, or null when the row has no source.</summary>
        public IRenderCommandMesh? RowSource;

        /// <summary>True when the plan registered the row.</summary>
        public bool RowSupported;

        /// <summary>The render-side renderer of the source command.</summary>
        public XRMeshRenderer? RowRenderer;

        /// <summary>The render-side material override of the source command.</summary>
        public XRMaterial? RowMaterialOverride;

        /// <summary>The mesh of the source primitive.</summary>
        public XRMesh? RowMesh;

        /// <summary>The mesh vertex count that the row compared.</summary>
        public int RowMeshVertexCount;

        /// <summary>The mesh index count that the row compared.</summary>
        public int RowMeshIndexCount;

        /// <summary>The mesh geometry revision that the row compared.</summary>
        public long RowMeshGeometryRevision;

        /// <summary>The mesh primitive type that the row compared.</summary>
        public EPrimitiveType RowMeshPrimitiveType;

        /// <summary>The mesh triangle count that the row compared, or -1 when the mesh has no triangle list.</summary>
        public int RowMeshTriangleCount;

        /// <summary>The effective material of the source primitive.</summary>
        public XRMaterial? RowMaterial;

        /// <summary>The effective raster options of the source primitive.</summary>
        public RenderingParameters? RowRenderOptions;

        /// <summary>The render state that the row compared with its plan.</summary>
        public AdvancedRenderStateRecord RowRenderState;

        /// <summary>The material binding layout version that the row compared.</summary>
        public ulong RowMaterialBindingLayoutVersion;

        /// <summary>The material binding value version that the row compared.</summary>
        public ulong RowMaterialBindingValueVersion;

        /// <summary>The material binding resource version that the row compared.</summary>
        public ulong RowMaterialBindingResourceVersion;

        /// <summary>The material shader state revision that the row compared.</summary>
        public long RowMaterialShaderStateRevision;

        /// <summary>The material uber state revision that the row compared.</summary>
        public long RowMaterialUberStateRevision;

        /// <summary>The registered material handle of the row.</summary>
        public AdvancedGpuHandle RowMaterialHandle;

        /// <summary>True when this row ran the material header check.</summary>
        public bool RowHeaderChecked;

        /// <summary>The committed layout that the header check used.</summary>
        public MaterialBindingLayout? RowHeaderLayout;

        /// <summary>The committed coverage mode that the header check used.</summary>
        public EAdvancedMaterialCoverageMode RowHeaderCoverage;

        /// <summary>The committed render state class that the header check used.</summary>
        public EAdvancedMaterialRenderStateClass RowHeaderState;

        /// <summary>
        /// The input group whose comparison failed, when the row did not pass.
        /// The value names the witness that detects a change of that group.
        /// </summary>
        public EAdvancedPreparedGateOutcome RowMismatch;

        /// <summary>
        /// Clears the row fields before the next row is evaluated, so that no
        /// value of a previous row stays in the outputs.
        /// </summary>
        public void ClearRow()
        {
            RowSource = null;
            RowSupported = false;
            RowRenderer = null;
            RowMaterialOverride = null;
            RowMesh = null;
            RowMeshVertexCount = 0;
            RowMeshIndexCount = 0;
            RowMeshGeometryRevision = 0L;
            RowMeshPrimitiveType = default;
            RowMeshTriangleCount = -1;
            RowMaterial = null;
            RowRenderOptions = null;
            RowRenderState = default;
            RowMaterialBindingLayoutVersion = 0UL;
            RowMaterialBindingValueVersion = 0UL;
            RowMaterialBindingResourceVersion = 0UL;
            RowMaterialShaderStateRevision = 0L;
            RowMaterialUberStateRevision = 0L;
            RowMaterialHandle = AdvancedGpuHandle.Invalid;
            RowHeaderChecked = false;
            RowHeaderLayout = null;
            RowHeaderCoverage = default;
            RowHeaderState = default;
            RowMismatch = EAdvancedPreparedGateOutcome.None;
        }
    }
}
