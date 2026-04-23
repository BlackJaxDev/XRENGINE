using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using XREngine.Extensions;
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
using XREngine.Fbx;
using XREngine.Gltf;
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
        private sealed class ImportOptionsScope(ModelImportOptions? previous) : IDisposable
        {
            private readonly ModelImportOptions? _previous = previous;

            public void Dispose() => _currentImportOptions.Value = _previous;

            public static ImportOptionsScope Push(ModelImportOptions? options)
            {
                ModelImportOptions? previous = _currentImportOptions.Value;
                _currentImportOptions.Value = options;
                return new ImportOptionsScope(previous);
            }
        }

        private sealed class ImportSourceScope(string? previousSourceFilePath) : IDisposable
        {
            private readonly string? _previousSourceFilePath = previousSourceFilePath;

            public void Dispose() => _currentImportSourceFilePath.Value = _previousSourceFilePath;

            public static ImportSourceScope Push(string? sourceFilePath)
            {
                string? previousSourceFilePath = _currentImportSourceFilePath.Value;
                _currentImportSourceFilePath.Value = sourceFilePath;
                return new ImportSourceScope(previousSourceFilePath);
            }
        }

        private static readonly AsyncLocal<ModelImportOptions?> _currentImportOptions = new();
        private static readonly AsyncLocal<string?> _currentImportSourceFilePath = new();

        public readonly record struct ModelImporterResult(
            SceneNode? RootNode,
            IReadOnlyCollection<XRMaterial> Materials,
            IReadOnlyCollection<XRMesh> Meshes);

        public delegate XRMaterial DelMakeMaterialAction(XRTexture[] textureList, List<TextureSlot> textures, string name);

        public DelMakeMaterialAction MakeMaterialAction { get; set; } = MakeMaterialDefault;
        public ModelImportOptions? ImportOptions { get; set; }

        public ModelImporter(string path, Action? onCompleted, DelMaterialFactory? materialFactory)
        {
            _assimp = new AssimpContext();
            _path = path;
            _onCompleted = onCompleted;
            _materialFactory = materialFactory ?? MaterialFactory;
        }

        private static bool HasTransparentBlendHint(List<TextureSlot> textures)
            => textures.Any(x => (x.Flags & 0x2) != 0);

        private static bool HasOpacityMask(List<TextureSlot> textures)
            => textures.Any(x => GetEffectiveTextureType(x) == TextureType.Opacity);

        /// <summary>
        /// Schedules an import job on the engine's job system.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="options"></param>
        /// <param name="onFinished"></param>
        /// <param name="onError"></param>
        /// <param name="onCanceled"></param>
        /// <param name="onProgress"></param>
        /// <param name="cancellationToken"></param>
        /// <param name="parent"></param>
        /// <param name="scaleConversion"></param>
        /// <param name="zUp"></param>
        /// <param name="rootTransformMatrix"></param>
        /// <param name="materialFactory"></param>
        /// <param name="makeMaterialAction"></param>
        /// <param name="layer"></param>
        /// <returns></returns>
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
            Matrix4x4? rootTransformMatrix = null,
            DelMaterialFactory? materialFactory = null,
            DelMakeMaterialAction? makeMaterialAction = null,
            ModelImportOptions? importOptions = null,
            bool? batchSubmeshAddsDuringAsyncImport = null,
            int layer = DefaultLayers.DynamicIndex)
        {
            IEnumerable ImportRoutine()
            {
                // Run on the job system thread directly (no Task.Run). The job system already executes
                // this enumerator on a worker thread.
                var result = ImportInternal(path, options, parent, scaleConversion, zUp, rootTransformMatrix, onFinished, materialFactory, makeMaterialAction, importOptions, onProgress, cancellationToken, batchSubmeshAddsDuringAsyncImport, layer);
                yield return new JobProgress(1f, result);
            }

            var job = Engine.Jobs.Schedule(
                ImportRoutine,
                progress: onProgress,
                completed: null,
                error: ex =>
                {
                    onError?.Invoke(ex);
                },
                canceled: () =>
                {
                    onCanceled?.Invoke();
                },
                progressWithPayload: null,
                cancellationToken: cancellationToken);
            return job;
        }

        private readonly ConcurrentDictionary<string, XRTexture2D> _texturePathCache = new();
        private readonly ConcurrentDictionary<string, string> _recursiveTextureSearchCache = new(StringComparer.OrdinalIgnoreCase);

        private XRMaterial MakeMaterialInternal(XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            using var _ = ImportOptionsScope.Push(_currentImportOptions.Value ?? ImportOptions);
            return MakeMaterialAction(textureList, textures, name);
        }

        private static XRTexture? GetDiffuseTexture(XRTexture[] textureList, List<TextureSlot> textures)
        {
            int diffuseIndex = ResolveTextureIndex(textures, TextureType.Diffuse, TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0 || diffuseIndex >= textureList.Length)
                return null;
            return textureList[diffuseIndex];
        }

        private static bool IsTransparentLike(ETransparencyMode mode)
            => mode is not ETransparencyMode.Opaque and not ETransparencyMode.Masked and not ETransparencyMode.AlphaToCoverage;

        public static ETransparencyMode ResolveTransparencyMode(XRTexture[] textureList, List<TextureSlot> textures)
        {
            ModelImportOptions? options = _currentImportOptions.Value;
            bool hasTransparentBlendHint = HasTransparentBlendHint(textures);
            bool hasOpacityMask = HasOpacityMask(textures);
            bool hasDiffuseAlpha = hasTransparentBlendHint || (GetDiffuseTexture(textureList, textures)?.HasAlphaChannel ?? false);

            if (hasOpacityMask)
            {
                return (options?.OpacityMapMode ?? EOpacityMapMode.Auto) switch
                {
                    EOpacityMapMode.Blended => ETransparencyMode.WeightedBlendedOit,
                    _ => ETransparencyMode.Masked,
                };
            }

            if (hasDiffuseAlpha)
            {
                return (options?.DiffuseAlphaMode ?? EDiffuseAlphaMode.Auto) switch
                {
                    EDiffuseAlphaMode.Opaque => ETransparencyMode.Opaque,
                    EDiffuseAlphaMode.Masked => ETransparencyMode.Masked,
                    EDiffuseAlphaMode.Blended => ETransparencyMode.WeightedBlendedOit,
                    _ => hasTransparentBlendHint ? ETransparencyMode.WeightedBlendedOit : ETransparencyMode.Opaque,
                };
            }

            return ETransparencyMode.Opaque;
        }

        public static void ConfigureImportedTransparency(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures)
        {
            ETransparencyMode mode = ResolveTransparencyMode(textureList, textures);
            bool needsAlphaCutoff = mode is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage;

            if (needsAlphaCutoff && mat.Parameter<ShaderFloat>("AlphaCutoff") is null)
            {
                var parameters = mat.Parameters ?? [];
                Array.Resize(ref parameters, parameters.Length + 1);
                parameters[^1] = new ShaderFloat(mat.AlphaCutoff, "AlphaCutoff");
                mat.Parameters = parameters;
            }

            mat.TransparencyMode = mode;
        }

        public static XRMaterial MakeMaterialDefault(XRTexture[] textureList, List<TextureSlot> textures, string name)
            => MakeMaterialDeferred(textureList, textures, name);

        public XRMaterial MaterialFactory(
            string modelFilePath,
            string name,
            List<TextureSlot> textures,
            TextureFlags flags,
            ShadingMode mode,
            Dictionary<string, List<MaterialProperty>> properties)
        {
            XRTexture[] textureList = textures.Count > 0 ? LoadTextures(modelFilePath, textures) : [];
            return MakeMaterialInternal(textureList, textures, name);
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
            if (textureList is null || textureList.Length == 0)
                return;

            // Ensure the material has enough texture slots to assign into.
            while (mat.Textures.Count < textureList.Length)
                mat.Textures.Add(null);

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

            path = ResolveTextureFilePath(modelFilePath, path, out bool textureExists);
            textureList[i] = textureExists
                ? _texturePathCache.GetOrAdd(path, MakeTextureAction)
                : _texturePathCache.GetOrAdd(path, static missingPath => TextureFactoryInternal(missingPath, schedulePreviewLoad: false));
        }

        private readonly ConcurrentDictionary<string, bool> _missingTexturePathWarnings = new();
        private readonly ConcurrentDictionary<string, bool> _invalidTextureSearchPathWarnings = new(StringComparer.OrdinalIgnoreCase);

        private string? TryResolveTextureFromConfiguredSearchPaths(string modelFilePath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            if (_recursiveTextureSearchCache.TryGetValue(fileName, out string? cachedPath))
                return string.IsNullOrWhiteSpace(cachedPath) ? null : cachedPath;

            ModelImportOptions? options = _currentImportOptions.Value ?? ImportOptions;
            string[] searchPaths = options?.TextureLoadDirSearchPaths ?? [];
            if (searchPaths.Length == 0)
            {
                _recursiveTextureSearchCache.TryAdd(fileName, string.Empty);
                return null;
            }

            string? modelDir = Path.GetDirectoryName(modelFilePath);
            foreach (string rawSearchPath in searchPaths)
            {
                if (string.IsNullOrWhiteSpace(rawSearchPath))
                    continue;

                string searchPath = rawSearchPath;
                if (!Path.IsPathRooted(searchPath) && !string.IsNullOrWhiteSpace(modelDir))
                    searchPath = Path.Combine(modelDir, searchPath);

                try
                {
                    searchPath = Path.GetFullPath(searchPath);
                }
                catch (Exception ex)
                {
                    if (_invalidTextureSearchPathWarnings.TryAdd(rawSearchPath, true))
                        LogImportExpectedWarning(modelFilePath, $"[ModelImporter] Ignoring invalid texture search path '{rawSearchPath}'. {ex.Message}");
                    continue;
                }

                if (!Directory.Exists(searchPath))
                {
                    if (_invalidTextureSearchPathWarnings.TryAdd(searchPath, true))
                        LogImportExpectedWarning(modelFilePath, $"[ModelImporter] Texture search path does not exist: '{searchPath}'");
                    continue;
                }

                try
                {
                    string? match = Directory.EnumerateFiles(searchPath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        string resolvedMatch = Path.GetFullPath(match);
                        _recursiveTextureSearchCache[fileName] = resolvedMatch;
                        return resolvedMatch;
                    }
                }
                catch (Exception ex)
                {
                    if (_invalidTextureSearchPathWarnings.TryAdd($"{searchPath}|scan", true))
                        LogImportExpectedWarning(modelFilePath, $"[ModelImporter] Failed to scan texture search path '{searchPath}'. {ex.Message}");
                }
            }

            _recursiveTextureSearchCache.TryAdd(fileName, string.Empty);
            return null;
        }

        private string ResolveTextureFilePath(string modelFilePath, string rawPath, out bool exists)
        {
            static string Normalize(string p)
            {
                p = p.Trim().Trim('"');
                p = p.Replace("/", "\\");
                while (p.StartsWith(".\\", StringComparison.Ordinal) || p.StartsWith("./", StringComparison.Ordinal))
                    p = p[2..];
                return p;
            }

            static string? TryExtractRelativeTexturePath(string path)
            {
                int texturesIndex = path.LastIndexOf("\\textures\\", StringComparison.OrdinalIgnoreCase);
                if (texturesIndex >= 0)
                    return path[(texturesIndex + 1)..];

                string fileName = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
            }

            static string CanonicalizePath(string path)
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch
                {
                    return path;
                }
            }

            string normalized = Normalize(rawPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                exists = false;
                return rawPath;
            }

            string? modelDir = Path.GetDirectoryName(modelFilePath);

            // 1) If rooted, try it directly.
            if (Path.IsPathRooted(normalized))
            {
                if (File.Exists(normalized))
                {
                    exists = true;
                    return normalized;
                }

                if (!string.IsNullOrWhiteSpace(modelDir))
                {
                    string? relativeTexturePath = TryExtractRelativeTexturePath(normalized);
                    if (!string.IsNullOrWhiteSpace(relativeTexturePath))
                    {
                        string rootedRelativeCandidate = Path.Combine(modelDir, relativeTexturePath);
                        if (File.Exists(rootedRelativeCandidate))
                        {
                            exists = true;
                            return rootedRelativeCandidate;
                        }

                        string rootedTexturesCandidate = Path.Combine(modelDir, "textures", Path.GetFileName(relativeTexturePath));
                        if (File.Exists(rootedTexturesCandidate))
                        {
                            exists = true;
                            return rootedTexturesCandidate;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(modelDir))
            {
                exists = false;
                return normalized;
            }

            // 2) Relative to model file.
            string candidate = Path.Combine(modelDir, normalized);
            if (File.Exists(candidate))
            {
                exists = true;
                return candidate;
            }

            // 3) Common OBJ/MTL pattern: everything lives under a "textures" folder.
            string fileName = Path.GetFileName(normalized);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                string texturesDirCandidate = Path.Combine(modelDir, "textures", fileName);
                if (File.Exists(texturesDirCandidate))
                {
                    exists = true;
                    return texturesDirCandidate;
                }

                string modelDirCandidate = Path.Combine(modelDir, fileName);
                if (File.Exists(modelDirCandidate))
                {
                    exists = true;
                    return modelDirCandidate;
                }

                string? recursiveSearchCandidate = TryResolveTextureFromConfiguredSearchPaths(modelFilePath, fileName);
                if (!string.IsNullOrWhiteSpace(recursiveSearchCandidate) && File.Exists(recursiveSearchCandidate))
                {
                    exists = true;
                    return recursiveSearchCandidate;
                }
            }

            candidate = CanonicalizePath(candidate);

            if (_missingTexturePathWarnings.TryAdd(candidate, true))
                LogImportExpectedWarning(modelFilePath, $"[ModelImporter] Texture path not found: '{candidate}' (from '{rawPath}', model='{modelFilePath}')");

            exists = false;
            return candidate;
        }

        public Func<string, XRTexture2D> MakeTextureAction { get; set; } = TextureFactoryInternal;

        private static XRTexture2D TextureFactoryInternal(string path)
            => TextureFactoryInternal(path, schedulePreviewLoad: false);

        private static XRTexture2D TextureFactoryInternal(string path, bool schedulePreviewLoad)
        {
            string textureName = Path.GetFileNameWithoutExtension(path);
            XRTexture2D placeholder = new()
            {
                Name = textureName,
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.Repeat,
                VWrap = ETexWrapMode.Repeat,
                AlphaAsTransparency = true,
                AutoGenerateMipmaps = false,
                Resizable = false,
                FilePath = path
            };

            try
            {
                placeholder.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
            }
            catch (Exception ex)
            {
                LogImportWarning($"Failed to assign filler texture for '{path}'. {ex.Message}");
            }

            if (schedulePreviewLoad)
            {
                XRTexture2D.ScheduleImportedTexturePreviewJob(
                    path,
                    placeholder,
                    onFinished: tex =>
                    {
                        tex.MagFilter = ETexMagFilter.Linear;
                        tex.UWrap = ETexWrapMode.Repeat;
                        tex.VWrap = ETexWrapMode.Repeat;
                        tex.AlphaAsTransparency = true;
                        tex.Resizable = false;
                        tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
                    },
                    onError: ex => LogImportException(ex, $"[TextureFactory] Texture import job FAILED for '{path}'."),
                    priority: JobPriority.Low);
            }
            else
            {
                XRTexture2D.RegisterImportedTextureStreamingPlaceholder(path, placeholder);
            }

            return placeholder;
        }

        public string SourceFilePath => _path;

        private static bool IsFbxPath(string? path)
            => !string.IsNullOrWhiteSpace(path)
                && Path.GetExtension(path).Equals(".fbx", StringComparison.OrdinalIgnoreCase);

        private static void LogImportDiagnostic(string message, params object[] args)
            => LogImportDiagnostic(_currentImportSourceFilePath.Value, message, args);

        private static void LogImportDiagnostic(string? sourceFilePath, string message, params object[] args)
        {
            if (IsFbxPath(sourceFilePath))
                Debug.Assets(message, args);
            else
                Debug.Out(message, args);
        }

        private static void LogImportWarning(string message, params object[] args)
            => LogImportWarning(_currentImportSourceFilePath.Value, message, args);

        private static void LogImportExpectedWarning(string message, params object[] args)
            => LogImportExpectedWarning(_currentImportSourceFilePath.Value, message, args);

        private static void LogImportWarning(string? sourceFilePath, string message, params object[] args)
        {
            if (IsFbxPath(sourceFilePath))
                Debug.AssetsWarning(message, args);
            else
            {
                if (args.Length > 0)
                    message = string.Format(message, args);
                Debug.LogWarning(message);
            }
        }

        private static void LogImportExpectedWarning(string? sourceFilePath, string message, params object[] args)
        {
            if (args.Length > 0)
                message = string.Format(message, args);

            if (IsFbxPath(sourceFilePath))
                Debug.Log(ELogCategory.Assets, EOutputVerbosity.Normal, false, $"[WARN] {message}");
            else
                Debug.Log(ELogCategory.General, EOutputVerbosity.Normal, false, $"[WARN] {message}");
        }

        private static void LogImportException(Exception ex, string? message = null)
        {
            if (IsFbxPath(_currentImportSourceFilePath.Value))
                Debug.AssetsException(ex, message);
            else
                Debug.LogException(ex, message);
        }

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
        private readonly ConcurrentDictionary<int, Lazy<XRMaterial>> _materialCacheByIndex = [];

        private readonly ConcurrentBag<XRMesh> _meshes = [];
        private readonly ConcurrentBag<XRMaterial> _materials = [];

        /// <summary>
        /// The layer index to assign to all imported SceneNodes (e.g., DefaultLayers.StaticIndex for static meshes).
        /// </summary>
        private int _importLayer = DefaultLayers.DynamicIndex;

        // Track transform state per published runtime node so collapsed Assimp helper nodes
        // do not enqueue duplicate mesh publication work for the same scene node.
        private class NodeTransformInfo(SceneNode sceneNode, Vector3 scale, List<int> meshIndices)
        {
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
            bool zUp = false,
            bool? processMeshesAsynchronously = null,
            bool? batchSubmeshAddsDuringAsyncImport = null)
        {
            using var importer = new ModelImporter(path, onCompleted, materialFactory);
            var node = importer.Import(options, true, true, scaleConversion, zUp, true, processMeshesAsynchronously, batchSubmeshAddsDuringAsyncImport);
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
            bool? batchSubmeshAddsDuringAsyncImport = null,
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
                makeMaterialAction: null,
                batchSubmeshAddsDuringAsyncImport: batchSubmeshAddsDuringAsyncImport);

            return tcs.Task;
        }

        private static ModelImporterResult ImportInternal(
            string path,
            PostProcessSteps options,
            SceneNode? parent,
            float scaleConversion,
            bool zUp,
            Matrix4x4? rootTransformMatrix,
            Action<ModelImporterResult>? onFinished,
            DelMaterialFactory? materialFactory,
            DelMakeMaterialAction? makeMaterialAction,
            ModelImportOptions? importOptions,
            Action<float>? onProgress,
            CancellationToken cancellationToken,
            bool? batchSubmeshAddsDuringAsyncImport = null,
            int layer = DefaultLayers.DynamicIndex)
        {
            using var importer = new ModelImporter(path, onCompleted: null, materialFactory);
            if (makeMaterialAction is not null)
                importer.MakeMaterialAction = makeMaterialAction;
            importer.ImportOptions = importOptions;
            importer._importLayer = layer;

            // Let mesh processing mode follow the engine setting unless an explicit override is passed at a higher level.
            // This avoids long synchronous import bursts that starve the job system during startup/world load.
            var node = importer.Import(options, true, true, scaleConversion, zUp, true, null, batchSubmeshAddsDuringAsyncImport, cancellationToken, onProgress, rootTransformMatrix);

            cancellationToken.ThrowIfCancellationRequested();

            // Add to parent using deferred mode - this queues the assignment to be processed
            // during PostUpdate, avoiding any blocking on the render thread
            if (parent != null && node != null)
                parent.Transform.AddChild(node.Transform, false, EParentAssignmentMode.Deferred);

            var result = new ModelImporterResult(node, importer._materials, importer._meshes);
            onFinished?.Invoke(result);
            return result;
        }

        private static readonly ConcurrentDictionary<(string path, string samplerName), XRTexture2D> _uberSamplerTextureCache = new();
        private static readonly ConcurrentDictionary<string, XRTexture2D> _uberDefaultSamplerTextureCache = new();

        public static XRTexture2D GetOrCreateUberSamplerTexture(string filePath, string samplerName)
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
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                };

                try
                {
                    tex.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
                }
                catch (Exception ex)
                {
                    LogImportWarning($"Failed to assign filler texture for '{key.path}'. {ex.Message}");
                }

                XRTexture2D.RegisterImportedTextureStreamingPlaceholder(key.path, tex);

                return tex;
            });
        }

        private static XRTexture2D GetOrCreateDefaultUberSamplerTexture(string samplerName, ColorF4 color)
        {
            return _uberDefaultSamplerTextureCache.GetOrAdd(samplerName, key =>
            {
                return new XRTexture2D(1u, 1u, color)
                {
                    Name = key,
                    SamplerName = key,
                    MagFilter = ETexMagFilter.Linear,
                    MinFilter = ETexMinFilter.Linear,
                    UWrap = ETexWrapMode.Repeat,
                    VWrap = ETexWrapMode.Repeat,
                    AlphaAsTransparency = true,
                    AutoGenerateMipmaps = false,
                    Resizable = false,
                };
            });
        }

        private const int ImportedSurfaceDetailNormalMapMode = 0;
        private const int ImportedSurfaceDetailHeightMapMode = 1;
        private const float ImportedHeightMapScale = 1.0f;

        private static TextureType NormalizeTextureType(TextureType textureType)
            => textureType switch
            {
                TextureType.Normals => TextureType.NormalCamera,
                TextureType.Shininess => TextureType.Specular,
                TextureType.EmissionColor => TextureType.Emissive,
                _ => textureType,
            };

        private static TextureType? InferTextureTypeFromFilePath(string? filePath)
        {
            string textureName = Path.GetFileNameWithoutExtension(filePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(textureName))
                return null;

            string key = textureName.ToLowerInvariant();
            bool looksLikeBaseColor = key.Contains("basecolor") || key.Contains("albedo") || key.Contains("diffuse");
            bool looksLikeOpacityMask = key.Contains("opacity") || key.Contains("transparent") || key.Contains("mask") || (key.Contains("alpha") && !looksLikeBaseColor);

            return key switch
            {
                var text when text.Contains("norm") || text.Contains("nrm") => TextureType.NormalCamera,
                var text when text.Contains("bump") || text.Contains("height") => TextureType.Height,
                var text when text.Contains("spec") || text.Contains("shin") => TextureType.Specular,
                _ when looksLikeBaseColor => TextureType.BaseColor,
                _ when looksLikeOpacityMask => TextureType.Opacity,
                var text when text.Contains("emiss") => TextureType.Emissive,
                var text when text.Contains("metal") => TextureType.Metalness,
                var text when text.Contains("rough") => TextureType.Roughness,
                _ => null,
            };
        }

        private static TextureType GetEffectiveTextureType(TextureSlot texture)
        {
            TextureType normalizedType = NormalizeTextureType(texture.TextureType);
            return InferTextureTypeFromFilePath(texture.FilePath) ?? normalizedType;
        }

        private static int ResolveTextureIndex(List<TextureSlot> textures, params TextureType[] textureTypes)
        {
            foreach (TextureType textureType in textureTypes)
            {
                TextureType normalizedType = NormalizeTextureType(textureType);
                int index = textures.FindIndex(x => NormalizeTextureType(x.TextureType) == normalizedType);
                if (index >= 0)
                    return index;

                index = textures.FindIndex(x => GetEffectiveTextureType(x) == normalizedType);
                if (index >= 0)
                    return index;
            }

            return -1;
        }

        private static XRTexture? ResolveTexture(XRTexture[] textureList, int textureIndex)
            => textureIndex >= 0 && textureIndex < textureList.Length ? textureList[textureIndex] : null;

        private static int ResolveSurfaceDetailTextureIndex(List<TextureSlot> textures, out bool isHeightMap)
        {
            int normalIndex = ResolveTextureIndex(textures, TextureType.Normals, TextureType.NormalCamera);
            if (normalIndex >= 0)
            {
                isHeightMap = false;
                return normalIndex;
            }

            int heightIndex = ResolveTextureIndex(textures, TextureType.Height);
            isHeightMap = heightIndex >= 0;
            return heightIndex;
        }

        private static void AppendSurfaceDetailParameters(ref ShaderVar[] parameters, bool isHeightMap)
        {
            Array.Resize(ref parameters, parameters.Length + 2);
            parameters[^2] = new ShaderInt(isHeightMap ? ImportedSurfaceDetailHeightMapMode : ImportedSurfaceDetailNormalMapMode, "NormalMapMode");
            parameters[^1] = new ShaderFloat(isHeightMap ? ImportedHeightMapScale : 0.0f, "HeightMapScale");
        }

        public static void MakeMaterialDeferred(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            ETransparencyMode transparencyMode = ResolveTransparencyMode(textureList, textures);
            bool transp = IsTransparentLike(transparencyMode);
            bool hasAnyTexture = textureList.Length > 0;

            int diffuseIndex = ResolveTextureIndex(textures, TextureType.Diffuse, TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = ResolveSurfaceDetailTextureIndex(textures, out bool usesHeightMap);
            int specularIndex = ResolveTextureIndex(textures, TextureType.Specular, TextureType.Shininess);
            int alphaMaskIndex = ResolveTextureIndex(textures, TextureType.Opacity);
            int metallicIndex = ResolveTextureIndex(textures, TextureType.Metalness);
            int roughnessIndex = ResolveTextureIndex(textures, TextureType.Roughness);
            int emissiveIndex = ResolveTextureIndex(textures, TextureType.EmissionColor, TextureType.Emissive);

            XRTexture? diffuse = ResolveTexture(textureList, diffuseIndex);
            XRTexture? normal = ResolveTexture(textureList, normalIndex);
            XRTexture? specular = ResolveTexture(textureList, specularIndex);
            XRTexture? alphaMask = ResolveTexture(textureList, alphaMaskIndex);
            XRTexture? metallic = ResolveTexture(textureList, metallicIndex);
            XRTexture? roughness = ResolveTexture(textureList, roughnessIndex);
            XRTexture? emissive = ResolveTexture(textureList, emissiveIndex);

            bool hasNormal = normal is not null;
            bool hasSpecular = specular is not null;
            bool hasAlphaMask = alphaMask is not null;
            bool hasMetallic = metallic is not null;
            bool hasRoughness = roughness is not null;
            bool hasEmissive = emissive is not null;

            // -- Diagnostic dump (TEMPORARY) ----------------------------
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[MakeMaterialDeferred] '{name}' transparency={transparencyMode} slots=[");
                for (int i = 0; i < textures.Count; i++)
                {
                    var s = textures[i];
                    sb.Append($" {i}:{s.TextureType}(eff={GetEffectiveTextureType(s)})='{Path.GetFileName(s.FilePath)}'");
                }
                sb.Append($" ] resolved: diffuse={diffuseIndex}:{diffuse?.Name ?? "NULL"} normal={normalIndex}:{normal?.Name ?? "NULL"}(heightMap={usesHeightMap}) spec={specularIndex}:{specular?.Name ?? "NULL"} alpha={alphaMaskIndex}:{alphaMask?.Name ?? "NULL"} metal={metallicIndex}:{metallic?.Name ?? "NULL"} rough={roughnessIndex}:{roughness?.Name ?? "NULL"} emis={emissiveIndex}:{emissive?.Name ?? "NULL"}");
                sb.Append($" flags: N={hasNormal} S={hasSpecular} A={hasAlphaMask} M={hasMetallic} R={hasRoughness} E={hasEmissive}");
                LogImportDiagnostic(sb.ToString());
            }
            // -- End diagnostic dump ------------------------------------

            mat.Shaders.Clear();

            if (hasAnyTexture)
            {
                if (transp)
                {
                    transp = true;
                    // Truly transparent materials cannot use the deferred GBuffer pass, so fall
                    // back to a lit forward shader to preserve scene lighting while compositing
                    // via the transparent pipeline (WBOIT, etc.).
                    mat.Textures = [diffuse];
                    mat.Shaders.Add(ShaderHelper.LitTextureFragForward());
                    mat.Parameters =
                    [
                        new ShaderFloat(1.0f, "MatSpecularIntensity"),
                        new ShaderFloat(32.0f, "MatShininess"),
                        new ShaderFloat(0.9f, "Roughness"),
                        new ShaderFloat(0.0f, "Metallic"),
                        new ShaderFloat(0.0f, "Emission"),
                    ];
                    mat.RenderPass = ShaderHelper.ResolveTransparentRenderPass(transparencyMode);
                    mat.RenderOptions = new RenderingParameters()
                    {
                        CullMode = ECullMode.Back,
                        DepthTest = new DepthTest()
                        {
                            UpdateDepth = false,
                            Enabled = ERenderParamUsage.Enabled,
                            Function = EComparison.Lequal,
                        },
                        BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                        RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.ViewportDimensions,
                    };
                }
                else
                {
                    float roughnessScale = hasRoughness ? 1.0f : 0.9f;
                    float metallicScale = hasMetallic ? 1.0f : 0.0f;
                    float emissionScale = hasEmissive ? 1.0f : 0.0f;

                    // Legacy imported materials (for example OBJ/MTL assets like Sponza)
                    // use specular and separate opacity-mask textures rather than metallic-roughness.
                    // Keep those textures in the deferred GBuffer so the final deferred shading path
                    // receives the same material information as the forward path.
                    if (hasNormal && hasRoughness)
                    {
                        // Sparse metallic slot is intentional: the shader expects Texture2=metallic and Texture3=roughness.
                        mat.Textures = [diffuse, normal, metallic, roughness];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalRoughnessMetallicDeferred());
                    }
                    else if (hasNormal && hasMetallic)
                    {
                        mat.Textures = [diffuse, normal, metallic];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalMetallicFragDeferred());
                    }
                    else if (hasNormal && hasSpecular && hasAlphaMask)
                    {
                        mat.Textures = [diffuse, normal, specular, alphaMask];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalSpecAlphaFragDeferred());
                    }
                    else if (hasNormal && hasSpecular)
                    {
                        mat.Textures = [diffuse, normal, specular];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalSpecFragDeferred());
                    }
                    else if (hasNormal && hasAlphaMask)
                    {
                        mat.Textures = [diffuse, normal, alphaMask];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalAlphaFragDeferred());
                    }
                    else if (hasNormal)
                    {
                        mat.Textures = [diffuse, normal];
                        mat.Shaders.Add(ShaderHelper.LitTextureNormalFragDeferred());
                    }
                    else if (hasMetallic && hasRoughness)
                    {
                        // Sparse slot 1 is intentional: Texture2/Texture3 map to metallic/roughness in the shader.
                        mat.Textures = [diffuse, null, metallic, roughness];
                        mat.Shaders.Add(ShaderHelper.LitTextureMetallicRoughnessDeferred());
                    }
                    else if (hasMetallic)
                    {
                        mat.Textures = [diffuse, null, metallic];
                        mat.Shaders.Add(ShaderHelper.LitTextureMetallicFragDeferred());
                    }
                    else if (hasRoughness)
                    {
                        mat.Textures = [diffuse, null, roughness];
                        mat.Shaders.Add(ShaderHelper.LitTextureRoughnessFragDeferred());
                    }
                    else if (hasSpecular && hasAlphaMask)
                    {
                        mat.Textures = [diffuse, specular, alphaMask];
                        mat.Shaders.Add(ShaderHelper.LitTextureSpecAlphaFragDeferred());
                    }
                    else if (hasSpecular)
                    {
                        mat.Textures = [diffuse, specular];
                        mat.Shaders.Add(ShaderHelper.LitTextureSpecFragDeferred());
                    }
                    else if (hasAlphaMask)
                    {
                        mat.Textures = [diffuse, alphaMask];
                        mat.Shaders.Add(ShaderHelper.LitTextureAlphaFragDeferred());
                    }
                    else if (hasEmissive)
                    {
                        mat.Textures = [diffuse, emissive];
                        mat.Shaders.Add(ShaderHelper.LitTextureEmissiveDeferred());
                    }
                    else
                    {
                        mat.Textures = [diffuse];
                        mat.Shaders.Add(ShaderHelper.LitTextureFragDeferred()!);
                    }

                    mat.Parameters =
                    [
                        new ShaderVector3(ColorF3.White, "BaseColor"),
                        new ShaderFloat(1.0f, "Opacity"),
                        new ShaderFloat(1.0f, "Specular"),
                        new ShaderFloat(roughnessScale, "Roughness"),
                        new ShaderFloat(metallicScale, "Metallic"),
                        new ShaderFloat(emissionScale, "Emission"),
                    ];

                    if (hasNormal)
                    {
                        var parameters = mat.Parameters;
                        AppendSurfaceDetailParameters(ref parameters, usesHeightMap);
                        mat.Parameters = parameters;
                    }
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
                    new ShaderFloat(0.0f, "Emission"),
                ];
                mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
            }

            mat.Name = name;

            // -- Diagnostic dump (TEMPORARY) ----------------------------
            {
                string shader = mat.Shaders.Count > 0 ? mat.Shaders[0]?.Name ?? "(null)" : "(none)";
                string texNames = string.Join(", ", (mat.Textures ?? []).Select((t, i) => $"[{i}]={t?.Name ?? "NULL"}"));
                LogImportDiagnostic($"[MakeMaterialDeferred] '{name}' ? shader={shader} textures=({texNames}) pass={mat.RenderPass}");
            }
            // -- End diagnostic dump ------------------------------------

            if (!transp)
            {
                mat.RenderOptions = new RenderingParameters()
                {
                    CullMode = ECullMode.Back,
                    DepthTest = new DepthTest()
                    {
                        UpdateDepth = true,
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Lequal,
                    },
                    BlendModeAllDrawBuffers = BlendMode.Disabled(),
                };
            }

            ConfigureImportedTransparency(mat, textureList, textures);
        }

        public static XRMaterial MakeMaterialDeferred(XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            XRMaterial mat = new(textureList);
            MakeMaterialDeferred(mat, textureList, textures, name);
            return mat;
        }

        public static void MakeMaterialForwardPlusTextured(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            ETransparencyMode transparencyMode = ResolveTransparencyMode(textureList, textures);

            int diffuseIndex = ResolveTextureIndex(textures, TextureType.Diffuse, TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = ResolveSurfaceDetailTextureIndex(textures, out bool usesHeightMap);
            int specularIndex = ResolveTextureIndex(textures, TextureType.Specular, TextureType.Shininess);
            int alphaMaskIndex = ResolveTextureIndex(textures, TextureType.Opacity);

            XRTexture? diffuse = diffuseIndex >= 0 && diffuseIndex < textureList.Length ? textureList[diffuseIndex] : null;
            XRTexture? normal = normalIndex >= 0 && normalIndex < textureList.Length ? textureList[normalIndex] : null;
            XRTexture? specular = specularIndex >= 0 && specularIndex < textureList.Length ? textureList[specularIndex] : null;
            XRTexture? alphaMask = alphaMaskIndex >= 0 && alphaMaskIndex < textureList.Length ? textureList[alphaMaskIndex] : null;

            bool hasNormal = normal is not null;
            bool hasSpecular = specular is not null;
            bool hasAlphaMask = alphaMask is not null;
            bool useTransparentBlend = IsTransparentLike(transparencyMode);

            // Force a deterministic texture layout for the shader based on available maps:
            // With normal: Texture0=Albedo, Texture1=Normal, Texture2=Specular, Texture3=AlphaMask
            // Without normal: Texture0=Albedo, Texture1=Specular, Texture2=AlphaMask
            if (hasNormal && hasSpecular && hasAlphaMask)
                mat.Textures = [diffuse, normal, specular, alphaMask];
            else if (hasNormal && hasSpecular)
                mat.Textures = [diffuse, normal, specular];
            else if (hasNormal && hasAlphaMask)
                mat.Textures = [diffuse, normal, alphaMask];
            else if (!hasNormal && hasSpecular && hasAlphaMask)
                mat.Textures = [diffuse, specular, alphaMask];
            else if (!hasNormal && hasSpecular)
                mat.Textures = [diffuse, specular];
            else if (!hasNormal && hasAlphaMask)
                mat.Textures = [diffuse, alphaMask];
            else if (hasNormal)
                mat.Textures = [diffuse, normal];
            else
                mat.Textures = [diffuse];

            mat.Shaders.Clear();

            if (diffuse is not null)
            {
                // Select the appropriate shader based on available maps
                XRShader shader;
                if (hasNormal && hasSpecular && hasAlphaMask)
                    shader = ShaderHelper.LitTextureNormalSpecAlphaFragForward();
                else if (hasNormal && hasSpecular)
                    shader = ShaderHelper.LitTextureNormalSpecFragForward();
                else if (hasNormal && hasAlphaMask)
                    shader = ShaderHelper.LitTextureNormalAlphaFragForward();
                else if (!hasNormal && hasSpecular && hasAlphaMask)
                    shader = ShaderHelper.LitTextureSpecAlphaFragForward();
                else if (!hasNormal && hasSpecular)
                    shader = ShaderHelper.LitTextureSpecFragForward();
                else if (!hasNormal && hasAlphaMask)
                    shader = ShaderHelper.LitTextureAlphaFragForward();
                else if (hasNormal)
                    shader = ShaderHelper.LitTextureNormalFragForward();
                else
                    shader = ShaderHelper.LitTextureFragForward();

                mat.Shaders.Add(shader);
                if (hasAlphaMask)
                {
                    mat.Parameters =
                    [
                        new ShaderFloat(1.0f, "MatSpecularIntensity"),
                        new ShaderFloat(32.0f, "MatShininess"),
                        new ShaderFloat(0.9f, "Roughness"),
                        new ShaderFloat(0.0f, "Metallic"),
                        new ShaderFloat(0.0f, "Emission"),
                        new ShaderFloat(0.5f, "AlphaCutoff"),
                    ];
                }
                else
                {
                    mat.Parameters =
                    [
                        new ShaderFloat(1.0f, "MatSpecularIntensity"),
                        new ShaderFloat(32.0f, "MatShininess"),
                        new ShaderFloat(0.9f, "Roughness"),
                        new ShaderFloat(0.0f, "Metallic"),
                        new ShaderFloat(0.0f, "Emission"),
                    ];
                }

                if (hasNormal)
                {
                    var parameters = mat.Parameters;
                    AppendSurfaceDetailParameters(ref parameters, usesHeightMap);
                    mat.Parameters = parameters;
                }

                if (useTransparentBlend)
                    mat.RenderPass = ShaderHelper.ResolveTransparentRenderPass(transparencyMode);
                else
                    mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            }
            else
            {
                mat.Shaders.Add(ShaderHelper.LitColorFragForward());
                mat.Parameters =
                [
                    new ShaderVector4(new Vector4(1, 0, 1, 1), "MatColor"),
                    new ShaderFloat(1.0f, "MatSpecularIntensity"),
                    new ShaderFloat(32.0f, "MatShininess"),
                    new ShaderFloat(1.0f, "Roughness"),
                    new ShaderFloat(0.0f, "Metallic"),
                    new ShaderFloat(0.0f, "Emission"),
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
                    UpdateDepth = !useTransparentBlend,
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                },
                BlendModeAllDrawBuffers = useTransparentBlend ? BlendMode.EnabledTransparent() : BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.Lights | EUniformRequirements.ViewportDimensions,
            };

            ConfigureImportedTransparency(mat, textureList, textures);
        }

        public static XRMaterial MakeMaterialForwardPlusTextured(XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            XRMaterial mat = new(textureList);
            MakeMaterialForwardPlusTextured(mat, textureList, textures, name);
            return mat;
        }

        public static ShaderVar[] CreateDefaultForwardPlusUberShaderParameters(float bumpScale = 0.0f)
        {
            Vector4 identitySt = new(1.0f, 1.0f, 0.0f, 0.0f);

            return
            [
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_Color"),
                new ShaderInt(0, "_ColorThemeIndex"),

                new ShaderVector4(identitySt, "_MainTex_ST"),
                new ShaderVector2(Vector2.Zero, "_MainTexPan"),
                new ShaderInt(0, "_MainTexUV"),

                new ShaderVector4(identitySt, "_BumpMap_ST"),
                new ShaderVector2(Vector2.Zero, "_BumpMapPan"),
                new ShaderInt(0, "_BumpMapUV"),
                new ShaderFloat(bumpScale, "_BumpScale"),

                new ShaderVector4(identitySt, "_AlphaMask_ST"),
                new ShaderVector2(Vector2.Zero, "_AlphaMaskPan"),
                new ShaderInt(0, "_AlphaMaskUV"),
                new ShaderInt(0, "_MainAlphaMaskMode"),
                new ShaderFloat(1.0f, "_AlphaMaskBlendStrength"),
                new ShaderFloat(0.0f, "_AlphaMaskValue"),
                new ShaderFloat(0.0f, "_AlphaMaskInvert"),
                new ShaderFloat(0.5f, "_Cutoff"),
                new ShaderInt(0, "_Mode"),
                new ShaderFloat(1.0f, "_AlphaForceOpaque"),
                new ShaderFloat(0.0f, "_AlphaMod"),

                new ShaderVector4(identitySt, "_MainColorAdjustTexture_ST"),
                new ShaderVector2(Vector2.Zero, "_MainColorAdjustTexturePan"),
                new ShaderInt(0, "_MainColorAdjustTextureUV"),
                new ShaderFloat(0.0f, "_Saturation"),
                new ShaderFloat(0.0f, "_MainBrightness"),
                new ShaderFloat(0.0f, "_MainHueShiftToggle"),
                new ShaderFloat(0.0f, "_MainHueShift"),
                new ShaderFloat(0.0f, "_MainHueShiftSpeed"),
                new ShaderInt(0, "_MainHueShiftColorSpace"),
                new ShaderFloat(0.0f, "_MainHueShiftReplace"),

                new ShaderInt(5, "_LightingMode"),
                new ShaderInt(0, "_LightingColorMode"),
                new ShaderInt(2, "_LightingMapMode"),
                new ShaderInt(0, "_LightingDirectionMode"),
                new ShaderFloat(0.0f, "_LightingCapEnabled"),
                new ShaderFloat(10.0f, "_LightingCap"),
                new ShaderFloat(0.0f, "_LightingMinLightBrightness"),
                new ShaderFloat(0.0f, "_LightingMonochromatic"),
                new ShaderFloat(0.0f, "_LightingIndirectUsesNormals"),
                new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "_LightingShadowColor"),
                new ShaderFloat(1.0f, "_ShadowStrength"),
                new ShaderFloat(0.0f, "_LightingIgnoreAmbientColor"),
                new ShaderFloat(0.0f, "_ShadowOffset"),
                new ShaderFloat(0.0f, "_ForceFlatRampedLightmap"),
                new ShaderVector4(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "_ShadowColor"),
                new ShaderFloat(0.5f, "_ShadowBorder"),
                new ShaderFloat(0.05f, "_ShadowBlur"),
                new ShaderFloat(0.0f, "_LightingWrappedWrap"),
                new ShaderFloat(0.0f, "_LightingWrappedNormalization"),
                new ShaderFloat(0.0f, "_LightingGradientStart"),
                new ShaderFloat(1.0f, "_LightingGradientEnd"),

                new ShaderVector4(identitySt, "_LightingAOMaps_ST"),
                new ShaderVector2(Vector2.Zero, "_LightingAOMapsPan"),
                new ShaderInt(0, "_LightingAOMapsUV"),
                new ShaderFloat(0.0f, "_LightDataAOStrengthR"),
                new ShaderVector4(identitySt, "_LightingShadowMasks_ST"),
                new ShaderFloat(0.0f, "_LightingShadowMaskStrengthR"),

                new ShaderVector4(identitySt, "_EmissionMap_ST"),
                new ShaderVector2(Vector2.Zero, "_EmissionMapPan"),
                new ShaderInt(0, "_EmissionMapUV"),
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_EmissionColor"),
                new ShaderFloat(1.0f, "_EmissionStrength"),
                new ShaderFloat(0.0f, "_EmissionScrollingEnabled"),
                new ShaderVector2(Vector2.Zero, "_EmissionScrollingSpeed"),
                new ShaderFloat(0.0f, "_EmissionScrollingVertexColor"),

                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_MatcapColor"),
                new ShaderFloat(1.0f, "_MatcapIntensity"),
                new ShaderFloat(0.0f, "_MatcapBorder"),
                new ShaderInt(0, "_MatcapUVMode"),
                new ShaderFloat(0.0f, "_MatcapReplace"),
                new ShaderFloat(0.0f, "_MatcapMultiply"),
                new ShaderFloat(0.0f, "_MatcapAdd"),
                new ShaderFloat(0.0f, "_MatcapEmissionStrength"),
                new ShaderFloat(0.0f, "_MatcapLightMask"),
                new ShaderFloat(1.0f, "_MatcapNormal"),
                new ShaderVector4(identitySt, "_MatcapMask_ST"),
                new ShaderInt(0, "_MatcapMaskChannel"),
                new ShaderFloat(0.0f, "_MatcapMaskInvert"),

                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_RimLightColor"),
                new ShaderFloat(0.5f, "_RimWidth"),
                new ShaderFloat(0.5f, "_RimSharpness"),
                new ShaderFloat(0.0f, "_RimLightColorBias"),
                new ShaderFloat(0.0f, "_RimEmission"),
                new ShaderFloat(0.0f, "_RimHideInShadow"),
                new ShaderInt(0, "_RimStyle"),
                new ShaderFloat(1.0f, "_RimBlendStrength"),
                new ShaderInt(0, "_RimBlendMode"),
                new ShaderVector4(identitySt, "_RimMask_ST"),
                new ShaderInt(0, "_RimMaskChannel"),

                new ShaderFloat(0.0f, "_StylizedSpecular"),
                new ShaderVector4(identitySt, "_SpecularMap_ST"),
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_SpecularTint"),
                new ShaderFloat(0.5f, "_SpecularSmoothness"),
                new ShaderFloat(1.0f, "_SpecularStrength"),
                new ShaderInt(0, "_SpecularType"),

                new ShaderVector4(identitySt, "_DetailMask_ST"),
                new ShaderVector4(identitySt, "_DetailTex_ST"),
                new ShaderVector2(Vector2.Zero, "_DetailTexPan"),
                new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "_DetailTint"),
                new ShaderFloat(0.0f, "_DetailTexIntensity"),
                new ShaderFloat(0.0f, "_DetailBrightness"),
                new ShaderVector4(identitySt, "_DetailNormalMap_ST"),
                new ShaderVector2(Vector2.Zero, "_DetailNormalMapPan"),
                new ShaderFloat(0.0f, "_DetailNormalMapScale"),

                new ShaderFloat(0.0f, "_MainVertexColoringEnabled"),
                new ShaderFloat(0.0f, "_MainVertexColoringLinearSpace"),
                new ShaderFloat(1.0f, "_MainVertexColoring"),
                new ShaderFloat(0.0f, "_MainUseVertexColorAlpha"),

                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_BackFaceColor"),
                new ShaderFloat(0.0f, "_BackFaceBlendMode"),
                new ShaderFloat(0.0f, "_BackFaceEmission"),
                new ShaderFloat(1.0f, "_BackFaceAlpha"),

                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_GlitterColor"),
                new ShaderFloat(1.0f, "_GlitterDensity"),
                new ShaderFloat(1.0f, "_GlitterSize"),
                new ShaderFloat(1.0f, "_GlitterSpeed"),
                new ShaderFloat(1.0f, "_GlitterBrightness"),
                new ShaderFloat(0.0f, "_GlitterMinAngle"),
                new ShaderFloat(1.0f, "_GlitterMaxAngle"),
                new ShaderFloat(0.0f, "_GlitterRainbow"),

                new ShaderFloat(1.0f, "_FlipbookColumns"),
                new ShaderFloat(1.0f, "_FlipbookRows"),
                new ShaderFloat(0.0f, "_FlipbookFrameRate"),
                new ShaderFloat(0.0f, "_FlipbookFrame"),
                new ShaderFloat(0.0f, "_FlipbookManualFrame"),
                new ShaderFloat(0.0f, "_FlipbookBlendMode"),
                new ShaderFloat(0.0f, "_FlipbookCrossfade"),

                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_SSSColor"),
                new ShaderFloat(1.0f, "_SSSPower"),
                new ShaderFloat(0.0f, "_SSSDistortion"),
                new ShaderFloat(1.0f, "_SSSScale"),
                new ShaderFloat(0.0f, "_SSSAmbient"),

                new ShaderFloat(0.0f, "_DissolveType"),
                new ShaderFloat(0.0f, "_DissolveProgress"),
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_DissolveEdgeColor"),
                new ShaderFloat(0.05f, "_DissolveEdgeWidth"),
                new ShaderFloat(0.0f, "_DissolveEdgeEmission"),
                new ShaderVector4(identitySt, "_DissolveNoiseTexture_ST"),
                new ShaderFloat(1.0f, "_DissolveNoiseStrength"),
                new ShaderVector3(Vector3.Zero, "_DissolveStartPoint"),
                new ShaderVector3(Vector3.UnitY, "_DissolveEndPoint"),
                new ShaderFloat(0.0f, "_DissolveInvert"),
                new ShaderFloat(0.5f, "_DissolveCutoff"),

                new ShaderFloat(0.0f, "_ParallaxMode"),
                new ShaderVector4(identitySt, "_ParallaxMap_ST"),
                new ShaderFloat(0.05f, "_ParallaxStrength"),
                new ShaderFloat(8.0f, "_ParallaxMinSamples"),
                new ShaderFloat(32.0f, "_ParallaxMaxSamples"),
                new ShaderFloat(0.5f, "_ParallaxOffset"),
                new ShaderFloat(0.0f, "_ParallaxMapChannel"),

                new ShaderFloat(0.0f, "_PBRBRDF"),
                new ShaderFloat(0.0f, "_PBRMetallicMultiplier"),
                new ShaderFloat(1.0f, "_PBRRoughnessMultiplier"),
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_PBRReflectionTint"),
                new ShaderVector4(new Vector4(1.0f, 1.0f, 1.0f, 1.0f), "_PBRSpecularTint"),
                new ShaderFloat(1.0f, "_PBRReflectionStrength"),
                new ShaderFloat(1.0f, "_PBRSpecularStrength"),
                new ShaderFloat(1.0f, "_RefSpecFresnel"),
                new ShaderFloat(0.0f, "_RefSpecFresnelAlpha"),
                new ShaderVector4(identitySt, "_PBRMetallicMaps_ST"),
                new ShaderVector2(Vector2.Zero, "_PBRMetallicMapsPan"),
                new ShaderInt(0, "_PBRMetallicMapsUV"),
                new ShaderInt(0, "_PBRMetallicMapsMetallicChannel"),
                new ShaderInt(1, "_PBRMetallicMapsRoughnessChannel"),
                new ShaderInt(2, "_PBRMetallicMapsReflectionMaskChannel"),
                new ShaderInt(3, "_PBRMetallicMapsSpecularMaskChannel"),
                new ShaderFloat(0.0f, "_PBRMetallicMapInvert"),
                new ShaderFloat(0.0f, "_PBRRoughnessMapInvert"),
                new ShaderFloat(0.0f, "_PBRReflectionMaskInvert"),
                new ShaderFloat(0.0f, "_PBRSpecularMaskInvert"),
                new ShaderFloat(0.0f, "_PBRForceFallback"),
                new ShaderFloat(0.0f, "_PBRLitFallback"),
                new ShaderFloat(0.0f, "_Specular2ndLayer"),
                new ShaderFloat(1.0f, "_PBRSpecularStrength2"),
                new ShaderFloat(1.0f, "_PBRRoughnessMultiplier2"),
                new ShaderFloat(0.0f, "_PBRNormalSelect"),
                new ShaderFloat(0.0f, "_PBRGSAAEnabled"),
                new ShaderFloat(0.15f, "_GSAAVariance"),
                new ShaderFloat(0.2f, "_GSAAThreshold"),
            ];
        }

        public static RenderingParameters CreateForwardPlusUberShaderRenderOptions()
            => new()
            {
                CullMode = ECullMode.Back,
                DepthTest = new DepthTest()
                {
                    UpdateDepth = true,
                    Enabled = ERenderParamUsage.Enabled,
                    Function = EComparison.Lequal,
                },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                RequiredEngineUniforms = EUniformRequirements.Camera
                    | EUniformRequirements.Lights
                    | EUniformRequirements.ViewportDimensions
                    | EUniformRequirements.RenderTime,
            };

        public static void MakeMaterialForwardPlusUberShader(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            int diffuseIndex = ResolveTextureIndex(textures, TextureType.Diffuse, TextureType.BaseColor);
            if (diffuseIndex < 0)
                diffuseIndex = Array.FindIndex(textureList, t => t is not null);
            if (diffuseIndex < 0)
                diffuseIndex = 0;

            int normalIndex = ResolveSurfaceDetailTextureIndex(textures, out _);
            int alphaMaskIndex = ResolveTextureIndex(textures, TextureType.Opacity);

            XRTexture? diffuseSrc = diffuseIndex >= 0 && diffuseIndex < textureList.Length ? textureList[diffuseIndex] : null;
            XRTexture? normalSrc = normalIndex >= 0 && normalIndex < textureList.Length ? textureList[normalIndex] : null;
            XRTexture? alphaMaskSrc = alphaMaskIndex >= 0 && alphaMaskIndex < textureList.Length ? textureList[alphaMaskIndex] : null;

            string? diffusePath = (diffuseSrc as XRTexture2D)?.FilePath;
            string? normalPath = (normalSrc as XRTexture2D)?.FilePath;
            string? alphaMaskPath = (alphaMaskSrc as XRTexture2D)?.FilePath;

            XRTexture2D main = diffusePath is not null
                ? GetOrCreateUberSamplerTexture(diffusePath, "_MainTex")
                : GetOrCreateDefaultUberSamplerTexture("_MainTex", ColorF4.White);
            XRTexture2D bump = GetOrCreateDefaultUberSamplerTexture("_BumpMap", new ColorF4(0.5f, 0.5f, 1.0f, 1.0f));
            XRTexture2D? alphaMask = null;
            float bumpScale = 0.0f;
            if (normalPath is not null)
            {
                bump = GetOrCreateUberSamplerTexture(normalPath, "_BumpMap");
                bumpScale = 1.0f;
            }

            if (alphaMaskPath is not null)
                alphaMask = GetOrCreateUberSamplerTexture(alphaMaskPath, "_AlphaMask");

            mat.Textures = alphaMask is null ? [main, bump] : [main, bump, alphaMask];

            XRShader frag = ShaderHelper.UberFragForward();

            mat.Shaders.Clear();
            mat.Shaders.Add(frag);

            mat.Parameters = CreateDefaultForwardPlusUberShaderParameters(bumpScale);
            if (alphaMask is not null)
                mat.Parameter<ShaderInt>("_MainAlphaMaskMode")?.SetValue(2);

            mat.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
            mat.Name = name;
            mat.RenderOptions = CreateForwardPlusUberShaderRenderOptions();

            ConfigureImportedTransparency(mat, textureList, textures);
            mat.PrepareUberVariantImmediately();
        }

        public static XRMaterial MakeMaterialForwardPlusUberShader(XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            XRMaterial mat = new(textureList);
            MakeMaterialForwardPlusUberShader(mat, textureList, textures, name);
            return mat;
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
            bool? batchSubmeshAddsDuringAsyncImport = null,
            CancellationToken cancellationToken = default,
            Action<float>? onProgress = null,
            Matrix4x4? rootTransformMatrix = null)
        {
            using var importedTextureStreamingScope = XRTexture2D.EnterImportedTextureStreamingScope();

            ModelImportOptions effectiveImportOptions = ResolveEffectiveImportOptions(
                options,
                preservePivots,
                removeAssimpFBXNodes,
                scaleConversion,
                zUp,
                multiThread,
                processMeshesAsynchronously,
                batchSubmeshAddsDuringAsyncImport);

            using var _ = ImportOptionsScope.Push(effectiveImportOptions);
            using var __ = ImportSourceScope.Push(SourceFilePath);

            if (ShouldUseNativeGltfBackend(effectiveImportOptions))
            {
                bool allowAssimpFallback = effectiveImportOptions.GltfBackend == GltfImportBackend.Auto;

                try
                {
                    NativeGltfSceneImporter.ImportResult result = NativeGltfSceneImporter.Import(
                        this,
                        SourceFilePath,
                        effectiveImportOptions,
                        effectiveImportOptions.ScaleConversion,
                        effectiveImportOptions.ZUp,
                        _importLayer,
                        cancellationToken,
                        onProgress,
                        rootTransformMatrix);

                    foreach (XRMaterial material in result.Materials)
                        _materials.Add(material);
                    foreach (XRMesh mesh in result.Meshes)
                        _meshes.Add(mesh);

                    return result.RootNode;
                }
                catch (Exception ex) when (allowAssimpFallback)
                {
                    LogImportWarning(SourceFilePath, $"[ModelImporter.Import] Native glTF import failed for '{SourceFilePath}'. Falling back to Assimp. {ex.Message}");
                }
            }

            if (ShouldUseNativeFbxBackend(effectiveImportOptions))
            {
                NativeFbxSceneImporter.ImportResult result = NativeFbxSceneImporter.Import(
                    this,
                    SourceFilePath,
                    effectiveImportOptions,
                    effectiveImportOptions.ScaleConversion,
                    effectiveImportOptions.ZUp,
                    _importLayer,
                    cancellationToken,
                    onProgress,
                    rootTransformMatrix);

                foreach (XRMaterial material in result.Materials)
                    _materials.Add(material);
                foreach (XRMesh mesh in result.Meshes)
                    _meshes.Add(mesh);

                return result.RootNode;
            }

            SetAssimpConfig(effectiveImportOptions);

            AScene scene;
            using (Engine.Profiler.Start($"Assimp ImportFile: {SourceFilePath} with options: {effectiveImportOptions.PostProcessSteps}"))
            {
                scene = _assimp.ImportFile(SourceFilePath, effectiveImportOptions.PostProcessSteps);
            }

            if (scene is null || scene.SceneFlags == SceneFlags.Incomplete || scene.RootNode is null)
            {
                LogImportWarning(SourceFilePath, $"[ModelImporter.Import] Assimp returned null/incomplete scene for '{SourceFilePath}'. scene={scene != null}, flags={scene?.SceneFlags}, rootNode={scene?.RootNode != null}");
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            _meshProcessRoutines.Clear();
            _meshFinalizeActions.Clear();
            bool processMeshesAsync = effectiveImportOptions.ProcessMeshesAsynchronously ?? Engine.Rendering.Settings.ProcessMeshImportsAsynchronously;
            bool batchSubmeshAdds = effectiveImportOptions.BatchSubmeshAddsDuringAsyncImport;
            bool importedRenderersGenerateAsync = effectiveImportOptions.GenerateMeshRenderersAsync;
            bool splitSubmeshesIntoSeparateModelComponents = effectiveImportOptions.SplitSubmeshesIntoSeparateModelComponents;

            SceneNode rootNode;
            using (Engine.Profiler.Start($"Assemble model hierarchy"))
            {
                rootNode = new(Path.GetFileNameWithoutExtension(SourceFilePath)) { Layer = _importLayer };
                _nodeTransforms.Clear();
                ProcessNode(true, scene.RootNode, scene, rootNode, null, rootTransformMatrix ?? Matrix4x4.Identity, effectiveImportOptions.CollapseGeneratedFbxHelperNodes, null, cancellationToken);
                NormalizeNodeScales(
                    scene,
                    rootNode,
                    cancellationToken,
                    processMeshesAsync,
                    batchSubmeshAdds,
                    importedRenderersGenerateAsync,
                    splitSubmeshesIntoSeparateModelComponents);
            }

            void meshProcessAction() => ProcessMeshesOnJobThread(onProgress, cancellationToken);
            RunMeshProcessing(meshProcessAction, processMeshesAsync, cancellationToken);

            return rootNode;
        }

        private ModelImportOptions ResolveEffectiveImportOptions(
            PostProcessSteps options,
            bool preservePivots,
            bool removeAssimpFBXNodes,
            float scaleConversion,
            bool zUp,
            bool multiThread,
            bool? processMeshesAsynchronously,
            bool? batchSubmeshAddsDuringAsyncImport)
        {
            if (ImportOptions is not null)
                return ImportOptions;

            ModelImportOptions resolved = new()
            {
                FbxPivotPolicy = preservePivots
                    ? FbxPivotImportPolicy.PreservePivotSemantics
                    : FbxPivotImportPolicy.BakeIntoLocalTransform,
                CollapseGeneratedFbxHelperNodes = removeAssimpFBXNodes,
                ScaleConversion = scaleConversion,
                ZUp = zUp,
                MultiThread = multiThread,
                ProcessMeshesAsynchronously = processMeshesAsynchronously,
                GltfBackend = GltfImportBackend.Auto,
            };

            if (batchSubmeshAddsDuringAsyncImport.HasValue)
                resolved.BatchSubmeshAddsDuringAsyncImport = batchSubmeshAddsDuringAsyncImport.Value;

            resolved.LegacyPostProcessSteps = options;
            return resolved;
        }

        private bool ShouldUseNativeFbxBackend(ModelImportOptions effectiveImportOptions)
        {
            if (!Path.GetExtension(SourceFilePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                return false;

            FbxImportBackend selected = effectiveImportOptions.FbxBackend;
            if (selected == FbxImportBackend.Auto)
                selected = Engine.EditorPreferences?.FbxImporterBackend ?? FbxImportBackend.Assimp;

            return selected is not FbxImportBackend.Assimp;
        }

        private bool ShouldUseNativeGltfBackend(ModelImportOptions effectiveImportOptions)
        {
            string extension = Path.GetExtension(SourceFilePath);
            if (!extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".glb", StringComparison.OrdinalIgnoreCase))
                return false;

            GltfImportBackend selected = effectiveImportOptions.GltfBackend;
            if (selected == GltfImportBackend.Auto)
                selected = Engine.EditorPreferences?.GltfImporterBackend ?? GltfImportBackend.Auto;

            return selected is not GltfImportBackend.Assimp;
        }

        private void SetAssimpConfig(ModelImportOptions importOptions)
        {
            float rotate = importOptions.ZUp ? -90.0f : 0.0f;
            bool preservePivots = importOptions.FbxPivotPolicy == FbxPivotImportPolicy.PreservePivotSemantics;
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, preservePivots));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_MATERIALS, true));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_TEXTURES, true));
            _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_GLOB_MULTITHREADING, importOptions.MultiThread));
            _assimp.Scale = importOptions.ScaleConversion;
            _assimp.XAxisRotation = -rotate;
            //_assimp.ZAxisRotation = 180.0f;
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
            if (!processAsynchronously)
            {
                meshProcessAction();
                _onCompleted?.Invoke();
                return;
            }

            int total = _meshProcessRoutines.Count;
            if (total <= 0)
            {
                _onCompleted?.Invoke();
                return;
            }

            // Reserve some workers for other engine jobs (texture cache, debug vis, etc.)
            // to prevent mesh processing from starving the entire job system.
            int availableWorkers = Math.Max(1, Engine.Jobs.WorkerCount - 2);
            int targetBatchCount = Math.Max(1, Math.Min(total, availableWorkers));
            int batchSize = Math.Max(1, (int)Math.Ceiling(total / (double)targetBatchCount));
            int batchCount = (total + batchSize - 1) / batchSize;

            int remaining = batchCount;
            int faulted = 0;
            int canceled = 0;
            int finalized = 0;
            int[] retried = new int[batchCount];

            void TryFlushAndComplete()
            {
                if (Interlocked.Exchange(ref finalized, 1) != 0)
                    return;

                Engine.EnqueueSwapTask(() =>
                {
                    try
                    {
                        foreach (var finalize in _meshFinalizeActions)
                            finalize();

                        if (Volatile.Read(ref faulted) != 0 || Volatile.Read(ref canceled) != 0)
                            LogImportWarning(SourceFilePath, $"[ModelImporter] Mesh processing completed with partial failures for '{SourceFilePath}'. faulted={faulted}, canceled={canceled}");

                        _onCompleted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        LogImportException(ex, $"Mesh finalize failed for '{SourceFilePath}'.");
                    }
                });
            }

            void TryFinalize()
            {
                int rem = Interlocked.Decrement(ref remaining);
                if (rem != 0)
                    return;

                TryFlushAndComplete();
            }

            void ScheduleMeshBatch(int batchIndex)
            {
                int start = batchIndex * batchSize;
                int endExclusive = Math.Min(start + batchSize, total);

                IEnumerable BatchRoutine()
                {
                    for (int routineIndex = start; routineIndex < endExclusive; routineIndex++)
                    {
                        foreach (var step in _meshProcessRoutines[routineIndex]())
                            yield return step;
                    }
                }

                Engine.Jobs.Schedule(
                    BatchRoutine,
                    completed: () =>
                    {
                        TryFinalize();
                    },
                    error: ex =>
                    {
                        if (Interlocked.CompareExchange(ref retried[batchIndex], 1, 0) == 0)
                        {
                            ScheduleMeshBatch(batchIndex);
                            return;
                        }

                        Interlocked.Increment(ref faulted);
                        LogImportException(ex, $"Mesh processing batch failed after retry for '{SourceFilePath}' (batch {batchIndex}, routines {start}-{endExclusive - 1}).");
                        TryFinalize();
                    },
                    canceled: () =>
                    {
                        Interlocked.Increment(ref canceled);
                        TryFinalize();
                    },
                    cancellationToken: cancellationToken,
                    priority: JobPriority.Low);
            }

            for (int i = 0; i < batchCount; i++)
                ScheduleMeshBatch(i);
        }

        private void NormalizeNodeScales(
            AScene scene,
            SceneNode rootNode,
            CancellationToken cancellationToken,
            bool processMeshesAsynchronously,
            bool batchSubmeshAddsDuringAsyncImport,
            bool importedRenderersGenerateAsync,
            bool splitSubmeshesIntoSeparateModelComponents)
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
                //Debug.Out($"Processing node {nodeInfo.SceneNode.Name} with world T[{translation}] R[{rotation}] S[{scale}]");

                Matrix4x4 geometryTransform = nodeInfo.OriginalWorldMatrix * rootTransform.InverseWorldMatrix;
                EnqueueProcessMeshes(
                    nodeInfo.MeshIndices,
                    scene,
                    nodeInfo.SceneNode,
                    nodeInfo.SceneNode.Name,
                    geometryTransform,
                    rootTransform,
                    cancellationToken,
                    importedRenderersGenerateAsync,
                    splitSubmeshesIntoSeparateModelComponents,
                    publishSubMeshesOnSwapThread: processMeshesAsynchronously,
                    batchSubmeshAddsDuringAsyncImport: processMeshesAsynchronously && batchSubmeshAddsDuringAsyncImport);
            }
        }

        private void TrackNodeTransform(SceneNode sceneNode, Vector3 scale, List<int> meshIndices)
        {
            if (_nodeTransforms.Find(info => ReferenceEquals(info.SceneNode, sceneNode)) is NodeTransformInfo existing)
            {
                existing.OriginalScale = scale;
                foreach (int meshIndex in meshIndices)
                {
                    if (!existing.MeshIndices.Contains(meshIndex))
                        existing.MeshIndices.Add(meshIndex);
                }

                return;
            }

            _nodeTransforms.Add(new NodeTransformInfo(sceneNode, scale, meshIndices));
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
            
            TrackNodeTransform(sceneNode, scale, meshIndices);

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

        private unsafe void EnqueueProcessMeshes(
            List<int> meshIndices,
            AScene scene,
            SceneNode sceneNode,
            string componentBaseName,
            Matrix4x4 dataTransform,
            TransformBase rootTransform,
            CancellationToken cancellationToken,
            bool importedRenderersGenerateAsync,
            bool splitSubmeshesIntoSeparateModelComponents,
            bool publishSubMeshesOnSwapThread,
            bool batchSubmeshAddsDuringAsyncImport)
        {
            int count = meshIndices.Count;
            if (count == 0)
                return;

            ModelComponent CreateModelComponent(string componentName)
            {
                ModelComponent component = sceneNode.AddComponent<ModelComponent>()!;
                Model componentModel = new();
                componentModel.Meshes.ThreadSafe = true;
                component.Name = componentName;
                component.Model = componentModel;
                return component;
            }

            Dictionary<int, Model>? targetModelsByMeshIndex = null;
            Model? sharedModel = null;

            if (splitSubmeshesIntoSeparateModelComponents)
            {
                targetModelsByMeshIndex = new Dictionary<int, Model>(count);
                for (int i = 0; i < count; i++)
                {
                    int meshIndex = meshIndices[i];
                    Mesh assimpMesh = scene.Meshes[meshIndex];
                    string componentName = string.IsNullOrWhiteSpace(assimpMesh.Name)
                        ? $"{componentBaseName} SubMesh {i}"
                        : assimpMesh.Name;
                    targetModelsByMeshIndex[meshIndex] = CreateModelComponent(componentName).Model!;
                }
            }
            else
            {
                sharedModel = CreateModelComponent(componentBaseName).Model;
            }

            Model ResolveTargetModel(int meshIndex)
                => targetModelsByMeshIndex is not null
                    ? targetModelsByMeshIndex[meshIndex]
                    : sharedModel!;

            // Async processing publishes mesh additions on the swap thread so scene/render state is not
            // mutated from worker threads. The publish policy decides whether those additions are flushed
            // once at the end or streamed as contiguous source-order submeshes become ready.
            ConcurrentDictionary<int, (Model model, SubMesh subMesh)>? pending = publishSubMeshesOnSwapThread
                ? new ConcurrentDictionary<int, (Model model, SubMesh subMesh)>()
                : null;

            int[]? ordered = null;
            int nextPublishOffset = 0;

            void FlushReadySubMeshes()
            {
                if (pending is null || ordered is null)
                    return;

                while (nextPublishOffset < ordered.Length && pending.TryRemove(ordered[nextPublishOffset], out var pendingSubMesh))
                {
                    pendingSubMesh.model.Meshes.Add(pendingSubMesh.subMesh);
                    nextPublishOffset++;
                }
            }

            if (pending != null)
            {
                ordered = new int[count];
                for (int i = 0; i < count; i++)
                    ordered[i] = meshIndices[i];

                if (batchSubmeshAddsDuringAsyncImport)
                    _meshFinalizeActions.Add(FlushReadySubMeshes);
            }

            for (var i = 0; i < count; i++)
            {
                int localMeshIndex = meshIndices[i];
                _meshProcessRoutines.Add(() => MeshRoutine(localMeshIndex));
            }

            IEnumerable MeshRoutine(int meshIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Mesh mesh = scene.Meshes[meshIndex];
                (XRMesh xrMesh, XRMaterial xrMaterial) = ProcessSubMesh(mesh, scene, dataTransform, cancellationToken);
                    // BindRootMatrix is not set for the Assimp/legacy path:
                    // The legacy GLSL convention outputs root-local positions (via implicit transpose),
                    // and ModelMatrix correctly transforms root-local to world. Pre-multiplying rootBind
                    // would produce world-space skinning output, causing double-transformation with ModelMatrix.
                _meshes.Add(xrMesh);

                SubMesh subMesh = new(new SubMeshLOD(xrMaterial, xrMesh, 0.0f)
                {
                    GenerateAsync = importedRenderersGenerateAsync,
                })
                {
                    Name = mesh.Name,
                    RootTransform = rootTransform
                };

                Model targetModel = ResolveTargetModel(meshIndex);

                if (pending != null)
                {
                    pending[meshIndex] = (targetModel, subMesh);
                    if (!batchSubmeshAddsDuringAsyncImport)
                        Engine.EnqueueSwapTask(FlushReadySubMeshes);
                }
                else
                {
                    targetModel.Meshes.Add(subMesh);
                }

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
            var xrMaterial = ResolveMaterial(mesh, scene, cancellationToken);
            return (xrMesh, xrMaterial);
        }

        private XRMaterial ResolveMaterial(Mesh mesh, AScene scene, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Lazy<XRMaterial> lazyMaterial = _materialCacheByIndex.GetOrAdd(
                mesh.MaterialIndex,
                materialIndex => new Lazy<XRMaterial>(
                    () => ProcessMaterial(materialIndex, mesh.Name, scene, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                return lazyMaterial.Value;
            }
            catch
            {
                _materialCacheByIndex.TryRemove(new KeyValuePair<int, Lazy<XRMaterial>>(mesh.MaterialIndex, lazyMaterial));
                throw;
            }
        }

        private unsafe XRMaterial ProcessMaterial(int materialIndex, string meshName, AScene scene, CancellationToken cancellationToken)
        {
            using var t = Engine.Profiler.Start($"Processing material for {meshName}");

            cancellationToken.ThrowIfCancellationRequested();

            Material matInfo = scene.Materials[materialIndex];
            List<TextureSlot> textures = [];
            HashSet<TextureType> discoveredTypes = [];
            foreach (TextureType type in Enum.GetValues<TextureType>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type == default || !discoveredTypes.Add(type))
                    continue;

                var maps = LoadMaterialTextures(matInfo, type);
                if (maps.Count > 0)
                    textures.AddRange(maps);
            }
            ReadProperties(matInfo, out string name, out TextureFlags flags, out ShadingMode mode, out var propDic);
            XRMaterial material = _materialFactory(SourceFilePath, name, textures, flags, mode, propDic);
            _materials.Add(material);
            return material;
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
