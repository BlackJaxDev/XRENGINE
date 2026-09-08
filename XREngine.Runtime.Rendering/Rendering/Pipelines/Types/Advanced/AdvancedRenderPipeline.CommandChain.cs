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
        commands.Add<VPRC_AdvancedRenderStage>().SetStage(descriptor.Stage);

        // The stage command retains the stable backend-facing frame-contract identity.
        // Commands which consume the native HDR/depth outputs are appended immediately
        // after their corresponding marker so they share the same ordered contract.
        switch (descriptor.Stage)
        {
            case EAdvancedRenderStage.NativeOpaqueShading:
                AppendAdvancedBackgroundCommands(commands);
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

    private void AppendAdvancedScreenSpaceUi(ViewportRenderCommandContainer commands)
    {
        var ui = commands.Add<VPRC_IfElse>();
        ui.Label = "AdvancedScreenSpaceUiAllowed";
        ui.ConditionEvaluator = () => AllowsScreenSpaceUi;
        var uiCommands = new ViewportRenderCommandContainer(this);
        uiCommands.Add<VPRC_RenderScreenSpaceUI>();
        ui.TrueCommands = uiCommands;
    }
}
