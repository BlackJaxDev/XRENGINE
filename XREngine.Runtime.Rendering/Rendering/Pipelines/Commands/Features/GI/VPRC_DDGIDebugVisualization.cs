using System;
using System.Runtime.CompilerServices;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Draws DDGI probes from their GPU state buffer without CPU readback.</summary>
[RenderPipelineScriptCommand]
public sealed class VPRC_DDGIDebugVisualization : ViewportRenderCommand
{
    private const uint ComputeGroupSize = 64u;
    private const string AllocationScopeName = "DDGI.VPRC_DDGIDebugVisualization";

    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, DDGIProbeDebugResources> s_resources = new();

    public bool Enabled { get; set; }
    public EDDGIDebugMode DebugMode { get; set; } = EDDGIDebugMode.None;
    public string ReadyVariableName { get; set; } = "DDGIGeometryReady";
    public string NodeCountVariableName { get; set; } = "DDGIGeometryNodeCount";
    public string ProbeStateBufferName { get; set; } = DDGIResourceNames.ProbeStateBuffer;
    public string ForwardFBOName { get; set; } = string.Empty;
    public string? RenderGraphPassName { get; set; }

    /// <summary>Billboard radius relative to camera distance.</summary>
    public float ProbeSize { get; set; } = 0.0125f;

    protected override bool ShouldExecuteThisFrame()
        => GlobalIlluminationPlanSelection.IsSelectedAndSupported(
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline,
            EGlobalIlluminationMode.DDGI);

    protected override void Execute()
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass ||
            !GlobalIlluminationPlanSelection.IsSelectedAndSupported(ActivePipelineInstance.Pipeline, EGlobalIlluminationMode.DDGI))
            return;

        var world = ActivePipelineInstance.RenderState.WindowViewport?.World ?? RuntimeEngine.Rendering.State.RenderingWorld;
        DDGIFrameContext frameContext = DDGIFrameContext.Get(ActivePipelineInstance);
        if (world is null || !frameContext.TryGetSelectedVolume(world, out DDGIVolumeComponent? volume) || volume is null)
            return;

        if (!Enabled && !volume.DebugDrawProbes)
            return;

#if !XRE_PUBLISHED
        using var allocationScope = RuntimeRenderingHostServices.Profiling.EnableThreadAllocationTracking
            ? DDGIManagedAllocationDiagnostics.Begin(AllocationScopeName)
            : default;
#endif

        XRRenderPipelineInstance instance = ActivePipelineInstance;
        if (!RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable)
            return;

        if (!frameContext.BindResources(instance) || !frameContext.HasInitializedResources)
            return;
        DDGIVolumeRuntimeState state = frameContext.State;
        uint probeCount = GetProbeCount(state);
        XRDataBuffer? probeBuffer = instance.GetBuffer(ProbeStateBufferName);
        if (probeCount == 0 || probeBuffer is null || probeBuffer.IsDestroyed || probeBuffer.ElementCount < probeCount)
            return;

        PublishLightweightVariables(instance, state, probeCount);
        DDGIProbeDebugResources resources = s_resources.GetValue(instance, static pipeline => new DDGIProbeDebugResources(pipeline));
        if (!resources.EnsureResources(probeCount) || !resources.TryPrepareForRendering())
            return;

        if (!TryResolveRenderGraphPassIndex(ComputePassName, out int computePassIndex) ||
            !TryResolveRenderGraphPassIndex(DrawPassName, out int drawPassIndex))
        {
            Debug.RenderingWarningEvery(
                "DDGI.DebugVisualization.MissingPassMetadata",
                TimeSpan.FromSeconds(2),
                "DDGI probe debug visualization is waiting for render-graph pass metadata.");
            return;
        }

        using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(computePassIndex))
            resources.Dispatch(probeBuffer, state, probeCount, ComputeGroupSize);

        resources.SetProbeSize(Math.Max(ProbeSize, 0.0001f));
        var camera = instance.RenderState.SceneCamera
            ?? instance.RenderState.RenderingCamera
            ?? instance.LastSceneCamera
            ?? instance.LastRenderingCamera;
        using (RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(drawPassIndex))
        using (instance.RenderState.PushRenderingCamera(camera))
            resources.Render(probeCount);
    }

    private void PublishLightweightVariables(XRRenderPipelineInstance instance, DDGIVolumeRuntimeState state, uint probeCount)
    {
        var variables = instance.Variables;
        bool geometryReady = variables.TryGet(ReadyVariableName, out bool ready) && ready;
        uint geometryNodeCount = variables.TryGet(NodeCountVariableName, out uint count) ? count : 0u;
        variables.Set("DDGIGeometryReady", geometryReady);
        variables.Set("DDGIGeometryNodeCount", geometryNodeCount);
        variables.Set("DDGIProbeDebugEnabled", true);
        variables.Set("DDGIProbeDebugProbeCount", probeCount);
        variables.Set("DDGIProbeDebugCascadeCount", (uint)state.CascadeCount);
    }

    private static uint GetProbeCount(DDGIVolumeRuntimeState state)
    {
        ulong total = 0;
        for (int i = 0; i < state.Cascades.Count; i++)
        {
            var cascade = state.Cascades[i];
            total += (ulong)Math.Max(cascade.ProbeCounts.X, 0) * (uint)Math.Max(cascade.ProbeCounts.Y, 0) * (uint)Math.Max(cascade.ProbeCounts.Z, 0);
        }
        return total > uint.MaxValue ? uint.MaxValue : (uint)total;
    }

    private const string ComputePassName = nameof(VPRC_DDGIDebugVisualization) + "_Compute";
    private string DrawPassName => RenderGraphPassName ?? $"{nameof(VPRC_DDGIDebugVisualization)}_Draw";

    private bool TryResolveRenderGraphPassIndex(string passName, out int passIndex)
    {
        if (ParentPipeline?.PassMetadata is { } metadata)
            foreach (RenderPassMetadata pass in metadata)
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                {
                    passIndex = pass.PassIndex;
                    return true;
                }
        passIndex = int.MinValue;
        return false;
    }

    internal override void DescribeRenderPass(RenderGraphDescribeContext context)
    {
        base.DescribeRenderPass(context);
        var compute = context.GetOrCreateSyntheticPass(ComputePassName, ERenderGraphPassStage.Compute);
        compute.ReadBuffer(ProbeStateBufferName);
        string destination = context.CurrentRenderTarget?.Name ?? ForwardFBOName ?? RenderGraphResourceNames.OutputRenderTarget;
        context.GetOrCreateSyntheticPass(DrawPassName, ERenderGraphPassStage.Graphics)
            .KeepSecondaryDynamic(ERenderPassSecondaryCachePolicy.DynamicDebug)
            .UseEngineDescriptors()
            .UseMaterialDescriptors()
            .DependsOn(compute.PassIndex)
            .UseColorAttachment(MakeFboColorResource(destination), ERenderGraphAccess.ReadWrite, ERenderPassLoadOp.Load, ERenderPassStoreOp.Store);
    }
}
