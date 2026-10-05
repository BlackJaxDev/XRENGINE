using System.Buffers;
using XREngine.Rendering;

namespace XREngine.Core.Files;

/// <summary>Installs browser rendering factories and its explicit bitmap font codec.</summary>
public static class RenderingPublishedCookedAssetRegistration
{
    internal static void RegisterBrowserRuntimeFactories()
    {
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedStandardLitTextureMaterial),
            static () => new PublishedStandardLitTextureMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedTexturedAlphaMaterial),
            static () => new PublishedTexturedAlphaMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedUnlitMaterial),
            static () => new PublishedUnlitMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(UnlitPublishedTextureProfile),
            static () => new UnlitPublishedTextureProfile());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(UnlitPublishedImageEntry),
            static () => new UnlitPublishedImageEntry());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(UnlitPublishedMipEntry),
            static () => new UnlitPublishedMipEntry());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedAuthoredTexturedMaterial),
            static () => new PublishedAuthoredTexturedMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(XREngine.Components.Capture.Lights.PublishedRetainedLightProbeComponent),
            static () => new XREngine.Components.Capture.Lights.PublishedRetainedLightProbeComponent());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedUberBaseMaterial),
            static () => new PublishedUberBaseMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedUiImageMaterial),
            static () => new PublishedUiImageMaterial());
        CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedDeferredDecalMaterial),
            static () => new PublishedDeferredDecalMaterial());
    }

    /// <summary>Installs the restricted cooked bitmap profile only for an explicit browser owner.</summary>
    public static IDisposable InstallBrowserBitmapFontCodec()
        => PublishedCookedAssetRegistry.Register(
            typeof(FontGlyphSet),
            static (asset, writer) => writer.Write(PublishedFontGlyphSetCodec.Serialize((FontGlyphSet)asset)),
            static (payload, _) => PublishedFontGlyphSetCodec.Deserialize(payload.ToArray()),
            "XREngine.Runtime.Rendering.BrowserBitmapFonts",
            static _ => Array.Empty<PublishedCookedAssetDependency>());
}
