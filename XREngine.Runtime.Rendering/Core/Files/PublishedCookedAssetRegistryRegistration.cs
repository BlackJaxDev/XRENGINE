using XREngine.Rendering;
using XREngine.Data;

namespace XREngine.Core.Files;

public static class RenderingPublishedCookedAssetRegistration
{
    // Mesh and texture cooked payloads contain their buffers/pixels rather than
    // separate XRAsset references.
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedStandardLitTextureMaterial),
                static () => new PublishedStandardLitTextureMaterial());
            CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedUiImageMaterial),
                static () => new PublishedUiImageMaterial());
            CookedBinarySerializer.RegisterRuntimeFactory(typeof(PublishedDeferredDecalMaterial),
                static () => new PublishedDeferredDecalMaterial());
            leases.Add(RuntimeCookedBinarySerializer.RegisterRuntimeFactory(
                typeof(XRMesh),
                static () => XRMesh.CreateDeferredForDeserialization()));
            leases.Add(RuntimeCookedBinarySerializer.RegisterRuntimeFactory(typeof(XRTexture2D), static () => new XRTexture2D()));
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(XRMesh),
                static asset => RuntimeCookedBinarySerializer.Serialize((XRMesh)asset),
                static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload),
                "XREngine.Runtime.Rendering",
                static _ => Array.Empty<PublishedCookedAssetDependency>()));
            leases.Add(PublishedCookedAssetRegistry.Register(
                typeof(XRTexture2D),
                static asset => RuntimeCookedBinarySerializer.Serialize((XRTexture2D)asset),
                static (payload, assetType) => RuntimeCookedBinarySerializer.Deserialize(assetType, payload),
                "XREngine.Runtime.Rendering",
                static _ => Array.Empty<PublishedCookedAssetDependency>()));
        });

    /// <summary>Installs the restricted cooked bitmap profile only for an explicit browser owner.</summary>
    public static IDisposable InstallBrowserBitmapFontCodec()
        => PublishedCookedAssetRegistry.Register(
            typeof(FontGlyphSet),
            static asset => PublishedFontGlyphSetCodec.Serialize((FontGlyphSet)asset),
            static (payload, _) => PublishedFontGlyphSetCodec.Deserialize(payload),
            "XREngine.Runtime.Rendering.BrowserBitmapFonts",
            static _ => Array.Empty<PublishedCookedAssetDependency>());
}
