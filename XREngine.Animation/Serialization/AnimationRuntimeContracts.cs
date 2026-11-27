// Tells the source generator how to identify, save, and load animation assets in a built game.
// "Cooked" assets are files that the project build has converted into a runtime format.
// The generator creates registration code; AnimationSerializationRegistration.Install runs it.

// RuntimeTypeContract connects a file's type ID and schema version to its C# asset type.
// For example, ProjectBuilder writes "contract:xre.animation.clip:2" into a clip file.
// PublishedCookedAssetReader uses that ID to determine that the file contains an AnimationClip.
// Clips hold animation tracks. Blend trees combine child motions. State machines select motions
// through states and transitions. Each can be saved and loaded as an asset, so each needs an ID.
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Animation.AnimationClip), "xre.animation.clip", 2)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Animation.BlendTree1D), "xre.animation.blend-tree-1d", 2)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Animation.BlendTree2D), "xre.animation.blend-tree-2d", 2)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Animation.BlendTreeDirect), "xre.animation.blend-tree-direct", 2)]
[assembly: XREngine.RuntimeTypeContract(typeof(XREngine.Animation.AnimStateMachine), "xre.animation.state-machine", 3)]

// RuntimeCookedAsset generates the functions that turn an asset into bytes and bytes into an asset.
// PublishedCookedAssetRegistry selects those functions after the file's type has been identified.
// For a clip, AnimationClipSerialization.CreatePublishedModel copies its tracks into a data model.
// MemoryPack writes that model to bytes. Loading reads the model, creates an AnimationClip,
// and calls ApplyPublishedModel to populate its animation members and tracks.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Animation.AnimationClip), XREngine.ERuntimeCookedAssetCodec.AnimationClipModel)]
// BlendTreeSerialization follows the same model-to-bytes path for each blend-tree type.
// On load, CreatePublishedRuntimeBlendTree creates the correct tree type and adds its children.
// MotionSerialization creates the clips or nested blend trees described by those child records.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Animation.BlendTree1D), XREngine.ERuntimeCookedAssetCodec.BlendTree1DModel)]
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Animation.BlendTree2D), XREngine.ERuntimeCookedAssetCodec.BlendTree2DModel)]
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Animation.BlendTreeDirect), XREngine.ERuntimeCookedAssetCodec.BlendTreeDirectModel)]
// AnimStateMachineSerialization copies the state-machine graph into a model for saving.
// On load, the generated reader creates an AnimStateMachine and calls ApplyPublishedModel
// to add the saved states and transitions and create the motions assigned to those states.
[assembly: XREngine.RuntimeCookedAsset(typeof(XREngine.Animation.AnimStateMachine), XREngine.ERuntimeCookedAssetCodec.AnimStateMachineModel)]
