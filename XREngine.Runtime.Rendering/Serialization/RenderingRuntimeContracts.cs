[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.XRTexture2D), "xre.rendering.texture-2d", 1)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.XRMesh), "xre.rendering.mesh", 1)]
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Rendering.XRMesh), XREngine.ERuntimeCookedAssetCodec.CookedBinaryMesh)]
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Rendering.XRTexture2D), XREngine.ERuntimeCookedAssetCodec.TextureStreaming)]
