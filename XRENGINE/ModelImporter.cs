using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using Extensions;
using ImageMagick;
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
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
        public readonly record struct ModelImporterResult(
            SceneNode? RootNode,
            IReadOnlyCollection<XRMaterial> Materials,
            IReadOnlyCollection<XRMesh> Meshes);

        public delegate XRMaterial DelMakeMaterialAction(List<TextureSlot> textures, string name);

        public DelMakeMaterialAction MakeMaterialAction { get; set; } = MakeMaterialDefault;

        public ModelImporter(string path, Action? onCompleted, DelMaterialFactory? materialFactory)
        {
            _assimp = new AssimpContext();
            _path = path;
            _onCompleted = onCompleted;
            _materialFactory = materialFactory ?? MaterialFactory;
        }

        public static EnumeratorJob ScheduleImportJob(
            string path,
            PostProcessSteps options,
            Action<ModelImporterResult>? onFinished = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float>? onProgress = null,
            CancellationToken cancellationToken = default,
            SceneNode? parent = null,
            float scaleConversion = 1.0f,
            bool zUp = false,
            DelMaterialFactory? materialFactory = null,
            DelMakeMaterialAction? makeMaterialAction = null,
            int layer = DefaultLayers.DynamicIndex)
        {
            Debug.Out($"[ModelImporter] ScheduleImportJob called for: {path}");
            Debug.Out($"[ModelImporter] Parent node: {parent?.Name ?? "NULL"}");
            Debug.Out($"[ModelImporter] Thread: {Environment.CurrentManagedThreadId}");

            IEnumerable ImportRoutine()
            {
                Debug.Out($"[ModelImporter] ImportRoutine started on thread: {Environment.CurrentManagedThreadId}");
                // Run on the job system thread directly (no Task.Run). The job system already executes
                // this enumerator on a worker thread.
                var result = ImportInternal(path, options, parent, scaleConversion, zUp, onFinished, materialFactory, makeMaterialAction, onProgress, cancellationToken, layer);
                Debug.Out($"[ModelImporter] ImportInternal completed, yielding result");
                yield return new JobProgress(1f, result);
            }

            var job = Engine.Jobs.Schedule(
                ImportRoutine,
                progress: onProgress,
                completed: () => Debug.Out($"[ModelImporter] Job completed callback"),
                error: ex =>
                {
                    Debug.Out($"[ModelImporter] Job error callback: {ex.Message}");
                    onError?.Invoke(ex);
                },
                canceled: () =>
                {
                    Debug.Out($"[ModelImporter] Job canceled callback");
                    onCanceled?.Invoke();
                },
                progressWithPayload: null,
                cancellationToken: cancellationToken);
            Debug.Out($"[ModelImporter] Job scheduled");
            return job;
        }

        private readonly ConcurrentDictionary<string, XRTexture2D> _texturePathCache = new();

        private XRMaterial MakeMaterialInternal(List<TextureSlot> textures, string name)
        {
            return MakeMaterialAction(textures, name);
        }

        public static XRMaterial MakeMaterialDefault(List<TextureSlot> textures, string name)
        {
            bool transp = textures.Any(x => (x.Flags & 0x2) != 0 || x.TextureType == TextureType.Opacity);

            // Default material allocates the same number of texture slots as were discovered.
            // Texture loading/binding is handled elsewhere.
            var mat = new XRMaterial(new XRTexture?[textures.Count]);

            if (textures.Count > 0)
            {
                if (transp)
                {
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
                // Show the material as magenta if no textures are present.
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

            mat.RenderPass = transp ? (int)EDefaultRenderPass.TransparentForward : (int)EDefaultRenderPass.OpaqueDeferred;
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

            return mat;
        }

        public XRMaterial MaterialFactory(
            string modelFilePath,
            string name,
            List<TextureSlot> textures,
            TextureFlags flags,
            ShadingMode mode,
            Dictionary<string, List<MaterialProperty>> properties)
        {
            return MakeMaterialInternal(textures, name);
        }

        public XRTexture[] LoadTextures(string modelFilePath, List<TextureSlot> textures)
        {
            XRTexture[] textureList = new XRTexture[textures.Count];
            for (int i = 0; i < textures.Count; i++)
                LoadTexture(modelFilePath, textures, textureList, i);
            return textureList;
        }

        public static void FillTextures(XRMaterial mat, XRTexture[] textureList)
        {
            for (int i = 0; i < textureList.Length; i++)
            {
                XRTexture? tex = textureList[i];
                if (tex is not null)
                    mat.Textures[i] = tex;
            }
        }

        private void LoadTexture(string modelFilePath, List<TextureSlot> textures, XRTexture[] textureList, int i)
        {
            string path = textures[i].FilePath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            path = ResolveTextureFilePath(modelFilePath, path);
            textureList[i] = _texturePathCache.GetOrAdd(path, MakeTextureAction);
        }

        private readonly ConcurrentDictionary<string, bool> _missingTexturePathWarnings = new();

        private string ResolveTextureFilePath(string modelFilePath, string rawPath)
        {
            static string Normalize(string p)
            {
                p = p.Trim().Trim('"');
                p = p.Replace("/", "\\");
                while (p.StartsWith(".\\", StringComparison.Ordinal) || p.StartsWith("./", StringComparison.Ordinal))
                    p = p[2..];
                return p;
            }

            string normalized = Normalize(rawPath);
            if (string.IsNullOrWhiteSpace(normalized))
                return rawPath;

            // 1) If rooted, try it directly.
            if (Path.IsPathRooted(normalized))
            {
                if (!File.Exists(normalized) && _missingTexturePathWarnings.TryAdd(normalized, true))
                    Debug.LogWarning($"[ModelImporter] Texture path not found (rooted): '{normalized}' (from '{rawPath}')");
                return normalized;
            }

            string? modelDir = Path.GetDirectoryName(modelFilePath);
            if (string.IsNullOrWhiteSpace(modelDir))
                return normalized;

            // 2) Relative to model file.
            string candidate = Path.Combine(modelDir, normalized);
            if (File.Exists(candidate))
                return candidate;

            // 3) Common OBJ/MTL pattern: everything lives under a "textures" folder.
            string fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string texturesDirCandidate = Path.Combine(modelDir, "textures", fileName);
                if (File.Exists(texturesDirCandidate))
                    return texturesDirCandidate;

                string modelDirCandidate = Path.Combine(modelDir, fileName);
                if (File.Exists(modelDirCandidate))
                    return modelDirCandidate;
            }

            if (_missingTexturePathWarnings.TryAdd(candidate, true))
            {
                Debug.LogWarning($"[ModelImporter] Texture path not found: '{candidate}' (from '{rawPath}', model='{modelFilePath}')");
                if (!string.IsNullOrWhiteSpace(fileName))
                    Debug.Out($"[ModelImporter] Tried fallbacks: '{Path.Combine(modelDir, "textures", fileName)}', '{Path.Combine(modelDir, fileName)}'");
            }

            return candidate;
        }

        public Func<string, XRTexture2D> MakeTextureAction { get; set; } = TextureFactoryInternal;

        private static XRTexture2D TextureFactoryInternal(string path)
        {
            Debug.Out($"[TextureFactory] Creating placeholder for: {path}");
            Debug.Out($"[TextureFactory] File exists: {File.Exists(path)}");

            string textureName = Path.GetFileNameWithoutExtension(path);
            XRTexture2D placeholder = new()
            {
                Name = textureName,
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.Repeat,
                VWrap = ETexWrapMode.Repeat,
                AlphaAsTransparency = true,
                AutoGenerateMipmaps = true,
                Resizable = true,
                FilePath = path
            };

            try
            {
                placeholder.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
                Debug.Out($"[TextureFactory] Assigned filler texture to placeholder for: {textureName}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to assign filler texture for '{path}'. {ex.Message}");
            }

            XRTexture2D.ScheduleLoadJob(
                path,
                placeholder,
                onFinished: tex =>
                {
                    tex.MagFilter = ETexMagFilter.Linear;
                    tex.MinFilter = ETexMinFilter.Linear;
                    tex.UWrap = ETexWrapMode.Repeat;
                    tex.VWrap = ETexWrapMode.Repeat;
                    tex.AlphaAsTransparency = true;
                    tex.AutoGenerateMipmaps = true;
                    tex.Resizable = false;
                    tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                    var mipmaps = tex.Mipmaps;
                    uint w = mipmaps?.Length > 0 ? mipmaps[0].Width : 0;
                    uint h = mipmaps?.Length > 0 ? mipmaps[0].Height : 0;
                    Debug.Out($"[TextureFactory] LOADED texture: {path} ({w}x{h})");
                },
                onError: ex => Debug.LogException(ex, $"[TextureFactory] Texture import job FAILED for '{path}'."));

            return placeholder;
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

        private readonly ConcurrentDictionary<(TextureType type, string pathLower), TextureSlot> _textureInfoCache = [];
        private readonly ConcurrentDictionary<string, MagickImage?> _textureCache = new();
        private readonly Dictionary<string, List<SceneNode>> _nodeCache = [];

        private readonly ConcurrentBag<XRMesh> _meshes = [];
        private readonly ConcurrentBag<XRMaterial> _materials = [];

        /// <summary>
        /// The layer index to assign to all imported SceneNodes (e.g., DefaultLayers.StaticIndex for static meshes).
        /// </summary>
        private int _importLayer = DefaultLayers.DynamicIndex;

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
                parent.Transform.AddChild(node.Transform, false, EParentAssignmentMode.Immediate);
            return node;
        }

        public static Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> ImportAsync(
            string path,
            PostProcessSteps options,
            Action? onCompleted,
            DelMaterialFactory? materialFactory,
            SceneNode? parent,
            float scaleConversion = 1.0f,
            bool zUp = false,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<(SceneNode?, IReadOnlyCollection<XRMaterial>, IReadOnlyCollection<XRMesh>)>(TaskCreationOptions.RunContinuationsAsynchronously);

            ScheduleImportJob(
                path,
                options,
                onFinished: result =>
                {
                    onCompleted?.Invoke();
                    tcs.TrySetResult((result.RootNode, result.Materials, result.Meshes));
                },
                onError: ex => tcs.TrySetException(ex),
                onCanceled: () => tcs.TrySetCanceled(cancellationToken),
                onProgress: null,
                cancellationToken: cancellationToken,
                parent: parent,
                scaleConversion: scaleConversion,
                zUp: zUp,
                materialFactory: materialFactory,
                makeMaterialAction: null);

            return tcs.Task;
        }

        private static ModelImporterResult ImportInternal(
            string path,
            PostProcessSteps options,
            SceneNode? parent,
            float scaleConversion,
            bool zUp,
            Action<ModelImporterResult>? onFinished,
            DelMaterialFactory? materialFactory,
            DelMakeMaterialAction? makeMaterialAction,
            Action<float>? onProgress,
            CancellationToken cancellationToken,
            int layer = DefaultLayers.DynamicIndex)
        {
            Debug.Out($"[ModelImporter] ImportInternal started on thread: {Environment.CurrentManagedThreadId}");
            Debug.Out($"[ModelImporter] Path: {path}, Parent: {parent?.Name ?? "NULL"}");

            using var importer = new ModelImporter(path, onCompleted: null, materialFactory);
            if (makeMaterialAction is not null)
                importer.MakeMaterialAction = makeMaterialAction;
            importer._importLayer = layer;
            Debug.Out($"[ModelImporter] Created importer, calling Import()...");

            // Process meshes synchronously within this job thread.
            // The job system already runs this on a worker thread, so we don't need nested async.
            // This ensures _materials and _meshes are populated before we return.
            var node = importer.Import(options, true, true, scaleConversion, zUp, true, false, cancellationToken, onProgress);
            Debug.Out($"[ModelImporter] Import() returned, node: {node?.Name ?? "NULL"}");
            Debug.Out($"[ModelImporter] Meshes loaded: {importer._meshes.Count}, Materials loaded: {importer._materials.Count}");

            cancellationToken.ThrowIfCancellationRequested();

            // Add to parent using deferred mode - this queues the assignment to be processed
            // during PostUpdate, avoiding any blocking on the render thread
            if (parent != null && node != null)
            {
                Debug.Out($"[ModelImporter] Queueing AddChild (deferred): parent='{parent.Name}', child='{node.Name}'");
                parent.Transform.AddChild(node.Transform, false, EParentAssignmentMode.Deferred);
                Debug.Out($"[ModelImporter] AddChild queued for PostUpdate processing");
            }
            else
            {
                Debug.Out($"[ModelImporter] Skipping AddChild: parent={parent != null}, node={node != null}");
            }

            var result = new ModelImporterResult(node, importer._materials, importer._meshes);
            Debug.Out($"[ModelImporter] Invoking onFinished callback with {result.Meshes.Count} meshes, {result.Materials.Count} materials");
            onFinished?.Invoke(result);
            Debug.Out($"[ModelImporter] ImportInternal completed");
            return result;
        }

        private static readonly ConcurrentDictionary<(string path, string samplerName), XRTexture2D> _uberSamplerTextureCache = new();

        private static XRTexture2D GetOrCreateUberSamplerTexture(string filePath, string samplerName)
        {
            return _uberSamplerTextureCache.GetOrAdd((filePath, samplerName), static key =>
            {
                var tex = new XRTexture2D
                {
                    FilePath = key.path,
                    Name = Path.GetFileNameWithoutExtension(key.path),
                    SamplerName = key.samplerName,
                    MagFilter = ETexMagFilter.Linear,
                    MinFilter = ETexMinFilter.Linear,
                    UWrap = ETexWrapMode.Repeat,
                    VWrap = ETexWrapMode.Repeat,
                    AlphaAsTransparency = true,
                    AutoGenerateMipmaps = true,
                    Resizable = true,
                };

                try
                {
                    tex.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to assign filler texture for '{key.path}'. {ex.Message}");
                }

                XRTexture2D.ScheduleLoadJob(
                    key.path,
                    tex,
                    onFinished: _ =>
                    {
                        tex.MagFilter = ETexMagFilter.Linear;
                        tex.MinFilter = ETexMinFilter.Linear;
                        tex.UWrap = ETexWrapMode.Repeat;
                        tex.VWrap = ETexWrapMode.Repeat;
                        tex.AlphaAsTransparency = true;
                        tex.AutoGenerateMipmaps = true;
                        tex.Resizable = false;
                        tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                    },
                    onError: ex => Debug.LogException(ex, $"Uber sampler texture import job failed for '{key.path}'."));

                return tex;
            });
        }

        public static void MakeMaterialDeferred(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            Debug.Out($"[MakeMaterialDeferred] Material '{name}' has {textures.Count} texture slots, {textureList.Length} textures loaded");
            for (int i = 0; i < textures.Count; i++)
            {
                var slot = textures[i];
                var tex = i < textureList.Length ? textureList[i] : null;
                Debug.Out($"[MakeMaterialDeferred]   Slot[{i}]: Type={slot.TextureType}, Path='{slot.FilePath}', Loaded={(tex != null ? tex.Name : "NULL")}");
            }

            bool transp = textures.Any(x => (x.Flags & 0x2) != 0 || x.TextureType == TextureType.Opacity);
            bool hasNormal = textures.Any(x => x.TextureType == TextureType.Normals || x.TextureType == TextureType.Height);
            bool hasAnyTexture = textureList.Length > 0;

            int diffuseIndex = textures.FindIndex(x => x.TextureType == TextureType.Diffuse || x.TextureType == TextureType.BaseColor);
            Debug.Out($"[MakeMaterialDeferred] Initial diffuseIndex from Diffuse/BaseColor search: {diffuseIndex}");
            if (diffuseIndex < 0)
            {
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
                Debug.Out($"[MakeMaterialDeferred] Fallback diffuseIndex (first non-null): {diffuseIndex}");
            }
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = textures.FindIndex(x => x.TextureType == TextureType.Normals || x.TextureType == TextureType.Height);
            Debug.Out($"[MakeMaterialDeferred] normalIndex: {normalIndex}");

            XRTexture? diffuse = diffuseIndex >= 0 && diffuseIndex < textureList.Length ? textureList[diffuseIndex] : null;
            XRTexture? normal = normalIndex >= 0 && normalIndex < textureList.Length ? textureList[normalIndex] : null;
            Debug.Out($"[MakeMaterialDeferred] Selected diffuse: {diffuse?.Name ?? "NULL"} (index {diffuseIndex}), normal: {normal?.Name ?? "NULL"} (index {normalIndex})");

            mat.Shaders.Clear();

            if (hasAnyTexture)
            {
                if (transp || textureList.Any(x => x is not null && x.HasAlphaChannel))
                {
                    transp = true;
                    // Ensure Texture0 is the expected albedo for forward sampling.
                    mat.Textures = [diffuse];
                    mat.Shaders.Add(ShaderHelper.UnlitTextureFragForward()!);
                    mat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
                }
                else
                {
                    if (hasNormal && normal is not null)
                    {
                        // Deferred normal shader expects Texture0=albedo, Texture1=normal.
                        mat.Textures = [diffuse, normal];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalFragDeferred());
                    }
                    else
                    {
                        mat.Textures = [diffuse];
                        mat.Shaders.Add(ShaderHelper.LitTextureFragDeferred()!);
                    }
                    mat.Parameters =
                    [
                        new ShaderFloat(1.0f, "Opacity"),
                        new ShaderFloat(1.0f, "Specular"),
                        new ShaderFloat(0.9f, "Roughness"),
                        new ShaderFloat(0.0f, "Metallic"),
                        new ShaderFloat(1.0f, "IndexOfRefraction"),
                    ];
                    mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
                }
            }
            else
            {
                mat.Shaders.Add(ShaderHelper.LitColorFragDeferred()!);
                mat.Textures = [];
                mat.Parameters =
                [
                    new ShaderVector3(ColorF3.Magenta, "BaseColor"),
                    new ShaderFloat(1.0f, "Opacity"),
                    new ShaderFloat(1.0f, "Specular"),
                    new ShaderFloat(1.0f, "Roughness"),
                    new ShaderFloat(0.0f, "Metallic"),
                    new ShaderFloat(1.0f, "IndexOfRefraction"),
                ];
                mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
            }

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
                BlendModeAllDrawBuffers = transp ? BlendMode.EnabledTransparent() : BlendMode.Disabled(),
            };
        }

        public static void MakeMaterialForwardPlusTextured(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            bool transp = textures.Any(x => (x.Flags & 0x2) != 0 || x.TextureType == TextureType.Opacity);

            int diffuseIndex = textures.FindIndex(x => x.TextureType == TextureType.Diffuse || x.TextureType == TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = textures.FindIndex(x => x.TextureType == TextureType.Normals || x.TextureType == TextureType.Height);
            int specularIndex = textures.FindIndex(x => x.TextureType == TextureType.Specular || x.TextureType == TextureType.Shininess);
            int alphaMaskIndex = textures.FindIndex(x => x.TextureType == TextureType.Opacity);

            XRTexture? diffuse = diffuseIndex >= 0 && diffuseIndex < textureList.Length ? textureList[diffuseIndex] : null;
            XRTexture? normal = normalIndex >= 0 && normalIndex < textureList.Length ? textureList[normalIndex] : null;
            XRTexture? specular = specularIndex >= 0 && specularIndex < textureList.Length ? textureList[specularIndex] : null;
            XRTexture? alphaMask = alphaMaskIndex >= 0 && alphaMaskIndex < textureList.Length ? textureList[alphaMaskIndex] : null;

            bool hasNormal = normal is not null;
            bool hasSpecular = specular is not null;
            bool hasAlphaMask = alphaMask is not null;

            // Force a deterministic texture layout for the shader based on available maps:
            // With normal: Texture0=Albedo, Texture1=Normal, Texture2=Specular, Texture3=AlphaMask
            // Without normal: Texture0=Albedo, Texture1=Specular, Texture2=AlphaMask
            if (hasNormal && (hasSpecular || hasAlphaMask))
                mat.Textures = [diffuse, normal, specular, alphaMask];
            else if (!hasNormal && (hasSpecular || hasAlphaMask))
                mat.Textures = [diffuse, specular, alphaMask];
            else if (hasNormal)
                mat.Textures = [diffuse, normal];
            else
                mat.Textures = [diffuse];

            mat.Shaders.Clear();

            if (diffuse is not null)
            {
                // Select the appropriate shader based on available maps
                XRShader shader;
                if (hasNormal && (hasSpecular || hasAlphaMask))
                    shader = ShaderHelper.LitTextureNormalSpecAlphaFragForward();
                else if (!hasNormal && (hasSpecular || hasAlphaMask))
                    shader = ShaderHelper.LitTextureSpecAlphaFragForward();
                else if (hasNormal)
                    shader = ShaderHelper.LitTextureNormalFragForward();
                else
                    shader = ShaderHelper.LitTextureFragForward();

                mat.Shaders.Add(shader);
                mat.Parameters =
                [
                    new ShaderFloat(1.0f, "MatSpecularIntensity"),
                    new ShaderFloat(32.0f, "MatShininess"),
                    new ShaderFloat(0.5f, "AlphaCutoff"), // Default alpha cutoff threshold
                ];

                if (transp || diffuse.HasAlphaChannel || hasAlphaMask)
                {
                    transp = true;
                    mat.RenderPass = (int)EDefaultRenderPass.TransparentForward;
                }
                else
                {
                    mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                }
            }
            else
            {
                mat.Shaders.Add(ShaderHelper.LitColorFragForward());
                mat.Parameters =
                [
                    new ShaderVector4(new Vector4(1, 0, 1, 1), "MatColor"),
                    new ShaderFloat(1.0f, "MatSpecularIntensity"),
                    new ShaderFloat(32.0f, "MatShininess"),
                    new ShaderFloat(0.5f, "AlphaCutoff"),
                ];
                mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            }

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
                BlendModeAllDrawBuffers = transp ? BlendMode.EnabledTransparent() : BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights,
            };
        }

        public static void MakeMaterialForwardPlusUberShader(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            int diffuseIndex = textures.FindIndex(x => x.TextureType == TextureType.Diffuse || x.TextureType == TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = textures.FindIndex(x => x.TextureType == TextureType.Normals || x.TextureType == TextureType.Height);

            XRTexture? diffuseSrc = diffuseIndex >= 0 && diffuseIndex < textureList.Length ? textureList[diffuseIndex] : null;
            XRTexture? normalSrc = normalIndex >= 0 && normalIndex < textureList.Length ? textureList[normalIndex] : null;

            string? diffusePath = (diffuseSrc as XRTexture2D)?.FilePath;
            string? normalPath = (normalSrc as XRTexture2D)?.FilePath;

            // Always bind something for both samplers so the Uber shader has valid bindings.
            XRTexture2D? main = diffusePath is not null ? GetOrCreateUberSamplerTexture(diffusePath, "_MainTex") : null;
            XRTexture2D? bump = null;
            float bumpScale = 0.0f;
            if (normalPath is not null)
            {
                bump = GetOrCreateUberSamplerTexture(normalPath, "_BumpMap");
                bumpScale = 1.0f;
            }
            else if (diffusePath is not null)
            {
                bump = GetOrCreateUberSamplerTexture(diffusePath, "_BumpMap");
            }

            mat.Textures = [main, bump];

            XRShader vert = ShaderHelper.LoadEngineShader(Path.Combine("Uber", "UberShader.vert"));
            XRShader frag = ShaderHelper.LoadEngineShader(Path.Combine("Uber", "UberShader.frag"));

            mat.Shaders.Clear();
            mat.Shaders.Add(vert);
            mat.Shaders.Add(frag);

            mat.Parameters =
            [
                new ShaderVector4(new Vector4(1, 1, 1, 1), "_Color"),
                new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
                new ShaderVector2(Vector2.Zero, "_MainTexPan"),
                new ShaderInt(0, "_MainTexUV"),

                new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
                new ShaderVector2(Vector2.Zero, "_BumpMapPan"),
                new ShaderInt(0, "_BumpMapUV"),
                new ShaderFloat(bumpScale, "_BumpScale"),

                new ShaderFloat(1.0f, "_ShadingEnabled"),
                new ShaderInt(6, "_LightingMode"),
                new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
                new ShaderFloat(1.0f, "_ShadowStrength"),
                new ShaderFloat(0.0f, "_LightingMinLightBrightness"),
                new ShaderFloat(0.0f, "_LightingMonochromatic"),
                new ShaderFloat(0.0f, "_LightingCapEnabled"),
                new ShaderFloat(10.0f, "_LightingCap"),

                new ShaderInt(0, "_MainAlphaMaskMode"),
                new ShaderFloat(0.0f, "_AlphaMod"),
                new ShaderFloat(1.0f, "_AlphaForceOpaque"),
                new ShaderFloat(0.5f, "_Cutoff"),
                new ShaderInt(0, "_Mode"),
            ];

            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
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
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights,
            };
        }

        private readonly List<Func<IEnumerable>> _meshProcessRoutines = [];
        private readonly List<Action> _meshFinalizeActions = [];
        private readonly List<NodeTransformInfo> _nodeTransforms = [];

        public unsafe SceneNode? Import(
            PostProcessSteps options = PostProcessSteps.None,
            bool preservePivots = true,
            bool removeAssimpFBXNodes = true,
            float scaleConversion = 1.0f,
            bool zUp = false,
            bool multiThread = true,
            bool? processMeshesAsynchronously = null,
            CancellationToken cancellationToken = default,
            Action<float>? onProgress = null)
        {
            Debug.Out($"[ModelImporter.Import] Starting import of: {SourceFilePath}");
            Debug.Out($"[ModelImporter.Import] processMeshesAsynchronously param: {processMeshesAsynchronously}");

            SetAssimpConfig(preservePivots, scaleConversion, zUp, multiThread);

            AScene scene;
            using (Engine.Profiler.Start($"Assimp ImportFile: {SourceFilePath} with options: {options}"))
            {
                Debug.Out($"[ModelImporter.Import] Calling Assimp ImportFile...");
                scene = _assimp.ImportFile(SourceFilePath, options);
            }

            if (scene is null || scene.SceneFlags == SceneFlags.Incomplete || scene.RootNode is null)
            {
                Debug.Out($"[ModelImporter.Import] Assimp returned null/incomplete scene! scene={scene != null}, flags={scene?.SceneFlags}, rootNode={scene?.RootNode != null}");
                return null;
            }

            Debug.Out($"[ModelImporter.Import] Assimp loaded: {scene.MeshCount} meshes, {scene.MaterialCount} materials, {scene.RootNode.ChildCount} root children");

            cancellationToken.ThrowIfCancellationRequested();

            _meshProcessRoutines.Clear();
            _meshFinalizeActions.Clear();
            bool processMeshesAsync = processMeshesAsynchronously ?? Engine.Rendering.Settings.ProcessMeshImportsAsynchronously;
            Debug.Out($"[ModelImporter.Import] processMeshesAsync resolved to: {processMeshesAsync}");

            SceneNode rootNode;
            using (Engine.Profiler.Start($"Assemble model hierarchy"))
            {
                Debug.Out($"[ModelImporter.Import] Creating scene node hierarchy...");
                rootNode = new(Path.GetFileNameWithoutExtension(SourceFilePath)) { Layer = _importLayer };
                _nodeTransforms.Clear();
                ProcessNode(true, scene.RootNode, scene, rootNode, null, Matrix4x4.Identity, removeAssimpFBXNodes, null, cancellationToken);
                Debug.Out($"[ModelImporter.Import] ProcessNode complete, {_nodeTransforms.Count} nodes processed");
                NormalizeNodeScales(scene, rootNode, cancellationToken, processMeshesAsync);
                Debug.Out($"[ModelImporter.Import] NormalizeNodeScales complete, {_meshProcessRoutines.Count} mesh routines queued");
            }

            Debug.Out($"[ModelImporter.Import] Starting mesh processing (async={processMeshesAsync})...");
            void meshProcessAction() => ProcessMeshesOnJobThread(onProgress, cancellationToken);
            RunMeshProcessing(meshProcessAction, processMeshesAsync, cancellationToken);
            Debug.Out($"[ModelImporter.Import] RunMeshProcessing returned, returning rootNode: '{rootNode.Name}'");

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
            _assimp.XAxisRotation = -rotate;
            _assimp.ZAxisRotation = 180.0f;
        }

        private void ProcessMeshesOnJobThread(Action<float>? reportProgress, CancellationToken cancellationToken)
        {
            using var t = Engine.Profiler.Start("Processing meshes on job thread");
            int total = _meshProcessRoutines.Count;
            for (int i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var step in _meshProcessRoutines[i]())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (step is Task task)
                        task.GetAwaiter().GetResult();
                }
                if (reportProgress != null && total > 0)
                    reportProgress((i + 1) / (float)total);
            }
        }

        private void RunMeshProcessing(Action meshProcessAction, bool processAsynchronously, CancellationToken cancellationToken)
        {
            Debug.Out($"[ModelImporter] RunMeshProcessing: async={processAsynchronously}, routines={_meshProcessRoutines.Count}");

            if (!processAsynchronously)
            {
                Debug.Out($"[ModelImporter] Running mesh processing synchronously...");
                meshProcessAction();
                Debug.Out($"[ModelImporter] Sync mesh processing complete, invoking _onCompleted");
                _onCompleted?.Invoke();
                return;
            }

            // Schedule one job per mesh (each action is one mesh).
            // This avoids a single long-running job and lets other jobs interleave.
            int total = _meshProcessRoutines.Count;
            if (total <= 0)
            {
                Debug.Out($"[ModelImporter] No mesh routines to process, invoking _onCompleted");
                _onCompleted?.Invoke();
                return;
            }
            Debug.Out($"[ModelImporter] Scheduling {total} mesh jobs asynchronously...");

            int remaining = total;
            int faulted = 0;
            int canceled = 0;
            void TryFinalize()
            {
                if (Interlocked.Decrement(ref remaining) != 0)
                    return;

                if (Volatile.Read(ref faulted) != 0)
                    return;
                if (Volatile.Read(ref canceled) != 0)
                    return;

                // Flush pending model mutations in swap once all mesh jobs are done.
                //Engine.EnqueueSwapTask(() =>
                //{
                    try
                    {
                        foreach (var finalize in _meshFinalizeActions)
                            finalize();
                        _onCompleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, $"Mesh finalize failed for '{SourceFilePath}'.");
                    }
                //});
            }

            for (int i = 0; i < total; i++)
            {
                var routineFactory = _meshProcessRoutines[i];

                Engine.Jobs.Schedule(
                    routineFactory,
                    completed: () =>
                    {
                        TryFinalize();
                    },
                    error: ex =>
                    {
                        Interlocked.Exchange(ref faulted, 1);
                        Debug.LogException(ex, $"Mesh processing job failed for '{SourceFilePath}'.");
                        TryFinalize();
                    },
                    canceled: () =>
                    {
                        Interlocked.Exchange(ref canceled, 1);
                        TryFinalize();
                    },
                    cancellationToken: cancellationToken);
            }
        }

        private void NormalizeNodeScales(AScene scene, SceneNode rootNode, CancellationToken cancellationToken, bool scheduleOneJobPerMesh)
        {
            using var t = Engine.Profiler.Start("Normalizing node scales");

            foreach (var nodeInfo in _nodeTransforms)
            {
            cancellationToken.ThrowIfCancellationRequested();
                nodeInfo.OriginalWorldMatrix = nodeInfo.SceneNode.Transform.WorldMatrix;

                var transform = nodeInfo.SceneNode.GetTransformAs<Transform>(false)!;

                // Store the original world position
                Vector3 originalWorldPos = nodeInfo.SceneNode.Transform.WorldMatrix.Translation;

                // Create a new local matrix with unit scale but same rotation
                // Extract rotation from original local matrix
                Matrix4x4.Decompose(transform.LocalMatrix, out _, out Quaternion rotation, out Vector3 translation);

                // Create new local matrix with unit scale
                Matrix4x4 normalizedLocalMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);

                // Apply the new local matrix
                transform.DeriveLocalMatrix(normalizedLocalMatrix);
                transform.RecalculateMatrices(true, false);

                // Calculate the position difference caused by scale removal
                Vector3 newWorldPos = nodeInfo.SceneNode.Transform.WorldMatrix.Translation;
                Vector3 positionOffset = originalWorldPos - newWorldPos;

                // Adjust the local translation to compensate for scale removal
                if (positionOffset != Vector3.Zero)
                {
                    // For position offsets, use the full inverse transform
                    // Then set the local translation directly
                    transform.Translation = transform.Parent is null ? originalWorldPos : Vector3.Transform(originalWorldPos, transform.Parent.InverseWorldMatrix);
                    transform.RecalculateMatrices(true, false);
                }

                transform.SaveBindState();
            }

            //Create mesh process actions and create a transform to move vertices to model root space
            foreach (var nodeInfo in _nodeTransforms)
            {
                if (nodeInfo.MeshIndices.Count <= 0)
                    continue;
                
                var rootTransform = rootNode.Transform;

                //var tfm = nodeInfo.SceneNode.Transform;
                //Vector3 translation = tfm.WorldTranslation;
                //Vector3 scale = tfm.LossyWorldScale;
                //Quaternion rotation = tfm.WorldRotation;
                //Debug.Out($"Processing node {nodeInfo.AssimpNode.Name} with world T[{translation}] R[{rotation}] S[{scale}]");

                Matrix4x4 geometryTransform = nodeInfo.OriginalWorldMatrix * rootTransform.InverseWorldMatrix;
                EnqueueProcessMeshes(nodeInfo.AssimpNode, scene, nodeInfo.SceneNode, geometryTransform, rootTransform, cancellationToken, scheduleOneJobPerMesh);
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
            Matrix4x4? fbxMatrixParent = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    fbxMatrix,
                    cancellationToken);
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
                        tfm.RecalculateMatrices(true, false);
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
            sceneNode.Layer = _importLayer;
            var tfm = sceneNode.GetTransformAs<Transform>(true)!;
            tfm.DeriveLocalMatrix(localTransform);
            tfm.RecalculateMatrices(true, false);
            tfm.SaveBindState();

            if (_nodeCache.TryGetValue(name, out List<SceneNode>? nodes))
                nodes.Add(sceneNode);
            else
                _nodeCache.Add(name, [sceneNode]);

            return sceneNode;
        }

        private unsafe void EnqueueProcessMeshes(Node node, AScene scene, SceneNode sceneNode, Matrix4x4 dataTransform, TransformBase rootTransform, CancellationToken cancellationToken, bool marshalSubMeshAddsToMainThread)
        {
            int count = node.MeshCount;
            if (count == 0)
                return;

            // Create the model/component once per node, then schedule a separate mesh action for each mesh index.
            ModelComponent modelComponent = sceneNode.AddComponent<ModelComponent>()!;
            Model model = new();
            model.Meshes.ThreadSafe = true;
            modelComponent.Name = node.Name;
            modelComponent.Model = model;

            // For async-per-mesh processing, collect submeshes then flush once on main thread.
            // This avoids per-mesh main-thread waits (which can deadlock if the main thread is blocked).
            ConcurrentDictionary<int, SubMesh>? pending = marshalSubMeshAddsToMainThread
                ? new ConcurrentDictionary<int, SubMesh>()
                : null;

            if (pending != null)
            {
                int[] ordered = new int[count];
                for (int i = 0; i < count; i++)
                    ordered[i] = node.MeshIndices[i];

                _meshFinalizeActions.Add(() =>
                {
                    for (int i = 0; i < ordered.Length; i++)
                        if (pending.TryGetValue(ordered[i], out var subMesh))
                            model.Meshes.Add(subMesh);
                });
            }

            for (var i = 0; i < count; i++)
            {
                int localMeshIndex = node.MeshIndices[i];
                _meshProcessRoutines.Add(() => MeshRoutine(localMeshIndex));
            }

            IEnumerable MeshRoutine(int meshIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Debug.Out($"[ModelImporter] MeshRoutine started for mesh index {meshIndex}");
                Mesh mesh = scene.Meshes[meshIndex];
                Debug.Out($"[ModelImporter] Processing mesh: '{mesh.Name}' (verts={mesh.VertexCount}, faces={mesh.FaceCount})");
                (XRMesh xrMesh, XRMaterial xrMaterial) = ProcessSubMesh(mesh, scene, dataTransform, cancellationToken);
                _meshes.Add(xrMesh);
                _materials.Add(xrMaterial);

                SubMesh subMesh = new(xrMesh, xrMaterial)
                {
                    Name = mesh.Name,
                    RootTransform = rootTransform
                };

                if (pending != null)
                {
                    Debug.Out($"[ModelImporter] Adding mesh '{mesh.Name}' to pending dict (deferred add)");
                    pending[meshIndex] = subMesh;
                }
                else
                {
                    Debug.Out($"[ModelImporter] Adding mesh '{mesh.Name}' directly to model");
                    model.Meshes.Add(subMesh);
                }

                Debug.Out($"[ModelImporter] MeshRoutine completed for '{mesh.Name}'");
                yield break;
            }
        }

        private unsafe (XRMesh mesh, XRMaterial material) ProcessSubMesh(
            Mesh mesh,
            AScene scene,
            Matrix4x4 dataTransform,
            CancellationToken cancellationToken)
        {
            using var t = Engine.Profiler.Start($"Processing submesh for {mesh.Name}");

            cancellationToken.ThrowIfCancellationRequested();

            var xrMesh = new XRMesh(mesh, _assimp, _nodeCache, dataTransform);
            cancellationToken.ThrowIfCancellationRequested();
            var xrMaterial = ProcessMaterial(mesh, scene, cancellationToken);
            return (xrMesh, xrMaterial);
        }

        private unsafe XRMaterial ProcessMaterial(Mesh mesh, AScene scene, CancellationToken cancellationToken)
        {
            using var t = Engine.Profiler.Start($"Processing material for {mesh.Name}");

            cancellationToken.ThrowIfCancellationRequested();

            Material matInfo = scene.Materials[mesh.MaterialIndex];
            List<TextureSlot> textures = [];
            for (int i = 0; i < 22; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                string keyPath = path?.ToLowerInvariant() ?? string.Empty;
                var key = (type, keyPath);
                if (_textureInfoCache.TryGetValue(key, out var cached))
                {
                    textures.Add(cached);
                }
                else
                {
                    textures.Add(slot);
                    _textureInfoCache.TryAdd(key, slot);
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