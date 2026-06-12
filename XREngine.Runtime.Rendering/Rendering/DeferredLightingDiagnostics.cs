using System;

namespace XREngine.Rendering;

internal static class DeferredLightingDiagnostics
{
    public const string LogName = "vulkan-deferred-lighting-diagnostics.log";

    public static bool Enabled => RenderDiagnosticsFlags.DiagDeferredLighting;

    public static void Write(string message)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(message))
            return;

        Debug.WriteAuxiliaryLog(LogName, $"{DateTimeOffset.Now:O} {message}");
    }

    public static bool IsWatchedFrameBufferName(string? name)
        => string.Equals(name, DefaultRenderPipeline.LightingAccumFBOName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.LightCombineFBOName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.MsaaLightCombineFBOName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.ForwardPassFBOName, StringComparison.Ordinal);

    public static bool IsWatchedTextureName(string? name)
        => string.Equals(name, DefaultRenderPipeline.DiffuseTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.LightingAccumTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.MsaaLightingTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.AlbedoOpacityTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.NormalTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.RMSETextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.AmbientOcclusionIntensityTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.DepthViewTextureName, StringComparison.Ordinal)
        || string.Equals(name, DefaultRenderPipeline.BRDFTextureName, StringComparison.Ordinal);

    public static bool IsDeferredLightCombineSampler(string? name)
        => string.Equals(name, "AlbedoOpacity", StringComparison.Ordinal)
        || string.Equals(name, "Normal", StringComparison.Ordinal)
        || string.Equals(name, "RMSE", StringComparison.Ordinal)
        || string.Equals(name, "AmbientOcclusionTexture", StringComparison.Ordinal)
        || string.Equals(name, "DepthView", StringComparison.Ordinal)
        || string.Equals(name, "LightingTexture", StringComparison.Ordinal)
        || string.Equals(name, "LightingAccumTexture", StringComparison.Ordinal)
        || string.Equals(name, "LightingTextureMS", StringComparison.Ordinal)
        || string.Equals(name, "BRDF", StringComparison.Ordinal)
        || string.Equals(name, "IrradianceArray", StringComparison.Ordinal)
        || string.Equals(name, "PrefilterArray", StringComparison.Ordinal);
}
