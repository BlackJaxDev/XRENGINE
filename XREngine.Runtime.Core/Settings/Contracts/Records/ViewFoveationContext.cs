using System.Numerics;

namespace XREngine;

public readonly record struct ViewFoveationContext(
    EVrFoveationMode RequestedMode,
    EVrFoveationMode EffectiveMode,
    EVrFoveationQualityPreset QualityPreset,
    EVrFoveationCapabilityPath CapabilityPath,
    EVrFoveationGazeSource GazeSource,
    Vector2 ViewSpaceCenter,
    Vector2 ProjectionSpaceCenter,
    Vector2 RenderTargetUvCenter,
    VrFoveationRegionDefinition Regions,
    ulong BackendResourceKey,
    ViewFoveationAttachmentContext Attachment,
    string? FallbackReason)
{
    public bool IsEnabled => EffectiveMode != EVrFoveationMode.Off && CapabilityPath != EVrFoveationCapabilityPath.None;

    public static ViewFoveationContext Off(EVrFoveationQualityPreset qualityPreset = EVrFoveationQualityPreset.Balanced)
        => new(
            EVrFoveationMode.Off,
            EVrFoveationMode.Off,
            qualityPreset,
            EVrFoveationCapabilityPath.None,
            EVrFoveationGazeSource.None,
            Vector2.Zero,
            Vector2.Zero,
            new Vector2(0.5f, 0.5f),
            VrFoveationRegionDefinition.FromPreset(qualityPreset),
            0UL,
            ViewFoveationAttachmentContext.None,
            null);

    public static ViewFoveationContext FromResolution(
        in VrFoveationResolution resolution,
        Vector2 viewSpaceCenter,
        Vector2 projectionSpaceCenter,
        Vector2 renderTargetUvCenter,
        EVrFoveationGazeSource gazeSource,
        ulong backendResourceKey = 0UL)
        => new(
            resolution.RequestedMode,
            resolution.EffectiveMode,
            resolution.QualityPreset,
            resolution.CapabilityPath,
            gazeSource,
            viewSpaceCenter,
            projectionSpaceCenter,
            renderTargetUvCenter,
            VrFoveationRegionDefinition.FromPreset(resolution.QualityPreset),
            backendResourceKey,
            ViewFoveationAttachmentContext.FromCapability(resolution.CapabilityPath, backendResourceKey),
            resolution.Diagnostic);
}
