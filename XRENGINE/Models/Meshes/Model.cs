using MemoryPack;
using XREngine.Data;
using XREngine.Core.Files;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Assimp;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Models
{
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
    public partial class Model : XRAsset
    {
        public Model() { }
        public Model(params SubMesh[] meshes)
            => _meshes.AddRange(meshes);
        public Model(IEnumerable<SubMesh> meshes)
            => _meshes.AddRange(meshes);

        protected EventList<SubMesh> _meshes = [];
        public EventList<SubMesh> Meshes
        {
            get => _meshes;
            set => _meshes = value ?? [];
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

        private static XRMaterial CreateMaterial(ModelImporter importer, string modelFilePath, List<TextureSlot> textures, string name)
        {
            //Debug.Out($"Making material for {name}: {string.Join(", ", textureList.Select(x => x?.Name ?? "<missing name>"))}");

            XRTexture[] textureList = importer.LoadTextures(modelFilePath, textures);
            XRMaterial mat = new(textureList);
            ModelImporter.FillTextures(mat, textureList);
            
            // Clear current shader list
            mat.Shaders.Clear();

            XRShader color = ShaderHelper.LitColorFragDeferred()!;
            XRShader albedo = ShaderHelper.LitTextureFragDeferred()!;
            XRShader albedoNormal = ShaderHelper.LitTextureNormalFragDeferred()!;
            XRShader albedoNormalMetallic = ShaderHelper.LitTextureNormalMetallicFragDeferred()!;
            XRShader albedoMetallic = ShaderHelper.LitTextureMetallicFragDeferred()!;
            XRShader albedoNormalRoughnessMetallic = ShaderHelper.LitTextureNormalRoughnessMetallicDeferred()!;
            XRShader albedoRoughness = ShaderHelper.LitTextureRoughnessFragDeferred()!;
            XRShader albedoMatcap = ShaderHelper.LitTextureMatcapDeferred()!;
            XRShader albedoEmissive = ShaderHelper.LitTextureEmissiveDeferred();

            switch (name)
            {
                default:
                    // Default material setup - use textured deferred if we have valid albedo textures
                    if (textureList.Length > 0 && textureList[0] is not null)
                    {
                        mat.Shaders.Add(albedo);
                        mat.Textures = [textureList[0]];
                    }
                    else
                    {
                        mat.Shaders.Add(color);
                    }
                    MakeDefaultParameters(mat);
                    break;
            }
            mat.Name = name;
            // Set a default render pass (opaque deferred lighting in this example)
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;

            return mat;
        }

        public static XRTexture2D GetOrCreateTexture(string path)
        {
            var tex = Engine.Assets.Load<XRTexture2D>(path);
            if (tex is null)
            {
                Debug.Out($"Failed to load texture: {path}");
                tex = new XRTexture2D()
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    MagFilter = ETexMagFilter.Linear,
                    MinFilter = ETexMinFilter.Linear,
                    UWrap = ETexWrapMode.Repeat,
                    VWrap = ETexWrapMode.Repeat,
                    AlphaAsTransparency = true,
                    AutoGenerateMipmaps = true,
                    Resizable = true,
                    FilePath = path,
                };

                // Best-effort: try to load the texture data asynchronously from the file path.
                // This keeps runtime behavior consistent even when the asset database can't resolve the texture.
                try
                {
                    XRTexture2D.ScheduleLoadJob(
                        path,
                        tex,
                        onFinished: loaded =>
                        {
                            loaded.MagFilter = ETexMagFilter.Linear;
                            loaded.MinFilter = ETexMinFilter.Linear;
                            loaded.UWrap = ETexWrapMode.Repeat;
                            loaded.VWrap = ETexWrapMode.Repeat;
                            loaded.AlphaAsTransparency = true;
                            loaded.AutoGenerateMipmaps = true;
                            loaded.Resizable = false;
                            loaded.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                        },
                        onError: ex => Debug.LogException(ex, $"Failed to load texture '{path}'."));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to schedule texture load for '{path}'. {ex.Message}");
                }
            }
            else
            {
                //Debug.Out($"Loaded texture: {path}");
                tex.MagFilter = ETexMagFilter.Linear;
                tex.MinFilter = ETexMinFilter.Linear;
                tex.UWrap = ETexWrapMode.Repeat;
                tex.VWrap = ETexWrapMode.Repeat;
                tex.AlphaAsTransparency = true;
                tex.AutoGenerateMipmaps = true;
                tex.Resizable = false;
                tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            }
            return tex;
        }

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

        public override bool Import3rdParty(string filePath, object? importOptions)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var opts = importOptions as ModelImportOptions ?? new ModelImportOptions();

            using var importer = new ModelImporter(filePath, onCompleted: null, materialFactory: null);

            // Preserve the importer's default texture factory (it sets FilePath + schedules actual loads)
            // and only apply optional user remapping on top.
            var defaultMakeTexture = importer.MakeTextureAction;

            XRTexture2D GetOrCreateTextureRemapped(string path)
            {
                Dictionary<string, string> pathRemap = opts.TexturePathRemap ?? [];
                if (pathRemap.TryGetValue(path, out string? newPath) && !string.IsNullOrEmpty(newPath))
                    path = newPath;

                return defaultMakeTexture(path);
            }
            XRMaterial GetOrCreateMaterialRemapped(List<TextureSlot> textures, string name)
            {
                Dictionary<string, string> materialRemap = opts.MaterialNameRemap ?? [];
                if (materialRemap.TryGetValue(name, out string? replacementPath) && !string.IsNullOrEmpty(replacementPath) && File.Exists(replacementPath))
                {
                    XRMaterial? replacementMat = Engine.Assets.Load<XRMaterial>(replacementPath);
                    if (replacementMat is not null)
                        return replacementMat;
                }
                return CreateMaterial(importer, filePath, textures, name);
            }
            importer.MakeMaterialAction = GetOrCreateMaterialRemapped;
            importer.MakeTextureAction = GetOrCreateTextureRemapped;

            // For asset import we prefer deterministic completion, so default to synchronous mesh processing.
            // If a user explicitly requests async processing, allow it via options.
            bool? processMeshesAsynchronously = opts.ProcessMeshesAsynchronously ? true : false;

            SceneNode? rootNode = importer.Import(
                opts.PostProcessSteps,
                preservePivots: opts.PreservePivots,
                removeAssimpFBXNodes: opts.RemoveAssimpFBXNodes,
                scaleConversion: opts.ScaleConversion,
                zUp: opts.ZUp,
                multiThread: opts.MultiThread,
                processMeshesAsynchronously: processMeshesAsynchronously);

            if (rootNode is null)
                return false;

            List<SubMesh> subMeshes = [];
            foreach (var node in EnumerateNodes(rootNode))
            {
                var modelComponent = node.GetLastComponent<ModelComponent>();
                if (modelComponent?.Model is not Model nodeModel)
                    continue;

                foreach (var subMesh in nodeModel.Meshes)
                {
                    // Avoid capturing the transient import scene graph in the serialized asset.
                    subMesh.RootTransform = null;
                    subMeshes.Add(subMesh);
                }
            }

            _meshes.Clear();
            _meshes.AddRange(subMeshes);

            Name ??= Path.GetFileNameWithoutExtension(filePath);
            return _meshes.Count > 0;
        }

        private static IEnumerable<SceneNode> EnumerateNodes(SceneNode root)
        {
            var stack = new Stack<SceneNode>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;

                foreach (var childTransform in node.Transform.Children)
                {
                    if (childTransform?.SceneNode is SceneNode childNode)
                        stack.Push(childNode);
                }
            }
        }
    }
}
