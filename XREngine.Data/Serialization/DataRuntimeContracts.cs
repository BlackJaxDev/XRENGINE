// Tells the source generator which settings assets and collection types need runtime loading code.
// DataPublishedCookedAssetRegistration.Install adds the generated entries to the runtime registries.

// RuntimeTypeContract gives each settings type a stable file ID and a schema version.
// CookedAssetTypeReference writes these into the file header. PublishedCookedAssetReader looks
// them up to choose the C# type before reading the saved settings values.
// Engine.LoadProjectUserSettings loads user preferences; ProjectBuilder packages them for the game.
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.UserSettings), "xre.settings.user", 5)]
// Engine.LoadProjectBuildSettings loads the project's build settings asset.
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.BuildSettings), "xre.settings.build", 2)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Data.AudioProxyAsset), "xre.audio.proxy", 1)]

// RuntimeCookedAsset generates the save/load functions stored in PublishedCookedAssetRegistry.
// For these two types, MemoryPack writes the settings object to bytes and reads the bytes back
// into a settings object. The type ID above selects the type; these functions read its values.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.UserSettings), XREngine.ERuntimeCookedAssetCodec.MemoryPack)]
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.BuildSettings), XREngine.ERuntimeCookedAssetCodec.MemoryPack)]

// RuntimeClosedFormatter generates constructors for exact collection types, such as List<string>.
// CookedBinarySerializer uses those constructors before adding the elements read from a file.
// This avoids constructing a generic type through reflection in an ahead-of-time compiled game.
// RenderPipelineRequirementsDeclaration stores operation names and program IDs in these lists.
// Scene snapshots also store AvatarPresentationComponent.VisemeBlendShapeNames as a string list.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Collections.Generic.List<string>))]
// RenderPipelineRequirementsDeclaration.Programs maps pass names to shader program IDs.
// Its saved entries need a Dictionary<string, string> instance when the pipeline is loaded.
// The property's nullable string annotation does not change that C# runtime type.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Collections.Generic.Dictionary<string, string>))]
// XRMesh.ReadPayload loads older mesh files whose triangle, line, and point indices are int arrays.
// This generates an array constructor for RuntimeCookedBinarySerializer.CreateRuntimeArray:
// allocate new int[length], then fill it with the indices read from the file.
[assembly: XREngine.RuntimeClosedFormatter(typeof(int[]))]
