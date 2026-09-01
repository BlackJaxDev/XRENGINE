using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private EAdvancedReconstructionDebugView _reconstructionDebugView;
    private bool _enableReconstructionDerivativeDiagnostics;
    private bool _enableReconstructionGpuValidation;
    private bool _enableReconstructionReferenceOutput;

    /// <summary>
    /// Selects a diagnostic view of the shader-local reconstructed surface.
    /// </summary>
    public EAdvancedReconstructionDebugView ReconstructionDebugView
    {
        get => _reconstructionDebugView;
        set
        {
            if (!SetField(ref _reconstructionDebugView, value))
                return;
            InvalidateReconstructionResourceProfile();
        }
    }

    /// <summary>
    /// Enables derivative-error and selected-mip diagnostic images.
    /// </summary>
    public bool EnableReconstructionDerivativeDiagnostics
    {
        get => _enableReconstructionDerivativeDiagnostics;
        set
        {
            if (!SetField(
                    ref _enableReconstructionDerivativeDiagnostics,
                    value))
            {
                return;
            }
            InvalidateReconstructionResourceProfile();
        }
    }

    /// <summary>
    /// Enables generation, bounds, and non-finite checks in validation kernels.
    /// </summary>
    public bool EnableReconstructionGpuValidation
    {
        get => _enableReconstructionGpuValidation;
        set
        {
            if (!SetField(ref _enableReconstructionGpuValidation, value))
                return;
            InvalidateReconstructionResourceProfile();
        }
    }

    /// <summary>
    /// Enables the non-production full-screen reference image used for comparisons.
    /// </summary>
    public bool EnableReconstructionReferenceOutput
    {
        get => _enableReconstructionReferenceOutput;
        set
        {
            if (!SetField(ref _enableReconstructionReferenceOutput, value))
                return;
            InvalidateReconstructionResourceProfile();
        }
    }

    private void InvalidateReconstructionResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("AttributeReconstructionProfileChanged");

    private void DeclareAttributeReconstructionResources(
        RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize =
            RenderResourceSizePolicy.Internal();
        uint layers = Math.Max(
            builder.Profile.ViewCount,
            builder.Profile.Stereo ? 2u : 1u);

        for (uint slot = 0u;
             slot < AdvancedFrameSlotContract.DefaultSlotCount;
             slot++)
        {
            VisibilityBuffer<AdvancedReconstructionGpuCounters>(
                    builder,
                    AdvancedReconstructionResourceNames.Counters(slot),
                    VisibilityViewCapacity,
                    EBufferTarget.ShaderStorageBuffer,
                    EBufferUsage.DynamicRead)
                .Lifetime(RenderResourceLifetime.Transient)
                .DebugLabel(
                    $"Advanced reconstruction counters slot {slot}")
                .Add();
        }

        ReconstructionTexture(
                builder,
                AdvancedReconstructionResourceNames.DebugOutput,
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile =>
                HasReconstructionFeature(
                    profile,
                    AdvancedReconstructionResourceFeature.DebugOutput))
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel("Advanced reconstructed-surface debug output")
            .Add();

        ReconstructionTexture(
                builder,
                AdvancedReconstructionResourceNames.DerivativeError,
                internalSize,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                ESizedInternalFormat.R16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile =>
                HasReconstructionFeature(
                    profile,
                    AdvancedReconstructionResourceFeature.DerivativeDiagnostics))
            .DependsOn(AdvancedVisibilityResourceNames.Identity)
            .DebugLabel("Advanced analytical derivative error")
            .Add();

        ReconstructionTexture(
                builder,
                AdvancedReconstructionResourceNames.SelectedMip,
                internalSize,
                EPixelInternalFormat.R16f,
                EPixelFormat.Red,
                EPixelType.HalfFloat,
                ESizedInternalFormat.R16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile =>
                HasReconstructionFeature(
                    profile,
                    AdvancedReconstructionResourceFeature.DerivativeDiagnostics))
            .DependsOn(AdvancedReconstructionResourceNames.DerivativeError)
            .DebugLabel("Advanced selected texture mip")
            .Add();

        ReconstructionTexture(
                builder,
                AdvancedReconstructionResourceNames.ReferenceOutput,
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile =>
                HasReconstructionFeature(
                    profile,
                    AdvancedReconstructionResourceFeature.ReferenceOutput))
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata,
                AdvancedVisibilityResourceNames.DepthStencil)
            .DebugLabel(
                "Advanced non-production full-screen reconstruction reference")
            .Add();
    }

    private RenderPipelineResourceLayoutBuilder.TextureSpecBuilder
        ReconstructionTexture(
            RenderPipelineResourceLayoutBuilder builder,
            string name,
            RenderResourceSizePolicy size,
            EPixelInternalFormat internalFormat,
            EPixelFormat pixelFormat,
            EPixelType pixelType,
            ESizedInternalFormat sizedInternalFormat)
        => VisibilityTexture(
                builder,
                name,
                size,
                RenderPipelineResourceUsage.SampledTexture |
                RenderPipelineResourceUsage.StorageImage |
                RenderPipelineResourceUsage.TransferSource,
                internalFormat,
                pixelFormat,
                pixelType,
                sizedInternalFormat,
                attachment: null,
                storage: true)
            .Lifetime(RenderResourceLifetime.Transient);

    private static bool HasReconstructionFeature(
        RenderPipelineResourceProfile profile,
        AdvancedReconstructionResourceFeature feature)
        => ((AdvancedReconstructionResourceFeature)profile.FeatureMask &
            feature) != 0;
}
