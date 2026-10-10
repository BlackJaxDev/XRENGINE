using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Shadows;

/// <summary>Identifies one desktop Advanced consumer and its frozen command package.</summary>
internal readonly record struct AdvancedDirectionalShadowConsumerAuthority(
    XRViewport? Viewport,
    XRRenderPipelineInstance? Pipeline,
    RenderCommandCollection? Commands,
    ulong RenderFrameId,
    long PackageGeneration,
    BackendReadyFramePackageIdentity PackageIdentity,
    EMeshSubmissionStrategy Strategy)
{
    public bool IsValid => Viewport is not null && Pipeline is not null && Commands is not null &&
        PackageGeneration > 0L && PackageIdentity.CollectGeneration >= 0L;

    public bool SameAs(in AdvancedDirectionalShadowConsumerAuthority other)
        => IsValid && other.IsValid && ReferenceEquals(Viewport, other.Viewport) &&
            ReferenceEquals(Pipeline, other.Pipeline) &&
            ReferenceEquals(Commands, other.Commands) &&
            RenderFrameId == other.RenderFrameId &&
            PackageGeneration == other.PackageGeneration &&
            PackageIdentity == other.PackageIdentity && Strategy == other.Strategy;

    public bool Matches(
        XRViewport viewport,
        XRRenderPipelineInstance pipeline,
        RenderCommandCollection commands,
        BackendReadyFramePackage package,
        ulong renderFrameId)
        => IsValid && ReferenceEquals(Viewport, viewport) &&
            ReferenceEquals(Pipeline, pipeline) &&
            ReferenceEquals(Commands, commands) && RenderFrameId == renderFrameId &&
            package.State == EBackendReadyFramePackageState.Published &&
            package.PackageGeneration == PackageGeneration &&
            package.Identity == PackageIdentity &&
            package.SubmissionResolution.Resolved == Strategy;
}
