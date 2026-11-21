// Tells the source generator how built games identify and load rendering assets.
// RuntimeTypeContract maps a stable file ID and schema version to a C# asset type.
// ProjectBuilder writes that ID into each mesh or texture file. PublishedCookedAssetReader
// looks it up to determine which asset type to create and which byte-reading function to call.
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.XRTexture2D), "xre.rendering.texture-2d", 1)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.XRMesh), "xre.rendering.mesh", 1)]
// BakeCacheFiles writes Gaussian cache and chunk files using these IDs.
// GaussianCacheComponent loads the cache and the chunks it references.
// Their save/load functions are registered separately by GaussianCachePublishedRegistration.
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.Models.Gaussian.Cache.GaussianCache), "xre.rendering.gaussian-cache", 1)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Rendering.Models.Gaussian.Cache.GaussianCacheChunk), "xre.rendering.gaussian-cache-chunk", 1)]

// RuntimeCookedAsset generates the save/load functions stored in PublishedCookedAssetRegistry.
// For meshes, those functions use RuntimeCookedBinarySerializer to write and read mesh data.
// Loading calls XRMesh.CreateDeferredForDeserialization to create the mesh object before
// reading its saved contents. The type ID above identifies the asset; this entry loads its data.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Rendering.XRMesh), XREngine.ERuntimeCookedAssetCodec.CookedBinaryMesh)]
// Textures use their own streaming file layout. The generated functions call
// XRTexture2D.WriteTextureStreamingPayload to write the texture data and
// TryDeserializeTextureStreamingPayload to create a texture from those bytes.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Rendering.XRTexture2D), XREngine.ERuntimeCookedAssetCodec.TextureStreaming)]

// RuntimeClosedFormatter generates array constructors used by RuntimeCookedBinarySerializer.
// XRMesh.ReadPayload still reads older mesh files with separate arrays for vertex attributes.
// Texture coordinates use Vector2[][]: an outer array of sets, each containing Vector2 values.
// ReadArray allocates both levels, so both the inner and outer array types need constructors.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Numerics.Vector2[]))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Numerics.Vector2[][]))]
// The same mesh reader loads positions, normals, and tangents into separate Vector3[] arrays.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Numerics.Vector3[]))]
// Vertex colors use Vector4[][]: an outer array of color sets and an inner array of color values.
// As with texture coordinates, ReadArray needs constructors for both levels.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Numerics.Vector4[]))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Numerics.Vector4[][]))]
