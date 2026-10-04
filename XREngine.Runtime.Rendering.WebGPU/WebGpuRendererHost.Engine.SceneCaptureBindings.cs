using XREngine.Components.Capture.Lights.Types;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private void RequireCapturedLighting(SceneCaptureLightingSnapshot lighting)
    {
        if (lighting.IsDisposed || !ReferenceEquals(lighting.Owner, this) || lighting.Session != _session ||
            lighting.BackendGeneration != BackendGeneration)
            throw new InvalidOperationException("WebGPU.SceneCapture.LightingObsolete: the frozen lighting belongs to a retired capture owner.");
        foreach (SceneCaptureShadowSnapshot shadow in lighting.Shadows)
            if (!TryGetAPIRenderObject(shadow.Texture, out AbstractRenderAPIObject? wrapper) ||
                wrapper is null || wrapper.GetHandle() != shadow.TextureGenerationHandle || !CanReuseCommittedShadow(shadow.Texture) ||
                GetShadowProductionTicket(shadow.Texture) != shadow.ProductionTicket)
                throw new InvalidOperationException("WebGPU.SceneCapture.ShadowCopyChanged: a frozen shadow image was replaced or rewritten.");
    }

    private SceneCaptureShadowSnapshot? GetCapturedShadow(object light)
    {
        if (_activeSceneCaptureLighting is not { } lighting) return null;
        RequireCapturedLighting(lighting);
        return lighting.Find(light);
    }

    private bool TryValidateCapturedShadow(in AdvancedShadowRecord record, XRTexture texture, out string reason)
    {
        if (_activeSceneCaptureLighting is { } lighting)
        {
            RequireCapturedLighting(lighting);
            foreach (SceneCaptureShadowSnapshot shadow in lighting.Shadows)
            {
                AdvancedShadowRecord frozen = shadow.Record;
                if (ReferenceEquals(shadow.Texture, texture) && LightComponent.BrowserShadowPayloadsMatch(in frozen, in record) &&
                    frozen.LastRenderedFrameLo == record.LastRenderedFrameLo && frozen.LastRenderedFrameHi == record.LastRenderedFrameHi)
                {
                    reason = string.Empty;
                    return true;
                }
            }
        }
        reason = "WebGPU.SceneCapture.ShadowRecordMismatch: the native shadow row does not match the frozen image's accepted projection receipt.";
        return false;
    }
}
