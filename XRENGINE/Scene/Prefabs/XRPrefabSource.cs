using MemoryPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Assimp;
using XREngine.Data;
using XREngine.Core.Files;
using XREngine.Fbx;
using XREngine.ModelCaching;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Serialized asset that owns a standalone hierarchy of scene nodes which can be instantiated into any world.
    /// </summary>
    [Serializable]
    [XR3rdPartyExtensions(typeof(ModelImportOptions),
        "3d",
        "3ds",
        "3mf",
        "ac",
        "acc",
        "amj",
        "ase",
        "ask",
        "b3d",
        "bvh",
        "csm",
        "cob",
        "dae",
        "dxf",
        "enff",
        "fbx",
        "gltf",
        "glb",
        "hmb",
        "ifc",
        "iqm",
        "irr",
        "irrmesh",
        "lwo",
        "lws",
        "lxo",
        "m3d",
        "md2",
        "md3",
        "md5anim",
        "md5camera",
        "md5mesh",
        "mdc",
        "mdl",
        "mesh.xml",
        "mot",
        "ms3d",
        "ndo",
        "nff",
        "obj",
        "off",
        "ogex",
        "ply",
        "prefab",
        "pmx",
        "prj",
        "q3o",
        "q3s",
        "raw",
        "scn",
        "sib",
        "smd",
        "stl",
        "stp",
        "step",
        "ter",
        "uc",
        "usd",
        "usda",
        "usdc",
        "usdz",
        "vta",
        "x",
        "x3d",
        "xgl",
        "zgl")]
    [XRAssetInspector("XREngine.Editor.AssetEditors.XRPrefabSourceInspector")]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRPrefabSource : XRAsset, IModelCacheAsset
    {
        private sealed class RestoreProcessMeshesAsyncScope(ModelImportOptions options, bool? requestedProcessMeshesAsynchronously) : IDisposable
        {
            private ModelImportOptions? _options = options;
            private readonly bool? _requestedProcessMeshesAsynchronously = requestedProcessMeshesAsynchronously;

            public void Dispose()
            {
                if (_options is null)
                    return;

                _options.ProcessMeshesAsynchronously = _requestedProcessMeshesAsynchronously;
                _options = null;
            }
        }

        private SceneNode? _rootNode;
        private UnityPrefabImportManifest? _unityImportManifest;
        private ModelImportProducerReport? _producerReport;

        internal static IDisposable EnterSynchronousMeshImportScope(ModelImportOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            bool? requestedProcessMeshesAsynchronously = options.ProcessMeshesAsynchronously;
            options.ProcessMeshesAsynchronously = false;
            return new RestoreProcessMeshesAsyncScope(options, requestedProcessMeshesAsynchronously);
        }

        /// <summary>
        /// Root of the prefab hierarchy. All descendants get stable prefab GUIDs when assigned here.
        /// </summary>
        public SceneNode? RootNode
        {
            get => _rootNode;
            set
            {
                if (SetField(ref _rootNode, value) && value is not null)
                    SceneNodePrefabUtility.EnsurePrefabMetadata(value, ID, overwriteExisting: true);
            }
        }

        /// <summary>
        /// Dependency fingerprints, conversion outcomes, and diagnostics for a prefab converted
        /// from a Unity project. This is <see langword="null"/> for native and ordinary model imports.
        /// </summary>
        public UnityPrefabImportManifest? UnityImportManifest
        {
            get => _unityImportManifest;
            set => SetField(ref _unityImportManifest, value);
        }

        /// <summary>
        /// Normalized metadata emitted by the producer that completed the most recent cold import.
        /// The future model binary manifest persists this contract; project YAML keeps its own
        /// authoritative remaps and does not serialize this runtime handoff object.
        /// </summary>
        [MemoryPackIgnore]
        [YamlIgnore]
        public ModelImportProducerReport? ProducerReport
        {
            get => _producerReport;
            set => SetField(ref _producerReport, value);
        }

        /// <summary>
        /// Creates a runtime instance of the prefab hierarchy.
        /// </summary>
        public SceneNode Instantiate(XRWorldInstance? world = null,
                                     SceneNode? parent = null,
                                     bool maintainWorldTransform = false)
        {
            if (RootNode is null)
                throw new InvalidOperationException("Cannot instantiate an empty prefab.");

            // Ensure the template tree has stable metadata before we serialize/clone it.
            SceneNodePrefabUtility.EnsurePrefabMetadata(RootNode, ID, overwriteExisting: false);

            return SceneNodePrefabUtility.Instantiate(RootNode,
                                                       ID,
                                                       world,
                                                       parent,
                                                       maintainWorldTransform);
        }

        public override bool Load3rdParty(string filePath)
        {
            // Prefer loading via the import pipeline so selecting a 3rd-party model file in the editor
            // reflects current cached import settings.
            object? opts = null;
            try
            {
                opts = Engine.Assets.GetOrCreateThirdPartyImportOptions(filePath, GetType());
            }
            catch
            {
                // Fall back to defaults if import options cannot be resolved.
            }

            return Import3rdParty(filePath, opts);
        }

        public override bool Import3rdParty(string filePath, object? importOptions)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            if (string.Equals(Path.GetExtension(filePath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var unityOptions = importOptions as ModelImportOptions ?? new ModelImportOptions();
                UnityPrefabConversionResult conversion = UnityEditorImportBridge.ImportPrefabConversion(
                    filePath,
                    FilePath,
                    unityOptions.UnityProjectRootOverride,
                    unityOptions.CookSettings,
                    unityOptions.CookOverrides);
                RootNode = conversion.RootNode;
                UnityImportManifest = conversion.Manifest;
                ProducerReport = RootNode is null
                    ? null
                    : UnityModelImportProducerAdapter.CreateReport(filePath, unityOptions, conversion.Manifest);
                if (RootNode is not null && !conversion.MeshletCookingCompleted)
                    throw new InvalidOperationException(
                        $"Unity prefab conversion for '{filePath}' returned an uncooked hierarchy. " +
                        "The Unity importer must cook the completed hierarchy before publication.");
                return RootNode is not null;
            }

            var opts = importOptions as ModelImportOptions ?? new ModelImportOptions();
            bool importOptionsChanged = false;

            Dictionary<string, XRTexture2D?> textureRemap = opts.TextureRemap ??= [];
            Dictionary<string, XRMaterial?> materialRemap = opts.MaterialRemap ??= [];
            IReadOnlyDictionary<string, string> legacyPathRemap = opts.LegacyTexturePathRemapValues ?? new Dictionary<string, string>();
            IReadOnlyDictionary<string, string> legacyMaterialRemap = opts.LegacyMaterialNameRemapValues ?? new Dictionary<string, string>();

            void TrackTextureKey(string path)
            {
                if (!textureRemap.ContainsKey(path))
                {
                    textureRemap.Add(path, null);
                    importOptionsChanged = true;
                }
            }

            void TrackMaterialKey(string name)
            {
                if (!materialRemap.ContainsKey(name))
                {
                    materialRemap.Add(name, null);
                    importOptionsChanged = true;
                }
            }

            using var importer = new ModelImporter(filePath, onCompleted: null, materialFactory: null);
            importer.ImportOptions = opts;

            // Preserve the importer's default texture factory (it sets FilePath + schedules actual loads)
            // and only apply optional user remapping on top.
            var defaultMakeTexture = importer.MakeTextureAction;

            XRTexture2D GetOrCreateTextureRemapped(string path)
            {
                TrackTextureKey(path);

                if (textureRemap.TryGetValue(path, out XRTexture2D? replacementTexture) && replacementTexture is not null)
                    return replacementTexture;

                if (legacyPathRemap.TryGetValue(path, out string? newPath) && !string.IsNullOrEmpty(newPath))
                    path = newPath;

                return defaultMakeTexture(path);
            }

            XRMaterial GetOrCreateMaterialRemapped(XRTexture[] textureList, List<TextureSlot> textures, string name)
            {
                TrackMaterialKey(name);

                if (materialRemap.TryGetValue(name, out XRMaterial? replacementMaterial) && replacementMaterial is not null)
                    return replacementMaterial;

                if (legacyMaterialRemap.TryGetValue(name, out string? replacementPath) &&
                    !string.IsNullOrEmpty(replacementPath) &&
                    File.Exists(replacementPath))
                {
                    XRMaterial? replacementMat = Engine.Assets.Load<XRMaterial>(replacementPath);
                    if (replacementMat is not null)
                        return replacementMat;
                }

                return CreateMaterial(textureList, textures, name);
            }

            importer.MakeMaterialAction = GetOrCreateMaterialRemapped;
            importer.MakeTextureAction = GetOrCreateTextureRemapped;

            bool batchSubmeshAddsDuringAsyncImport = opts.BatchSubmeshAddsDuringAsyncImport;

            // Prefab imports must be fully populated before returning because downstream callers
            // immediately serialize/externalize the imported asset graph.
            SceneNode? rootNode;
            using (EnterSynchronousMeshImportScope(opts))
            {
                rootNode = importer.Import(
                    opts.PostProcessSteps,
                    preservePivots: opts.FbxPivotPolicy == FbxPivotImportPolicy.PreservePivotSemantics,
                    removeAssimpFBXNodes: opts.CollapseGeneratedFbxHelperNodes,
                    scaleConversion: opts.ScaleConversion,
                    zUp: opts.ZUp,
                    multiThread: opts.MultiThread,
                    processMeshesAsynchronously: opts.ProcessMeshesAsynchronously,
                    batchSubmeshAddsDuringAsyncImport: batchSubmeshAddsDuringAsyncImport,
                    onProgress: opts.ProgressCallback);
            }

            if (rootNode is null)
                return false;

            ModelImportProducerReport? producerReport = importer.LastProducerReport;
            if (producerReport is not null)
            {
                foreach (ModelImportReferenceKey reference in producerReport.ReferenceKeys)
                {
                    if (reference.Kind == ModelImportReferenceKind.Texture)
                        TrackTextureKey(reference.Key);
                    else if (reference.Kind == ModelImportReferenceKind.Material)
                        TrackMaterialKey(reference.Key);
                }
            }

            if (importOptionsChanged)
                Engine.Assets.SaveThirdPartyImportOptions(filePath, GetType(), opts);

            RootNode = rootNode;
            UnityImportManifest = null;
            ProducerReport = producerReport;
            Name ??= Path.GetFileNameWithoutExtension(filePath);
            return RootNode is not null;
        }

        private static XRMaterial CreateMaterial(XRTexture[] textureList, List<TextureSlot> textures, string name)
            => ModelImporter.MakeMaterialDeferred(textureList, textures, name);

        private static void MakeDefaultParameters(XRMaterial mat)
            => mat.Parameters =
            [
                new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
                new ShaderFloat(1.0f, "Opacity"),
                new ShaderFloat(1.0f, "Roughness"),
                new ShaderFloat(0.0f, "Metallic"),
                new ShaderFloat(0.0f, "Specular"),
                new ShaderFloat(0.0f, "Emission"),
            ];
    }
}
