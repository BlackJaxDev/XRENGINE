using MemoryPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Assimp;
using XREngine.Data;
using XREngine.Core.Files;
using XREngine.Fbx;
using XREngine.Gltf;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;

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
        "mesh",
        "mesh.xml",
        "mot",
        "ms3d",
        "ndo",
        "nff",
        "obj",
        "off",
        "ogex",
        "ply",
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
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRPrefabSource : XRAsset
    {
        private SceneNode? _rootNode;

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

            if (GltfImportKeyUtilities.IsGltfPath(filePath))
            {
                try
                {
                    GltfRoot gltfDocument = GltfJsonLoader.Load(filePath);
                    foreach (string textureKey in GltfImportKeyUtilities.EnumerateReferencedTextureKeys(gltfDocument))
                        TrackTextureKey(textureKey);
                    foreach (string materialKey in GltfImportKeyUtilities.GetMaterialKeys(gltfDocument))
                        TrackMaterialKey(materialKey);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[XRPrefabSource] Failed to pre-seed glTF remap keys for '{filePath}'. {ex.Message}");
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

            bool? processMeshesAsynchronously = opts.ProcessMeshesAsynchronously;
            bool batchSubmeshAddsDuringAsyncImport = opts.BatchSubmeshAddsDuringAsyncImport;

            SceneNode? rootNode = importer.Import(
                opts.PostProcessSteps,
                preservePivots: opts.FbxPivotPolicy == FbxPivotImportPolicy.PreservePivotSemantics,
                removeAssimpFBXNodes: opts.CollapseGeneratedFbxHelperNodes,
                scaleConversion: opts.ScaleConversion,
                zUp: opts.ZUp,
                multiThread: opts.MultiThread,
                processMeshesAsynchronously: processMeshesAsynchronously,
                batchSubmeshAddsDuringAsyncImport: batchSubmeshAddsDuringAsyncImport);

            if (rootNode is null)
                return false;

            if (importOptionsChanged)
                Engine.Assets.SaveThirdPartyImportOptions(filePath, GetType(), opts);

            RootNode = rootNode;
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
