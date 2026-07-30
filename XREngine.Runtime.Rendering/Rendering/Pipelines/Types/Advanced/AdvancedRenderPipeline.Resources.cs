using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    public const string ExternalOutputResourceName = "$ExternalOutput";

    /// <summary>
    /// Captures the complete immutable resource/state profile for this pipeline.
    /// </summary>
    internal AdvancedRenderResourceProfile CaptureAdvancedResourceProfile(
        in RenderPipelineResourceProfile targetProfile)
        => AdvancedRenderResourceProfile.CreateAttributeReconstruction(
            targetProfile,
            CapabilityResult.Capabilities);

    /// <summary>
    /// Captures every optional visibility/capture allocation before generation.
    /// </summary>
    internal override ulong BuildResourceFeatureMaskForGenerationKey(
        XRRenderPipelineInstance instance,
        XRViewport? viewport)
    {
        AdvancedVisibilityResourceFeature visibilityFeatures =
            AdvancedVisibilityResourceFeature.Core;
        if (VisibilityDebugView != EAdvancedVisibilityDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            visibilityFeatures |= AdvancedVisibilityResourceFeature.DebugOutput;
        }
        if (EnableVisibilityGpuValidation)
            visibilityFeatures |= AdvancedVisibilityResourceFeature.GpuValidation;

        AdvancedReconstructionResourceFeature reconstructionFeatures =
            AdvancedReconstructionResourceFeature.Core;
        if (ReconstructionDebugView !=
                EAdvancedReconstructionDebugView.Disabled ||
            viewport?.CapturePolicy.RenderDebugOverlays == true)
        {
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.DebugOutput;
        }
        bool derivativeDebugView =
            ReconstructionDebugView is
                EAdvancedReconstructionDebugView.DerivativeError or
                EAdvancedReconstructionDebugView.SelectedMip;
        if (EnableReconstructionDerivativeDiagnostics ||
            derivativeDebugView)
        {
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.DerivativeDiagnostics |
                AdvancedReconstructionResourceFeature.DebugOutput;
        }
        if (EnableReconstructionGpuValidation)
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.GpuValidation;
        if (EnableReconstructionReferenceOutput)
            reconstructionFeatures |=
                AdvancedReconstructionResourceFeature.ReferenceOutput;

        return (ulong)visibilityFeatures | (ulong)reconstructionFeatures;
    }

    /// <summary>
    /// Declares the document-04/05 visibility and reconstruction resource contract.
    /// </summary>
    protected override void DescribeResources(RenderPipelineResourceLayoutBuilder builder)
    {
        DeclareVisibilityBufferResources(builder);
        DeclareAttributeReconstructionResources(builder);

        RenderPipelineExternalTargetKind kind = builder.Profile.ExternalTargetKind;
        if (kind == RenderPipelineExternalTargetKind.None)
            return;

        ExternalRenderResourceOwnership ownership = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceOwnership.Window,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceOwnership.XrRuntime,
            _ => ExternalRenderResourceOwnership.Caller,
        };
        ExternalRenderResourceSynchronization synchronization = kind switch
        {
            RenderPipelineExternalTargetKind.Window =>
                ExternalRenderResourceSynchronization.FrameBoundary,
            RenderPipelineExternalTargetKind.ExternalSwapchain =>
                ExternalRenderResourceSynchronization.AcquireRelease,
            _ => ExternalRenderResourceSynchronization.CallerProvided,
        };

        builder.External(ExternalOutputResourceName)
            .Contract(
                ExternalRenderResourceKind.FrameBuffer,
                ownership,
                synchronization)
            .DebugLabel(kind.ToString())
            .Add();
    }
}
