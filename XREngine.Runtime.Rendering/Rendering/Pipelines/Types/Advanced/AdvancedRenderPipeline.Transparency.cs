using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    private EAdvancedLatePassDebugView _latePassDebugView;

    /// <summary>
    /// Diagnostic visualization mode for late transparency, special effects, and post chain.
    /// </summary>
    public EAdvancedLatePassDebugView LatePassDebugView
    {
        get => _latePassDebugView;
        set
        {
            if (!SetField(ref _latePassDebugView, value))
                return;
            InvalidateLatePassResourceProfile();
        }
    }

    private void InvalidateLatePassResourceProfile()
        => InvalidateOwnedInstancePhysicalResources("LatePassProfileChanged");

    private void DeclareTransparencyAndLatePassResources(RenderPipelineResourceLayoutBuilder builder)
    {
        RenderResourceSizePolicy internalSize = RenderResourceSizePolicy.Internal();
        uint layers = Math.Max(builder.Profile.ViewCount, builder.Profile.Stereo ? 2u : 1u);

        // 1. Dedicated Scene Color Snapshot (allocated on demand for refractive passes)
        ReconstructionTexture(
                builder,
                AdvancedSceneColorContract.SceneColorSnapshotResourceName,
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.Float,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(HDRSceneTextureName)
            .DebugLabel("Advanced scene color snapshot")
            .Add();

        // 2. Reactive Mask (temporal TSR/TAA/upscaler disocclusion & transparency guidance)
        ReconstructionTexture(
                builder,
                AdvancedTemporalHistoryContract.ReactiveMaskResourceName,
                internalSize,
                EPixelInternalFormat.R8,
                EPixelFormat.Red,
                EPixelType.UnsignedByte,
                ESizedInternalFormat.R8)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced reactive temporal mask")
            .Add();

        // 3. Optional Late Pass Debug Output
        ReconstructionTexture(
                builder,
                "AdvancedTransparency.DebugOutput",
                internalSize,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.Float,
                ESizedInternalFormat.Rgba16f)
            .Layers(layers)
            .StereoCompatible(layers > 1u)
            .When(static profile => ((profile.FeatureMask >> 48) & 1u) != 0)
            .DependsOn(
                AdvancedVisibilityResourceNames.Identity,
                AdvancedVisibilityResourceNames.Metadata)
            .DebugLabel("Advanced late-pass debug visualization")
            .Add();
    }
}
