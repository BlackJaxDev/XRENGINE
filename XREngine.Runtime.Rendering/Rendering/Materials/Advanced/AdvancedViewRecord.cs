using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Complete temporal view state shared by native shading and post-processing.
/// Matrices use the explicit row-major, row-vector convention declared by the
/// advanced shader access library.
/// </summary>
[XREngine.Rendering.Shaders.GpuRecord("XRAdvancedViewRecord")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedViewRecord
{
    public Matrix4x4 View;
    public Matrix4x4 ProjectionJittered;
    public Matrix4x4 ProjectionUnjittered;
    public Matrix4x4 ViewProjectionJittered;
    public Matrix4x4 ViewProjectionUnjittered;

    public Matrix4x4 PreviousView;
    public Matrix4x4 PreviousProjectionJittered;
    public Matrix4x4 PreviousProjectionUnjittered;
    public Matrix4x4 PreviousViewProjectionJittered;
    public Matrix4x4 PreviousViewProjectionUnjittered;

    public Matrix4x4 InverseViewProjectionJittered;
    public Matrix4x4 InverseViewProjectionUnjittered;

    public Vector4 CameraPositionAndNear;
    public Vector4 CameraForwardAndFar;
    public Vector4 RenderSizeAndInverse;
    public Vector4 OutputSizeAndInverse;
    public Vector4 CurrentAndPreviousJitter;
    public Vector4 DepthParams;

    public uint ViewId;
    public uint OutputLayer;
    public EAdvancedViewRecordFlags Flags;
    public uint HistoryKeyLo;

    public uint HistoryKeyHi;
    public uint ViewGeneration;
    public uint ViewMaskLo;
    public uint ViewMaskHi;

    /// <summary>Frozen target-UV center, maximum conservative mip bias, and enabled flag.</summary>
    public Vector4 FoveationCenterAndBias;
    /// <summary>Ordered inner, guard, middle, and outer eccentricity radii.</summary>
    public Vector4 FoveationRadii;

    public uint SourceCameraIdentityLo;
    public uint SourceCameraIdentityHi;
    public uint CullingLayerMask;
    public uint RenderTimeBits;

    /// <summary>
    /// World-space clipping plane (<c>xyz</c> = outward unit normal, <c>w</c> = signed plane offset)
    /// applied to <c>gl_ClipDistance[0]</c> when <see cref="EAdvancedViewRecordFlags.ClipPlaneEnabled"/> is set.
    /// </summary>
    public Vector4 ClipPlaneWorld;

    /// <summary>Owning primary eye view index (<c>0..primaryViewCount - 1</c>) for portal sub-views.</summary>
    public uint PrimaryViewIndex;

    /// <summary>Parent view index in the portal recursion tree.</summary>
    public uint ParentViewIndex;

    /// <summary>Portal stencil node identifier (<c>0</c> for primary views, <c>1..31</c> for active portal nodes).</summary>
    public uint StencilNodeId;

    /// <summary>
    /// Packed portal recursion depth and self-exclusion draw index: bits 0..3 hold recursion depth,
    /// and bits 4..31 hold the excluded mirror draw index (<c>0</c> means no draw excluded).
    /// </summary>
    public uint DepthAndExcludeDraw;
}
