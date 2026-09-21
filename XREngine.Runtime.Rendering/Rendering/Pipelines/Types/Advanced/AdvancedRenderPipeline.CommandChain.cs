using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    AdvancedRenderPipeline IAdvancedRenderStageFamilyHost.AdvancedStageFamilyDefinition => this;

    /// <summary>
    /// Returns this full Advanced frame family with every command rebound to
    /// <paramref name="executionOwner"/>. This deliberately does not render
    /// through another pipeline instance: all resource lookup, reservations,
    /// and output authoring remain owned by that active instance.
    /// </summary>
    internal ViewportRenderCommandContainer GetAdvancedStageFamilyCommandChain(
        RenderPipeline executionOwner)
    {
        ArgumentNullException.ThrowIfNull(executionOwner);
        ViewportRenderCommandContainer commands = CommandChain;
        commands.ParentPipeline = executionOwner;
        return commands;
    }

    /// <summary>
    /// Creates a definition-only Advanced family for an outer two-pass OpenXR
    /// eye pipeline. The outer RVC pipeline remains the sole owner of command
    /// execution, resource generations, output reservations, and frame
    /// lifecycle.
    /// </summary>
    internal static AdvancedRenderPipeline CreateOpenXrTwoPassEyeStageFamily()
    {
        var family = new AdvancedRenderPipeline(
            stereo: false,
            capabilityResult: null,
            offscreenProfile: null,
            stageFamilyProfile: EAdvancedStageFamilyExecutionProfile.OpenXrTwoPassEye,
            subscribeToRuntimeSettings: false);
        // The definition is cached across RVC command-chain rebuilds. Add its
        // preparation acquire once here instead of mutating the shared root on
        // every owning-pipeline rebuild.
        family.CommandChain.Insert(0, new VPRC_AcquireAdvancedPreparation());
        return family;
    }

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        if (!UsesMinimalVisibilityOutput)
            commands.Add<VPRC_PrecomputeBRDF>();
        IReadOnlyList<AdvancedRenderStageDescriptor> stages =
            AdvancedRenderPipelineFrameContract.OrderedStages;

        // Jitter must be active while the native visibility/opaque stages render
        // their depth and velocity inputs. Accumulation and PopJitter remain in
        // the late post stage, after those inputs have been produced.
        AppendAdvancedTemporalBegin(commands);

        for (int i = 0; i < stages.Count; i++)
        {
            if (IncludesStage(stages[i].Stage))
                AppendStage(commands, stages[i]);
        }

        return commands;
    }

    /// <summary>
    /// Declares the complete resource family consumed by the Advanced command
    /// chain. A composed owner uses these declarations without creating a
    /// nested render-pipeline instance.
    /// </summary>
    internal void DescribeAdvancedStageFamilyResources(
        RenderPipelineResourceLayoutBuilder builder)
        => DescribeResources(builder);

    private void AppendStage(
        ViewportRenderCommandContainer commands,
        in AdvancedRenderStageDescriptor descriptor)
    {
        commands.Add<VPRC_Annotation>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_GPUTimerBegin>().Label = descriptor.GpuLabel;
        var stageCommand = commands.Add<VPRC_AdvancedRenderStage>();
        stageCommand.SetStage(descriptor.Stage);
        stageCommand.EnableDdgi = UsesDDGI && !UsesMinimalVisibilityOutput;

        // The stage command retains the stable backend-facing frame-contract identity.
        // Commands which consume the native HDR/depth outputs are appended immediately
        // after their corresponding marker so they share the same ordered contract.
        switch (descriptor.Stage)
        {
            case EAdvancedRenderStage.NativeOpaqueShading:
                AppendAdvancedBackgroundCommands(commands);
                AppendAdvancedDdgiCommands(commands);
                break;
            case EAdvancedRenderStage.LatePasses:
                AppendAdvancedLatePassCommands(commands);
                break;
            case EAdvancedRenderStage.TemporalAndPostProcessing:
                AppendAdvancedPostProcessCommands(commands);
                break;
            case EAdvancedRenderStage.Output:
                AppendAdvancedOutputCommands(commands);
                break;
            case EAdvancedRenderStage.UserInterface:
                AppendAdvancedScreenSpaceUi(commands);
                break;
        }
        commands.Add<VPRC_GPUTimerEnd>().Label = descriptor.GpuLabel;
    }

    /// <summary>Runs the shared DDGI lifecycle against Advanced native-shading outputs.</summary>
    private void AppendAdvancedDdgiCommands(ViewportRenderCommandContainer commands)
    {
        commands.Add<VPRC_BuildAccelerationStructure>();
        commands.Add<VPRC_DDGIEnvironmentPass>();
        commands.Add<VPRC_DDGIPrepareGeometryPass>();
        commands.Add<VPRC_DDGIRaygenPass>();
        commands.Add<VPRC_DDGITracePass>();
        commands.Add<VPRC_DDGIHitShadePass>();
        commands.Add<VPRC_DDGIRelocatePass>();
        commands.Add<VPRC_DDGIUpdateIrradiancePass>();
        commands.Add<VPRC_DDGIUpdateVisibilityPass>();
        commands.Add<VPRC_DDGIBorderCopyPass>();

        var composite = commands.Add<VPRC_DDGICompositePass>();
        composite.DepthTextureName = DepthViewTextureName;
        composite.NormalTextureName = NormalTextureName;
        composite.AlbedoTextureName = AlbedoOpacityTextureName;
        composite.RMSETextureName = RMSETextureName;
        composite.OutputTextureName = DDGITextureName;
        composite.CompositeQuadFBOName = DDGICompositeFBOName;
        composite.ForwardFBOName = ForwardPassFBOName;
        composite.IrradianceAtlasTextureName = DDGIIrradianceAtlasTextureName;
        composite.VisibilityAtlasTextureName = DDGIVisibilityAtlasTextureName;
        composite.ProbeStateBufferName = DDGIProbeStateBufferName;
        composite.RayBufferName = DDGIRayBufferName;
        composite.HitBufferName = DDGIHitBufferName;
        composite.AmbientOcclusionTextureName = AdvancedAmbientOcclusionContract.ResourceName;

        var debug = commands.Add<VPRC_DDGIDebugVisualization>();
        debug.ProbeStateBufferName = DDGIProbeStateBufferName;
        debug.ForwardFBOName = ForwardPassFBOName;
    }

    private void AppendAdvancedScreenSpaceUi(ViewportRenderCommandContainer commands)
    {
        commands.Add<VPRC_RenderMeshesPass>().SetOptions((int)EDefaultRenderPass.PostRender, false);
        var ui = commands.Add<VPRC_IfElse>();
        ui.Label = "AdvancedScreenSpaceUiAllowed";
        ui.ConditionEvaluator = () => AllowsScreenSpaceUi;
        var uiCommands = new ViewportRenderCommandContainer(this);
        uiCommands.Add<VPRC_RenderScreenSpaceUI>();
        ui.TrueCommands = uiCommands;
    }
}
