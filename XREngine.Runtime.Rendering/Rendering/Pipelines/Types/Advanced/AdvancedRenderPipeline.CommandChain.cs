using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.Integration;
using XREngine.Rendering.Resources;
using XREngine.Data.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    AdvancedRenderPipeline IAdvancedRenderStageFamilyHost.AdvancedStageFamilyDefinition => this;

    /// <summary>
    /// Creates the Advanced pipeline for one mono OpenXR eye output. The
    /// instance owns its command execution, resource generations, output
    /// reservation, and frame lifecycle. It has no screen-space UI.
    /// </summary>
    internal static AdvancedRenderPipeline CreateOpenXrEyePipeline()
        => new(
            stereo: false,
            capabilityResult: null,
            offscreenProfile: null,
            stageFamilyProfile: EAdvancedStageFamilyExecutionProfile.OpenXrTwoPassEye);

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        // Both eyes acquire one shared world preparation before either eye
        // consumes geometry.
        if (IsOpenXrEyeProfile)
            commands.Add<VPRC_AcquireAdvancedPreparation>();
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

    private void AppendStage(
        ViewportRenderCommandContainer commands,
        in AdvancedRenderStageDescriptor descriptor)
    {
        commands.Add<VPRC_Annotation>().Label = descriptor.GpuLabel;
        commands.Add<VPRC_GPUTimerBegin>().Label = descriptor.GpuLabel;
        var stageCommand = commands.Add<VPRC_AdvancedRenderStage>();
        stageCommand.SetStage(descriptor.Stage);
        stageCommand.GlobalIlluminationPlan = GlobalIlluminationPlan;

        // The stage command retains the stable backend-facing frame-contract identity.
        // Commands which consume the native HDR/depth outputs are appended immediately
        // after their corresponding marker so they share the same ordered contract.
        switch (descriptor.Stage)
        {
            case EAdvancedRenderStage.NativeOpaqueShading:
                AppendAdvancedBackgroundCommands(commands);
                AppendAdvancedGlobalIlluminationCommands(commands);
                break;
            case EAdvancedRenderStage.LatePasses:
                AppendAdvancedLatePassCommands(commands);
                AppendAdvancedPhysicsDebugCommands(commands);
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

    /// <summary>Draws selected GPU chains against the completed scene depth.</summary>
    private void AppendAdvancedPhysicsDebugCommands(ViewportRenderCommandContainer commands)
    {
        var selected = commands.Add<VPRC_IfElse>();
        selected.Label = "AdvancedSelectedPhysicsDebug";
        selected.ConditionEvaluator = static () =>
            RuntimeEngine.Rendering.State.DebugInstanceRenderingAvailable &&
            !RuntimeEngine.Rendering.State.IsLightProbePass &&
            !RuntimeEngine.Rendering.State.IsShadowPass &&
            GPUPhysicsChainDispatcher.Instance.HasSelectedGpuDebugChains;
        var selectedCommands = new ViewportRenderCommandContainer(this);
        RenderCaptureCommandPolicy.AddConditional(
            selectedCommands, this, ERenderCapturePass.DebugOverlays, debugCommands =>
            {
                using (debugCommands.AddUsing<VPRC_PushViewportRenderArea>(x => x.UseInternalResolution = true))
                using (debugCommands.AddUsing<VPRC_BindFBOByName>(x => x.SetOptions(
                    ForwardPassFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
                {
                    debugCommands.Add<VPRC_ColorMask>().Set(true, true, true, true);
                    debugCommands.Add<VPRC_DepthTest>().Enable = true;
                    debugCommands.Add<VPRC_DepthWrite>().Allow = true;
                    debugCommands.Add<VPRC_DepthFunc>().Comp = EComparison.Lequal;
                    var physics = debugCommands.Add<VPRC_RenderDebugPhysics>();
                    physics.RenderGraphPassName = "Advanced.PhysicsWorldDebug";
                    physics.DepthMode = PhysicsDebugDepthMode.DepthTested;
                    physics.RenderWorldPhysics = false;
                }
            });
        selected.TrueCommands = selectedCommands;
    }

    /// <summary>Runs the selected shared GI module against Advanced native-shading outputs.</summary>
    private void AppendAdvancedGlobalIlluminationCommands(ViewportRenderCommandContainer commands)
    {
        GlobalIlluminationProviderRegistry.ContributePasses(commands,
            new(new AdvancedGlobalIlluminationHostAdapter(UsesMinimalVisibilityOutput), GlobalIlluminationPlan,
                EGlobalIlluminationExecutionAnchor.SurfaceResolve,
                new(DepthViewTextureName, NormalTextureName, AlbedoOpacityTextureName,
                    RMSETextureName, AdvancedAmbientOcclusionContract.ResourceName, ForwardPassFBOName)));
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
