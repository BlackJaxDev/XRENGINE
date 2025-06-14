using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Extensions;
using ImageMagick;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using AScene = Assimp.Scene;
using BlendMode = XREngine.Rendering.Models.Materials.BlendMode;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine
{
    /// <summary>
    /// This class is used to import models from various formats using the Assimp library.
    /// Returns a SceneNode hierarchy populated with ModelComponents, and outputs generated materials and meshes.
    /// </summary>
    public class ModelImporter : IDisposable
    {
        public delegate void DelMakeMaterialAction(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name);

        public DelMakeMaterialAction MakeMaterialAction { get; set; } = MakeMaterial;

        public ModelImporter(string path, Action? onCompleted, DelMaterialFactory? materialFactory)
        {
            _assimp = new AssimpContext();
            _path = path;
            _onCompleted = onCompleted;
            _materialFactory = materialFactory ?? MaterialFactory;
        }

        private readonly ConcurrentDictionary<string, XRTexture2D> _texturePathCache = new();

        private void MakeMaterialInternal(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            FillTextures(mat, textureList);
            MakeMaterialAction(mat, textureList, textures, name);
        }

        private static void FillTextures(XRMaterial mat, XRTexture[] textureList)
        {
            for (int i = 0; i < textureList.Length; i++)
            {
                XRTexture? tex = textureList[i];
                if (tex is not null)
                    mat.Textures[i] = tex;
            }
        }

        public static void MakeMaterial(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            bool transp = textures.Any(x => (x.Flags & 0x2) != 0 || x.TextureType == TextureType.Opacity);
            bool normal = textures.Any(x => x.TextureType == TextureType.Normals);
            if (textureList.Length > 0)
            {
                if (transp || textureList.Any(x => x is not null && x.HasAlphaChannel))
                {
                    transp = true;
                    mat.Shaders.Add(ShaderHelper.UnlitTextureFragForward()!);
                }
                else
                {
                    mat.Shaders.Add(ShaderHelper.LitTextureFragDeferred()!);
                    mat.Parameters =
                    [
                        new ShaderFloat(1.0f, "Opacity"),
                                new ShaderFloat(1.0f, "Specular"),
                                new ShaderFloat(0.9f, "Roughness"),
                                new ShaderFloat(0.0f, "Metallic"),
                                new ShaderFloat(1.0f, "IndexOfRefraction"),
                            ];
                }
            }
            else
            {
                //Show the material as magenta if no textures are present
                mat.Shaders.Add(ShaderHelper.LitColorFragDeferred()!);
                mat.Parameters =
                [
                    new ShaderVector3(ColorF3.Magenta, "BaseColor"),
                        new ShaderFloat(1.0f, "Opacity"),
                        new ShaderFloat(1.0f, "Specular"),
                        new ShaderFloat(1.0f, "Roughness"),
                        new ShaderFloat(0.0f, "Metallic"),
                        new ShaderFloat(1.0f, "IndexOfRefraction"),
                    ];
            }

            mat.RenderPass = transp ? (int)EDefaultRenderPass.TransparentForward : (int)EDefaultRenderPass.OpaqueDeferredLit;
            mat.Name = name;
            mat.RenderOptions = new RenderingParameters()
            {
                CullMode = ECullMode.Back,
                DepthTest = new DepthTest()
                {
                    UpdateDepth = true,
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Less,
                },
                //LineWidth = 5.0f,
                BlendModeAllDrawBuffers = transp ? BlendMode.EnabledTransparent() : BlendMode.Disabled(),
            };
        }

        public XRMaterial MaterialFactory(
            string modelFilePath,
            string name,
            List<TextureSlot> textures,
            TextureFlags flags,
            ShadingMode mode,
            Dictionary<string, List<MaterialProperty>> properties)
        {
            XRTexture[] textureList = new XRTexture[textures.Count];
            XRMaterial mat = new(textureList);
            ParallelLoopResult MakeTextures()
            {
                void MakeTexture(int i) => LoadTexture(modelFilePath, textures, textureList, i);
                return Parallel.For(0, textures.Count, MakeTexture);
            }
            Task.Run(MakeTextures).ContinueWith(t => MakeMaterialInternal(mat, textureList, textures, name));
            return mat;
        }

        public void LoadTexture(string modelFilePath, List<TextureSlot> textures, XRTexture[] textureList, int i)
        {
            string path = textures[i].FilePath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            path = path.Replace("/", "\\");
            bool rooted = Path.IsPathRooted(path);
            if (!rooted)
            {
                string? dir = Path.GetDirectoryName(modelFilePath);
                if (dir is not null)
                    path = Path.Combine(dir, path);
            }

            textureList[i] = _texturePathCache.GetOrAdd(path, MakeTextureAction);
        }

        public Func<string, XRTexture2D> MakeTextureAction { get; set; } = TextureFactoryInternal;

        private static XRTexture2D TextureFactoryInternal(string path)
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
                };
            }
            else
            {
                Debug.Out($"Loaded texture: {path}");
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

        public string SourceFilePath => _path;

        private readonly AssimpContext _assimp;
        private readonly string _path;
        private readonly Action? _onCompleted;

        public delegate XRMaterial DelMaterialFactory(
            string modelFilePath,
            string name,
            List<TextureSlot> textures,
            TextureFlags flags,
            ShadingMode mode,
            Dictionary<string, List<MaterialProperty>> properties);

        private readonly DelMaterialFactory _materialFactory;

        private readonly ConcurrentDictionary<string, TextureSlot> _textureInfoCache = [];
        private readonly ConcurrentDictionary<string, MagickImage?> _textureCache = new();
        private readonly Dictionary<string, List<SceneNode>> _nodeCache = [];

        private readonly ConcurrentBag<XRMesh> _meshes = [];
        private readonly ConcurrentBag<XRMaterial> _materials = [];

        // Class to track original node transforms for later normalization
        private class NodeTransformInfo(Node assimpNode, SceneNode sceneNode, Vector3 scale, List<int> meshIndices)
        {
            public Node AssimpNode = assimpNode;
            public SceneNode SceneNode = sceneNode;
            public Vector3 OriginalScale = scale;
            public List<int> MeshIndices = meshIndices;
            public Matrix4x4 OriginalWorldMatrix;
        }

        public static SceneNode? Import(
            string path,
            PostProcessSteps options,
            out IReadOnlyCollection<XRMaterial> materials,
            out IReadOnlyCollection<XRMesh> meshes,
            Action? onCompleted,
            DelMaterialFactory? materialFactory,
            SceneNode? parent,
            float scaleConversion = 1.0f,
            bool zUp = false)
        {
            using var importer = new ModelImporter(path, onCompleted, materialFactory);
            var node = importer.Import(options, true, true, scaleConversion, zUp, true);
            materials = importer._materials;
            meshes = importer._meshes;
            if (parent != null && node != null)
                parent.Transform.AddChild(node.Transform, false, true);
            return node;
        }
        public static async Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> ImportAsync(
            string path,
            PostProcessSteps options,
            Action? onCompleted,
            DelMaterialFactory? materialFactory,
            SceneNode? parent,
            float scaleConversion = 1.0f,
            bool zUp = false)
            => await Task.Run(() =>
            {
                SceneNode? node = Import(path, options, out var materials, out var meshes, onCompleted, materialFactory, parent, scaleConversion, zUp);
                return (node, materials, meshes);
            });

        private readonly ConcurrentBag<Action> _meshProcessActions = [];
        private readonly List<NodeTransformInfo> _nodeTransforms = [];

        public unsafe SceneNode? Import(
            PostProcessSteps options = PostProcessSteps.None,
            bool preservePivots = true,
            bool removeAssimpFBXNodes = true,
            float scaleConversion = 1.0f,
            bool zUp = false,
            bool multiThread = true)
        {
            SetAssimpConfig(preservePivots, scaleConversion, zUp, multiThread);

            AScene scene;
            using (Engine.Profiler.Start($"Assimp ImportFile: {SourceFilePath} with options: {options}"))
                scene = _assimp.ImportFile(SourceFilePath, options);

            if (scene is null || scene.SceneFlags == SceneFlags.Incomplete || scene.RootNode is null)
                return null;

            SceneNode rootNode;
            using (Engine.Profiler.Start($"Assemble model hierarchy"))
            {
                rootNode = new(Path.GetFileNameWithoutExtension(SourceFilePath));
                _nodeTransforms.Clear();
                ProcessNode(true, scene.RootNode, scene, rootNode, null, Matrix4x4.CreateScale(1.0f, 1.0f, -1.0f), removeAssimpFBXNodes);
                NormalizeNodeScales(scene, rootNode);
            }

            Action meshProcessAction = multiThread
                ? ProcessMeshesParallel
                : ProcessMeshesSequential;

            if (Engine.Rendering.Settings.ProcessMeshImportsAsynchronously)
            {
                void Complete(object o) => _onCompleted?.Invoke();
                Task.Run(meshProcessAction).ContinueWith(Complete);
            }
            else
            {
                meshProcessAction();
                _onCompleted?.Invoke();
            }

            return rootNode;
        }

        private void SetAssimpConfig(bool preservePivots, float scaleConversion, bool zUp, bool multiThread)
        {
            float rotate = zUp ? -90.0f : 0.0f;
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, preservePivots));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_MATERIALS, true));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_TEXTURES, true));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_GLOB_MULTITHREADING, multiThread));
            _assimp.Scale = scaleConversion;
            _assimp.XAxisRotation = rotate;
        }

        //TODO: more extreme idea: allocate all initial meshes, and sequentially populate every mesh's buffers in parallel
        private void ProcessMeshesParallel()
        {
            using var t = Engine.Profiler.Start("Processing meshes in parallel");
            Parallel.ForEach(_meshProcessActions, action => action());
        }

        private void ProcessMeshesSequential()
        {
            using var t = Engine.Profiler.Start("Processing meshes sequentially");
            foreach (var action in _meshProcessActions)
                action();
        }

        private void NormalizeNodeScales(AScene scene, SceneNode rootNode)
        {
            using var t = Engine.Profiler.Start("Normalizing node scales");

            foreach (var nodeInfo in _nodeTransforms)
            {
                nodeInfo.OriginalWorldMatrix = nodeInfo.SceneNode.Transform.WorldMatrix;

                var transform = nodeInfo.SceneNode.GetTransformAs<Transform>(false)!;

                // Store the original world position
                Vector3 originalWorldPos = nodeInfo.SceneNode.Transform.WorldTranslation;

                // Create a new local matrix with unit scale but same rotation
                // Extract rotation from original local matrix
                Matrix4x4.Decompose(transform.LocalMatrix, out _, out Quaternion rotation, out Vector3 translation);

                // Create new local matrix with unit scale
                Matrix4x4 normalizedLocalMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

                // Apply the new local matrix
                transform.DeriveLocalMatrix(normalizedLocalMatrix);
                transform.RecalculateMatrices(true, true);

                // Calculate the position difference caused by scale removal
                Vector3 newWorldPos = nodeInfo.SceneNode.Transform.WorldTranslation;
                Vector3 positionOffset = originalWorldPos - newWorldPos;

                // Adjust the local translation to compensate for scale removal
                if (positionOffset != Vector3.Zero)
                {
                    // For position offsets, use the full inverse transform
                    Vector3 localOffset = transform.Parent?.InverseTransformPoint(originalWorldPos) ?? originalWorldPos;

                    // Then set the local translation directly
                    transform.Translation = localOffset;
                    transform.RecalculateMatrices(true, true);
                }

                transform.SaveBindState();
            }

            //Create mesh process actions and create a transform to move vertices to model root space
            foreach (var nodeInfo in _nodeTransforms)
            {
                if (nodeInfo.MeshIndices.Count <= 0)
                    continue;
                
                var rootTransform = rootNode.Transform;
                Matrix4x4 geometryTransform = nodeInfo.OriginalWorldMatrix * rootTransform.InverseWorldMatrix;
                EnqueueProcessMeshes(nodeInfo.AssimpNode, scene, nodeInfo.SceneNode, geometryTransform, rootTransform);
            }
        }

        private void ProcessNode(
            bool rootNode,
            Node node,
            AScene scene,
            SceneNode parentSceneNode,
            TransformBase? rootTransform,
            Matrix4x4 rootTransformMatrix,
            bool removeAssimpFBXNodes = true,
            Matrix4x4? fbxMatrixParent = null)
        {
            Matrix4x4 nodeTransform = node.Transform.Transposed() * rootTransformMatrix;
            Matrix4x4.Decompose(nodeTransform, out Vector3 scale, out _, out _);

            SceneNode sceneNode = CreateNode(
                rootNode,
                parentSceneNode,
                fbxMatrixParent,
                removeAssimpFBXNodes,
                out Matrix4x4? fbxMatrix,
                node.Name,
                nodeTransform);

            if (rootNode)
                rootTransform = sceneNode.Transform;

            List<int> meshIndices = [];
            for (var i = 0; i < node.MeshCount; i++)
                meshIndices.Add(node.MeshIndices[i]);
            
            // Store node information for later normalization
            _nodeTransforms.Add(new NodeTransformInfo(
                node,
                sceneNode,
                scale,
                meshIndices
            ));

            // Process children
            for (var i = 0; i < node.ChildCount; i++)
            {
                Node childNode = node.Children[i];
                ProcessNode(
                    false,
                    childNode,
                    scene,
                    sceneNode,
                    rootTransform,
                    Matrix4x4.Identity,
                    removeAssimpFBXNodes,
                    fbxMatrix);
            }
        }

        private SceneNode CreateNode(
            bool rootNode,
            SceneNode parentSceneNode,
            Matrix4x4? fbxMatrixParent,
            bool removeAssimpFBXNodes,
            out Matrix4x4? fbxMatrix,
            string name,
            Matrix4x4 localTransform)
        {
            fbxMatrix = null;
            bool remove = removeAssimpFBXNodes && !rootNode;
            if (remove)
            {
                int assimpFBXMagic = name.IndexOf("_$AssimpFbx$");
                bool assimpFBXNode = assimpFBXMagic != -1;
                if (assimpFBXNode)
                {
                    //Debug.Out($"Removing {name}");
                    name = name[..assimpFBXMagic];
                    bool affectsParent = parentSceneNode.Name?.StartsWith(name, StringComparison.InvariantCulture) ?? false;
                    if (affectsParent)
                    {
                        var tfm = parentSceneNode.Transform;
                        tfm.DeriveLocalMatrix(localTransform * parentSceneNode.Transform.LocalMatrix);
                        tfm.RecalculateMatrices(true, true);
                        tfm.SaveBindState();
                    }
                    else
                    {
                        fbxMatrix = localTransform;
                        if (fbxMatrixParent.HasValue)
                            fbxMatrix *= fbxMatrixParent.Value;
                    }
                    return parentSceneNode;
                }
            }
            return CreateNode(localTransform, parentSceneNode, fbxMatrixParent, remove, name);
        }

        private SceneNode CreateNode(
            Matrix4x4 localTransform,
            SceneNode parentSceneNode,
            Matrix4x4? fbxMatrixParent,
            bool removeAssimpFBXNodes,
            string name)
        {
            if (removeAssimpFBXNodes && fbxMatrixParent.HasValue)
                localTransform *= fbxMatrixParent.Value;

            SceneNode sceneNode = new(parentSceneNode, name);
            var tfm = sceneNode.GetTransformAs<Transform>(true)!;
            tfm.DeriveLocalMatrix(localTransform);
            tfm.RecalculateMatrices(true, true);
            tfm.SaveBindState();

            if (_nodeCache.TryGetValue(name, out List<SceneNode>? nodes))
                nodes.Add(sceneNode);
            else
                _nodeCache.Add(name, [sceneNode]);

            return sceneNode;
        }

        private unsafe void EnqueueProcessMeshes(Node node, AScene scene, SceneNode sceneNode, Matrix4x4 dataTransform, TransformBase rootTransform)
        {
            int count = node.MeshCount;
            if (count == 0)
                return;

            _meshProcessActions.Add(() => ProcessMeshes(node, scene, sceneNode, dataTransform, rootTransform));
        }

        private unsafe void ProcessMeshes(Node node, AScene scene, SceneNode sceneNode, Matrix4x4 dataTransform, TransformBase rootTransform)
        {
            using var t = Engine.Profiler.Start($"Processing meshes for {node.Name}");

            ModelComponent modelComponent = sceneNode.AddComponent<ModelComponent>()!;
            Model model = new();
            modelComponent.Name = node.Name;
            for (var i = 0; i < node.MeshCount; i++)
            {
                int meshIndex = node.MeshIndices[i];
                Mesh mesh = scene.Meshes[meshIndex];

                (XRMesh xrMesh, XRMaterial xrMaterial) = ProcessSubMesh(mesh, scene, dataTransform);

                _meshes.Add(xrMesh);
                _materials.Add(xrMaterial);

                model.Meshes.Add(new SubMesh(xrMesh, xrMaterial) { Name = mesh.Name, RootTransform = rootTransform });
            }

            modelComponent!.Model = model;
        }

        private unsafe (XRMesh mesh, XRMaterial material) ProcessSubMesh(
            Mesh mesh,
            AScene scene,
            Matrix4x4 dataTransform)
        {
            using var t = Engine.Profiler.Start($"Processing submesh for {mesh.Name}");

            Task<XRMesh> newMesh = Task.Run(() => new XRMesh(mesh, _assimp, _nodeCache, dataTransform));
            Task<XRMaterial> newMaterial = Task.Run(() => ProcessMaterial(mesh, scene));
            Task.WaitAll(newMesh, newMaterial);
            return (newMesh.Result, newMaterial.Result);
        }

        private unsafe XRMaterial ProcessMaterial(Mesh mesh, AScene scene)
        {
            using var t = Engine.Profiler.Start($"Processing material for {mesh.Name}");

            Material matInfo = scene.Materials[mesh.MaterialIndex];
            List<TextureSlot> textures = [];
            for (int i = 0; i < 22; ++i)
            {
                TextureType type = (TextureType)i;
                var maps = LoadMaterialTextures(matInfo, type);
                if (maps.Count > 0)
                    textures.AddRange(maps);
            }
            ReadProperties(matInfo, out string name, out TextureFlags flags, out ShadingMode mode, out var propDic);
            return _materialFactory(SourceFilePath, name, textures, flags, mode, propDic);
        }

        private static unsafe void ReadProperties(Material material, out string name, out TextureFlags flags, out ShadingMode shadingMode, out Dictionary<string, List<MaterialProperty>> properties)
        {
            var props = material.GetAllProperties();
            Dictionary<string, List<MaterialProperty>> dic = [];
            foreach (var prop in props)
            {
                if (!dic.TryGetValue(prop.Name, out List<MaterialProperty>? list))
                    dic.Add(prop.Name, list = []);
                list.Add(prop);
            }

            name = dic.TryGetValue(AI_MATKEY_NAME, out List<MaterialProperty>? nameList)
                ? nameList[0].GetStringValue() ?? AI_DEFAULT_MATERIAL_NAME
                : AI_DEFAULT_MATERIAL_NAME;

            flags = dic.TryGetValue(_AI_MATKEY_TEXFLAGS_BASE, out List<MaterialProperty>? flag) && flag[0].GetIntegerValue() is int f ? (TextureFlags)f : 0;
            shadingMode = dic.TryGetValue(AI_MATKEY_SHADING_MODEL, out List<MaterialProperty>? sm) && sm[0].GetIntegerValue() is int mode ? (ShadingMode)mode : ShadingMode.Flat;
            properties = dic;
        }

        const string AI_DEFAULT_MATERIAL_NAME = "DefaultMaterial";

        const string AI_MATKEY_BLEND_FUNC = "$mat.blend";
        const string AI_MATKEY_BUMPSCALING = "$mat.bumpscaling";
        const string AI_MATKEY_COLOR_AMBIENT = "$clr.ambient";
        const string AI_MATKEY_COLOR_DIFFUSE = "$clr.diffuse";
        const string AI_MATKEY_COLOR_EMISSIVE = "$clr.emissive";
        const string AI_MATKEY_COLOR_REFLECTIVE = "$clr.reflective";
        const string AI_MATKEY_COLOR_SPECULAR = "$clr.specular";
        const string AI_MATKEY_COLOR_TRANSPARENT = "$clr.transparent";
        const string AI_MATKEY_ENABLE_WIREFRAME = "$mat.wireframe";
        const string AI_MATKEY_GLOBAL_BACKGROUND_IMAGE = "?bg.global";
        const string AI_MATKEY_NAME = "?mat.name";
        const string AI_MATKEY_OPACITY = "$mat.opacity";
        const string AI_MATKEY_REFLECTIVITY = "$mat.reflectivity";
        const string AI_MATKEY_REFRACTI = "$mat.refracti";
        const string AI_MATKEY_SHADING_MODEL = "$mat.shadingm";
        const string AI_MATKEY_SHININESS = "$mat.shininess";
        const string AI_MATKEY_SHININESS_STRENGTH = "$mat.shinpercent";
        const string AI_MATKEY_TWOSIDED = "$mat.twosided";

        const string _AI_MATKEY_TEXTURE_BASE = "$tex.file";
        const string _AI_MATKEY_UVWSRC_BASE = "$tex.uvwsrc";
        const string _AI_MATKEY_TEXOP_BASE = "$tex.op";
        const string _AI_MATKEY_MAPPING_BASE = "$tex.mapping";
        const string _AI_MATKEY_TEXBLEND_BASE = "$tex.blend";
        const string _AI_MATKEY_MAPPINGMODE_U_BASE = "$tex.mapmodeu";
        const string _AI_MATKEY_MAPPINGMODE_V_BASE = "$tex.mapmodev";
        const string _AI_MATKEY_TEXMAP_AXIS_BASE = "$tex.mapaxis";
        const string _AI_MATKEY_UVTRANSFORM_BASE = "$tex.uvtrafo";
        const string _AI_MATKEY_TEXFLAGS_BASE = "$tex.flags";

        private unsafe List<TextureSlot> LoadMaterialTextures(Material mat, TextureType type)
        {
            List<TextureSlot> textures = [];
            var textureCount = mat.GetMaterialTextureCount(type);
            for (int i = 0; i < textureCount; i++)
            {
                if (!mat.GetMaterialTexture(type, i, out TextureSlot slot))
                    continue;

                string path = slot.FilePath;
                bool skip = false;
                foreach (var existingTexPath in _textureInfoCache.Keys)
                {
                    if (!string.Equals(existingTexPath, path, StringComparison.OrdinalIgnoreCase))
                        continue;

                    textures.Add(_textureInfoCache[existingTexPath]);
                    skip = true;
                    break;
                }
                if (!skip)
                {
                    textures.Add(slot);
                    _textureInfoCache.TryAdd(path, slot);
                }
            }
            return textures;
        }

        public void Dispose()
        {
            foreach (var tex in _textureCache.Values)
                tex?.Dispose();
            _textureCache.Clear();
            _textureInfoCache.Clear();
        }
    }
}