using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
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
        XRCamera? Camera,
        XRCamera? StereoRightEyeCamera,
        bool IsStereoPass,
        bool UseUnjitteredProjection,
        uint TransformId,
        // Camera matrices/vectors are snapshotted at enqueue time
        // while the camera is still the active rendering camera. The command buffer is
        // recorded later, after the pipeline camera stack has been popped, so reading
        // Camera.* at record time can yield stale values.
        Matrix4x4 ViewMatrix,
        Matrix4x4 InverseViewMatrix,
        Matrix4x4 ProjectionMatrix,
        Matrix4x4 InverseProjectionMatrix,
        Matrix4x4 ViewProjectionMatrix,
        Matrix4x4 ViewProjectionMatrixUnjittered,
        Matrix4x4 PreviousViewMatrix,
        Matrix4x4 PreviousProjectionMatrix,
        Matrix4x4 PreviousViewProjectionMatrix,
        Matrix4x4 PreviousViewProjectionMatrixUnjittered,
        Matrix4x4 RightEyeViewMatrix,
        Matrix4x4 RightEyeInverseViewMatrix,
        Matrix4x4 RightEyeProjectionMatrix,
        Matrix4x4 RightEyeInverseProjectionMatrix,
        Matrix4x4 RightEyeViewProjectionMatrix,
        Matrix4x4 RightEyeViewProjectionMatrixUnjittered,
        Matrix4x4 PreviousRightEyeViewMatrix,
        Matrix4x4 PreviousRightEyeProjectionMatrix,
        Matrix4x4 PreviousRightEyeViewProjectionMatrix,
        Matrix4x4 PreviousRightEyeViewProjectionMatrixUnjittered,
        Vector3 CameraPosition,
        Vector3 CameraForward,
        Vector3 CameraUp,
        Vector3 CameraRight,
        // Render-area dimensions snapshotted at enqueue time. The live
        // RuntimeEngine.Rendering.State.RenderArea is derived from the pipeline's
        // CurrentRenderRegion, which is reset to Empty by the time the command buffer
        // is recorded, so ScreenWidth/ScreenHeight engine uniforms (used by the debug
        // line/point geometry shaders) must read these snapshots instead.
        int RenderAreaWidth,
        int RenderAreaHeight,
        LayeredShadowUniformState ShadowUniformState,
        VkRenderProgram? PreparedProgram,
        string? PreparedProgramIdentity,
        ComputeDispatchSnapshot? ProgramBindingSnapshot);
}
