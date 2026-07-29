using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Complete temporal view state shared by native shading and post-processing.
/// Matrices use the explicit row-major, row-vector convention declared by the
/// advanced shader access library.
/// </summary>
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
}
