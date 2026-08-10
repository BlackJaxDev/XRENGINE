using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned mutations of the immutable-next render-state tracker.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
        => SetMaterialUniforms(
            material,
            program,
            ResourceRuntime.WrapperLookup.GetOrCreate(program, generateNow: false) as VkRenderProgram,
            LayeredShadowUniformState.CaptureFromCurrentRenderingState());

    internal void SetStencilMask(uint mask)
        => ActiveState.SetStencilWriteMask(mask);

    internal void AllowDepthWrite(bool enabled)
        => ActiveState.SetDepthWriteEnabled(enabled);

    internal void SetClearDepth(float depth)
        => ActiveState.SetClearDepth(depth);

    internal void SetClearStencil(int stencil)
        => ActiveState.SetClearStencil(stencil);

    internal void EnableDepthTest(bool enabled)
        => ActiveState.SetDepthTestEnabled(enabled);

    internal void SetDepthCompare(EComparison comparison)
        => ActiveState.SetDepthCompare(ToVulkanCompareOp(comparison));

    internal void SetCroppingEnabled(bool enabled)
        => ActiveState.SetCroppingEnabled(enabled);

    internal static CompareOp ToVulkanCompareOp(EComparison comparison)
        => comparison switch
        {
            EComparison.Never => CompareOp.Never,
            EComparison.Less => CompareOp.Less,
            EComparison.Equal => CompareOp.Equal,
            EComparison.Lequal => CompareOp.LessOrEqual,
            EComparison.Greater => CompareOp.Greater,
            EComparison.Nequal => CompareOp.NotEqual,
            EComparison.Gequal => CompareOp.GreaterOrEqual,
            EComparison.Always => CompareOp.Always,
            _ => CompareOp.Always
        };

    internal void SetColorMask(bool red, bool green, bool blue, bool alpha)
        => StateTracker.SetColorMask(red, green, blue, alpha);

    internal void SetClearColor(ColorF4 color)
        => StateTracker.SetClearColor(color);

    internal void SetScissor(BoundingRectangle region)
        => StateTracker.SetScissor(region);

    internal void SetViewport(BoundingRectangle region)
        => StateTracker.SetViewport(region);

    internal void ClearViewport()
        => StateTracker.ClearViewport();

    internal void SetIndexedViewportScissors(
        ReadOnlySpan<BoundingRectangle> viewports,
        ReadOnlySpan<BoundingRectangle> scissors)
        => StateTracker.SetIndexedViewportScissors(viewports, scissors);

    internal void ClearIndexedViewportScissors()
        => StateTracker.ClearIndexedViewportScissors();

    internal bool TrySetIndexedViewportScissors(
        ReadOnlySpan<BoundingRectangle> viewports,
        ReadOnlySpan<BoundingRectangle> scissors)
    {
        int count = Math.Min(viewports.Length, scissors.Length);
        if (count <= 0 ||
            !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
            count > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
        {
            return false;
        }

        StateTracker.SetIndexedViewportScissors(viewports[..count], scissors[..count]);
        return true;
    }

    internal void ClearIndexedViewportScissorsIfAny(int count)
    {
        if (count > 0)
            StateTracker.ClearIndexedViewportScissors();
    }
}
