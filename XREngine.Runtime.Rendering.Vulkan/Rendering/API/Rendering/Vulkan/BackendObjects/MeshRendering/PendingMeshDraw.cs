using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct PendingMeshDraw(
    VkMeshRenderer Renderer,
    Viewport Viewport,
    Rect2D Scissor,
    Viewport[]? IndexedViewports,
    Rect2D[]? IndexedScissors,
    uint ViewportScissorCount,
    SampleCountFlags RasterizationSamples,
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareOp DepthCompareOp,
    bool StencilTestEnabled,
    StencilOpState FrontStencilState,
    StencilOpState BackStencilState,
    uint StencilWriteMask,
    ColorComponentFlags ColorWriteMask,
    CullModeFlags CullMode,
    FrontFace FrontFace,
    bool BlendEnabled,
    bool AlphaToCoverageEnabled,
    BlendOp ColorBlendOp,
    BlendOp AlphaBlendOp,
    BlendFactor SrcColorBlendFactor,
    BlendFactor DstColorBlendFactor,
    BlendFactor SrcAlphaBlendFactor,
    BlendFactor DstAlphaBlendFactor,
    Matrix4x4 ModelMatrix,
    Matrix4x4 PreviousModelMatrix,
    XRMaterial? MaterialOverride,
    uint Instances,
    EMeshBillboardMode BillboardMode,
    uint TransformId,
    VulkanMeshDrawViewSnapshot? ViewSnapshot,
    VkRenderProgram? PreparedProgram,
    string? PreparedProgramIdentity,
    ulong PreparedProgramLinkGeneration,
    ComputeDispatchSnapshot? ProgramBindingSnapshot)
{
    internal XRCamera? Camera => ViewSnapshot?.Camera;
    internal XRCamera? StereoRightEyeCamera => ViewSnapshot?.RightEyeCamera;
    internal bool IsStereoPass => ViewSnapshot?.IsStereoPass == true;
    internal bool UseUnjitteredProjection
        => ViewSnapshot?.UseUnjitteredProjection == true;
    internal Matrix4x4 ViewMatrix
        => ViewSnapshot?.ViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 InverseViewMatrix
        => ViewSnapshot?.InverseViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 ProjectionMatrix
        => ViewSnapshot?.ProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 InverseProjectionMatrix
        => ViewSnapshot?.InverseProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 ViewProjectionMatrix
        => ViewSnapshot?.ViewProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 ViewProjectionMatrixUnjittered
        => ViewSnapshot?.ViewProjectionMatrixUnjittered ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousViewMatrix
        => ViewSnapshot?.PreviousViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousProjectionMatrix
        => ViewSnapshot?.PreviousProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousViewProjectionMatrix
        => ViewSnapshot?.PreviousViewProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousViewProjectionMatrixUnjittered
        => ViewSnapshot?.PreviousViewProjectionMatrixUnjittered ??
           Matrix4x4.Identity;
    internal Matrix4x4 RightEyeViewMatrix
        => ViewSnapshot?.RightEyeViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 RightEyeInverseViewMatrix
        => ViewSnapshot?.RightEyeInverseViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 RightEyeProjectionMatrix
        => ViewSnapshot?.RightEyeProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 RightEyeInverseProjectionMatrix
        => ViewSnapshot?.RightEyeInverseProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 RightEyeViewProjectionMatrix
        => ViewSnapshot?.RightEyeViewProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 RightEyeViewProjectionMatrixUnjittered
        => ViewSnapshot?.RightEyeViewProjectionMatrixUnjittered ??
           Matrix4x4.Identity;
    internal Matrix4x4 PreviousRightEyeViewMatrix
        => ViewSnapshot?.PreviousRightEyeViewMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousRightEyeProjectionMatrix
        => ViewSnapshot?.PreviousRightEyeProjectionMatrix ?? Matrix4x4.Identity;
    internal Matrix4x4 PreviousRightEyeViewProjectionMatrix
        => ViewSnapshot?.PreviousRightEyeViewProjectionMatrix ??
           Matrix4x4.Identity;
    internal Matrix4x4 PreviousRightEyeViewProjectionMatrixUnjittered
        => ViewSnapshot?.PreviousRightEyeViewProjectionMatrixUnjittered ??
           Matrix4x4.Identity;
    internal Vector3 CameraPosition
        => ViewSnapshot?.CameraPosition ?? Vector3.Zero;
    internal Vector3 CameraForward
        => ViewSnapshot?.CameraForward ?? Vector3.UnitZ;
    internal Vector3 CameraUp => ViewSnapshot?.CameraUp ?? Vector3.UnitY;
    internal Vector3 CameraRight => ViewSnapshot?.CameraRight ?? Vector3.UnitX;
    internal int RenderAreaWidth => ViewSnapshot?.RenderAreaWidth ?? 0;
    internal int RenderAreaHeight => ViewSnapshot?.RenderAreaHeight ?? 0;
    internal LayeredShadowUniformState ShadowUniformState
        => ViewSnapshot?.ShadowUniformState ?? default;

    /// <summary>
    /// Detaches every producer-owned mutable collection before a frame plan is
    /// sealed. Renderer, material, and camera references are logical owners;
    /// native binding dictionaries and indexed viewport arrays are snapshot data.
    /// </summary>
    internal PendingMeshDraw CreateSealedCopy()
        => this with
        {
            IndexedViewports = IndexedViewports is null
                ? null
                : (Viewport[])IndexedViewports.Clone(),
            IndexedScissors = IndexedScissors is null
                ? null
                : (Rect2D[])IndexedScissors.Clone(),
            ProgramBindingSnapshot = ProgramBindingSnapshot?.CreateSealedCopy(),
        };

    internal VulkanAutoUniformPublicationSnapshot AutoUniformPublication
    {
        get;
        init;
    }

    /// <summary>
    /// Captures the stable CPU-direct dynamic record written into the completed frame slot.
    /// Binding identity remains in the immutable recording snapshot; these values may change
    /// every frame without invalidating compatible recorded ranges.
    /// </summary>
    public VulkanCpuDirectDynamicData CaptureDynamicData(
        uint viewId,
        uint passMask,
        uint skinningId = 0u,
        uint blendshapeId = 0u,
        uint editorId = 0u)
    {
        XRMaterial? material = MaterialOverride ?? Renderer.MeshRenderer.Material;
        uint flags = (uint)BillboardMode & 0xFFu;
        if (IsStereoPass)
            flags |= 1u << 8;
        if (UseUnjitteredProjection)
            flags |= 1u << 9;

        return new VulkanCpuDirectDynamicData(
            ModelMatrix,
            PreviousModelMatrix,
            material is null ? 0u : unchecked((uint)material.GetHashCode()),
            skinningId,
            blendshapeId,
            editorId,
            flags,
            passMask,
            viewId,
            TransformId);
    }
}
