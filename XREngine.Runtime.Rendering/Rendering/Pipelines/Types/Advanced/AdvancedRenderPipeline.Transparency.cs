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

        // 1. Canonical view of the copy consumed by both explicit refractive
        // materials and the existing weighted/exact transparency resolves.
        builder.TextureView(
                AdvancedSceneColorContract.SceneColorSnapshotResourceName,
                TransparentSceneCopyTextureName)
            .Size(internalSize)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Usage(RenderPipelineResourceUsage.SampledTexture)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .LayerRange(0u, layers)
            .Target(array: layers > 1u, multisample: false)
            .Factory(CreateAdvancedSceneColorSnapshotView)
            .DebugLabel("Advanced scene color snapshot")
            .Add();

        builder.FrameBuffer(VelocityFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .DependsOn(VelocityTextureName, AdvancedVisibilityResourceNames.DepthStencil)
            .Color(0, VelocityTextureName)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(CreateVelocityFBO).Add();

        builder.FrameBuffer(TransparentMotionReactiveMaskFBOName).Size(internalSize).Lifetime(RenderResourceLifetime.Transient)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.DepthStencilAttachment)
            .DependsOn(
                AdvancedTemporalHistoryContract.ReactiveMaskResourceName,
                AdvancedVisibilityResourceNames.DepthStencil)
            .Color(0, AdvancedTemporalHistoryContract.ReactiveMaskResourceName)
            .DepthStencil(AdvancedVisibilityResourceNames.DepthStencil)
            .Factory(CreateTransparentMotionReactiveMaskFBO).Add();

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

    private XRTexture CreateAdvancedSceneColorSnapshotView()
    {
        const string name = AdvancedSceneColorContract.SceneColorSnapshotResourceName;
        XRTexture source = GetTexture<XRTexture>(TransparentSceneCopyTextureName)
            ?? throw new InvalidOperationException(
                $"Required scene-color source '{TransparentSceneCopyTextureName}' was not realized.");
        if (source is XRTexture2DArray array)
        {
            return new XRTexture2DArrayView(
                array,
                0u,
                1u,
                0u,
                array.Depth,
                ESizedInternalFormat.Rgba16f,
                true,
                false)
            {
                Name = name,
                SamplerName = name,
            };
        }

        if (source is not XRTexture2D texture)
            throw new InvalidOperationException(
                $"Scene-color source '{TransparentSceneCopyTextureName}' has unsupported type '{source.GetType().FullName}'.");

        return new XRTexture2DView(
            texture,
            0u,
            1u,
            ESizedInternalFormat.Rgba16f,
            false,
            false)
        {
            Name = name,
            SamplerName = name,
        };
    }
}
