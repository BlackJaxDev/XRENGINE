using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    /// <summary>
    /// Admits a frozen standalone shadow candidate only after its preceding
    /// producer has recorded matching inputs, or its exact committed image was
    /// authorized for this frame's cadence reuse. The owner supplies validation,
    /// never replacement numeric data for the immutable canonical publication.
    /// </summary>
    internal bool TryValidateBrowserStandaloneShadow(in AdvancedShadowRecord record, XRTexture texture,
        out string reason)
    {
        if (_activeSceneCaptureLighting is not null)
            return TryValidateCapturedShadow(in record, texture, out reason);
        IRuntimeRenderWorld? world = _activeSceneCaptureWorld ?? _engineViewport?.World;
        ulong outputGeneration = _activeSceneCaptureWorld is not null
            ? _sceneCaptureLightingOutputGeneration : CurrentFrameOutput?.TargetGeneration ?? 0;
        if (_engineRecording && world is not null && outputGeneration != 0)
        {
            for (int index = 0; index < world.Lights.DynamicDirectionalLights.Count; index++)
                if (MatchesBrowserStandaloneShadow(world.Lights.DynamicDirectionalLights[index], in record, texture, outputGeneration))
                {
                    reason = string.Empty;
                    return true;
                }
            for (int index = 0; index < world.Lights.DynamicPointLights.Count; index++)
                if (MatchesBrowserStandaloneShadow(world.Lights.DynamicPointLights[index], in record, texture, outputGeneration))
                {
                    reason = string.Empty;
                    return true;
                }
            for (int index = 0; index < world.Lights.DynamicSpotLights.Count; index++)
                if (MatchesBrowserStandaloneShadow(world.Lights.DynamicSpotLights[index], in record, texture, outputGeneration))
                {
                    reason = string.Empty;
                    return true;
                }
        }

        reason = "WebGPU.Advanced.ShadowPending: the frozen standalone shadow candidate has no matching current producer or authorized committed reuse receipt.";
        return false;
    }

    private bool MatchesBrowserStandaloneShadow(LightComponent light, in AdvancedShadowRecord record,
        XRTexture texture, ulong outputGeneration)
        => light.MatchesBrowserShadowPublication(in record, texture, outputGeneration, this) &&
           (WasShadowProducedInCurrentFrame(texture) || CanPublishReusedShadow(light, texture));
}
