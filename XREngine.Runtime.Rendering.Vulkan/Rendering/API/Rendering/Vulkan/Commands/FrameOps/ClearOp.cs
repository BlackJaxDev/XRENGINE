using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed record ClearOp(
    int PassIndex,
    XRFrameBuffer? Target,
    bool ClearColor,
    bool ClearDepth,
    bool ClearStencil,
    ColorF4 Color,
    float Depth,
    uint Stencil,
    Rect2D Rect,
    FrameOpContext Context) 
    : FrameOp(PassIndex, Target, Context)
{
    public bool ClearColor { get; private set; } = ClearColor;
    public bool ClearDepth { get; private set; } = ClearDepth;
    public bool ClearStencil { get; private set; } = ClearStencil;
    public ColorF4 Color { get; private set; } = Color;
    public float Depth { get; private set; } = Depth;
    public uint Stencil { get; private set; } = Stencil;
    public Rect2D Rect { get; private set; } = Rect;
    public override EVulkanPrimaryPlanNodeKind Kind => EVulkanPrimaryPlanNodeKind.Clear;

    internal override int RecordPrimary(
        VulkanRenderer renderer,
        scoped ref VulkanRenderer.PrimaryCommandBufferRecordingState recordingState,
        in VulkanPrimaryOperationRecordingInfo recordingInfo)
    {
        if (VulkanRenderer.CommandRecordingDiagnosticsEnabled &&
            Target?.Name == "ForwardPassFBO")
        {
            Debug.VulkanEvery(
                "Vulkan.FwdClear",
                TimeSpan.FromSeconds(2),
                "[Vulkan][FwdClear] ForwardPassFBO clear pass={0} color={1} depth={2} stencil={3}",
                recordingInfo.PassIndex,
                ClearColor,
                ClearDepth,
                ClearStencil);
        }

        if (DeferredLightingDiagnostics.Enabled &&
            DeferredLightingDiagnostics.IsWatchedFrameBufferName(Target?.Name))
        {
            Debug.VulkanEvery(
                $"DeferredLighting.ClearOp.{Target?.Name}",
                TimeSpan.FromSeconds(1),
                "[DeferredLightingDiag][ClearOp] target='{0}' pass={1} color={2} depth={3} stencil={4} renderScope.Target='{5}'",
                Target?.Name ?? "<swapchain>",
                recordingInfo.PassIndex,
                ClearColor,
                ClearDepth,
                ClearStencil,
                recordingState.RenderScope.Target?.Name ?? "<none>");
        }

        System.Diagnostics.Debug.Assert(
            recordingInfo.BeginsRendering,
            "Clear primary-plan nodes must own render-scope entry.");
        if (recordingInfo.BeginsRendering &&
            (!recordingState.RenderScope.IsActive ||
             recordingState.RenderScope.Target != Target))
        {
            renderer.EndActiveRenderPass(ref recordingState);
            renderer.BeginRenderPassForTarget(
                ref recordingState,
                Target,
                recordingInfo.PassIndex,
                recordingState.ActiveContext);
        }

        uint renderLayerCount = recordingState.RenderScope.UsesDynamicRendering
            ? Math.Max(
                recordingState.RenderScope.DynamicRenderingFormats.LayerCount,
                1u)
            : 0u;
        uint renderViewMask = recordingState.RenderScope.UsesDynamicRendering
            ? recordingState.RenderScope.DynamicRenderingFormats.ViewMask
            : 0u;
        bool recorded = false;

        // Do not erase swapchain color composed by an earlier pipeline. Depth
        // and stencil may still be cleared independently.
        if (Target is null &&
            recordingState.SwapchainClearedThisFrame &&
            ClearColor)
        {
            if (ClearDepth || ClearStencil)
            {
                renderer.RecordClearOp(
                    recordingState.CommandBuffer,
                    recordingState.ImageIndex,
                    this,
                    recordingState.RenderScope.RenderArea,
                    in recordingState.SwapchainTarget,
                    renderLayerCount,
                    renderViewMask,
                    suppressColorClear: true);
                recorded = true;
            }
        }
        else
        {
            renderer.RecordClearOp(
                recordingState.CommandBuffer,
                recordingState.ImageIndex,
                this,
                recordingState.RenderScope.RenderArea,
                in recordingState.SwapchainTarget,
                renderLayerCount,
                renderViewMask);
            recorded = true;
        }

        if (Target is null && recorded)
            recordingState.ActualSwapchainWriteCount++;

        return recordingInfo.OperationIndex;
    }

    internal static ClearOp Rent(
        int passIndex,
        XRFrameBuffer? target,
        bool clearColor,
        bool clearDepth,
        bool clearStencil,
        ColorF4 color,
        float depth,
        uint stencil,
        Rect2D rect,
        in FrameOpContext context)
    {
        bool frameOwned = TryRentForCurrentFrame(out ClearOp? reusable);
        if (reusable is null)
        {
            ClearOp created = new(
                passIndex,
                target,
                clearColor,
                clearDepth,
                clearStencil,
                color,
                depth,
                stencil,
                rect,
                context);
            return frameOwned ? RetainForCurrentFrame(created) : created;
        }

        reusable.Reset(
            passIndex,
            target,
            clearColor,
            clearDepth,
            clearStencil,
            color,
            depth,
            stencil,
            rect,
            context);
        return reusable;
    }

    private void Reset(
        int passIndex,
        XRFrameBuffer? target,
        bool clearColor,
        bool clearDepth,
        bool clearStencil,
        ColorF4 color,
        float depth,
        uint stencil,
        Rect2D rect,
        in FrameOpContext context)
    {
        PassIndex = passIndex;
        Target = target;
        ClearColor = clearColor;
        ClearDepth = clearDepth;
        ClearStencil = clearStencil;
        Color = color;
        Depth = depth;
        Stencil = stencil;
        Rect = rect;
        Context = context;
    }
}
