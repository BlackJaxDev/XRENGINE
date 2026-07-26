
using System.Collections.Concurrent;
using System.IO;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering
{
    [XRAssetInspector("XREngine.Editor.AssetEditors.XRShaderInspector")]
    [XRAssetContextMenu("Open Shader Editor...", "XREngine.Editor.UI.Tools.ShaderAssetMenuActions", "OpenInShaderEditor")]
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRShaderImportOptions),
        "glsl", "shader",
        "frag", "vert", "geom", "tesc", "tese", "comp", "task", "mesh",
        "fs", "vs", "gs", "tcs", "tes", "cs", "ts", "ms")]
    public class XRShader : GenericRenderObject
    {
        private readonly object _resolvedSourceCacheLock = new();
        private string? _resolvedSourceCache;
        private string? _resolvedSourceCachePath;
        private string? _resolvedSourceCacheText;
        private ResolvedShaderSource? _resolvedSourcePayloadCache;
        private string? _optimizedSourceCache;
        private string? _optimizedSourceCachePath;
        private string? _optimizedSourceCacheText;
        private ShaderSourceFileDependency[]? _resolvedSourceDependencies;
        private ShaderUiManifest? _uiManifestCache;
        private string? _uiManifestCachePath;
        private string? _uiManifestCacheText;
        private long _sourceRevision;

        public event Action<XRShader>? SourceChanged;

        /// <summary>
        /// Monotonic logical source revision used to reject stale asynchronous compile results.
        /// </summary>
        public long SourceRevision => Interlocked.Read(ref _sourceRevision);

        internal EShaderType _type = EShaderType.Fragment;
        public EShaderType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        private TextFile _source = string.Empty;
        public TextFile Source
        {
            get => _source;
            set => SetField(ref _source, value);
        }

        private bool _generateAsync = false;
        public bool GenerateAsync
        {
            get => _generateAsync;
            set => SetField(ref _generateAsync, value);
        }

        private bool _isGeneratedUberVariant = false;
        public bool IsGeneratedUberVariant
        {
            get => _isGeneratedUberVariant;
            set => SetField(ref _isGeneratedUberVariant, value);
        }

        private ulong _generatedUberVariantHash = 0;
        public ulong GeneratedUberVariantHash
        {
            get => _generatedUberVariantHash;
            set => SetField(ref _generatedUberVariantHash, value);
        }

        public XRShader() { }
        public XRShader(EShaderType type) => Type = type;
        public XRShader(EShaderType type, TextFile source)
        {
            Type = type;
            Source = source;
            //Debug.Out($"Loaded shader of type {type} from {source.FilePath}{Environment.NewLine}{source.Text}");
        }

        public static EShaderType ResolveType(string extension)
        {
            extension = extension.ToLowerInvariant();

            if (extension.StartsWith('.'))
                extension = extension[1..];

            return extension switch
            {
                "vs" or "vert" => EShaderType.Vertex,
                "gs" or "geom" => EShaderType.Geometry,
                "tcs" or "tesc" => EShaderType.TessControl,
                "tes" or "tese" => EShaderType.TessEvaluation,
                "cs" or "comp" => EShaderType.Compute,
                "ts" or "task" => EShaderType.Task,
                "ms" or "mesh" => EShaderType.Mesh,
                _ => EShaderType.Fragment,
            };
        }

        /// <summary>
        /// Loads a shader from common engine shaders.
        /// </summary>
        /// <param name="relativePath"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static XRShader EngineShader(string relativePath, EShaderType type)
            => ShaderHelper.LoadEngineShader(relativePath, type);

        /// <summary>
        /// Loads a shader from common engine shaders asynchronously.
        /// </summary>
        /// <param name="relativePath"></param>
        /// <param name="type"></param>
        /// <returns></returns>
        public static async Task<XRShader?> EngineShaderAsync(string relativePath, EShaderType type)
            => await ShaderHelper.LoadEngineShaderAsync(relativePath, type);

        public override void Reload(string filePath)
        {
            XRShader? loaded = RuntimeShaderServices.Current?.LoadAsset<XRShader>(filePath);
            if (loaded is null)
            {
                RuntimeShaderServices.Current?.LogWarning($"Failed to reload shader asset '{filePath}'.");
                return;
            }

            Type = loaded.Type;
            Source = loaded.Source;
            GenerateAsync = loaded.GenerateAsync;
            IsGeneratedUberVariant = loaded.IsGeneratedUberVariant;
            GeneratedUberVariantHash = loaded.GeneratedUberVariantHash;
        }
        public override bool Load3rdParty(string filePath)
        {
            Type = ResolveType(Path.GetExtension(filePath));
            TextFile file = new(filePath);
            file.LoadText(filePath);
            Source = file;
            IsGeneratedUberVariant = false;
            GeneratedUberVariantHash = 0;
            return true;
        }

        public override bool Import3rdParty(string filePath, object? importOptions)
        {
            bool ok = Load3rdParty(filePath);
            if (!ok)
                return false;

            if (importOptions is XREngine.Data.XRShaderImportOptions options)
                GenerateAsync = options.GenerateAsync;

            return true;
        }
        public override async Task<bool> Load3rdPartyAsync(string filePath)
        {
            Type = ResolveType(Path.GetExtension(filePath));
            TextFile file = new(filePath);
            await file.LoadTextAsync(filePath);
            Source = file;
            IsGeneratedUberVariant = false;
            GeneratedUberVariantHash = 0;
            return true;
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Source):
                        if (field is TextFile previousSource)
                            previousSource.TextChanged -= OnSourceTextChanged;
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Type):
                    InvalidateResolvedSourceCache();
                    Interlocked.Increment(ref _sourceRevision);
                    MarkDirty();
                    SourceChanged?.Invoke(this);
                    break;
                case nameof(Source):
                    InvalidateResolvedSourceCache();
                    if (field is TextFile newSource)
                        newSource.TextChanged += OnSourceTextChanged;
                    OnSourceTextChanged();
                    break;
            }
        }

        private void OnSourceTextChanged()
        {
            InvalidateResolvedSourceCache();
            Interlocked.Increment(ref _sourceRevision);

            //When the source text changes, we need to mark the shader as dirty so it can be recompiled
            MarkDirty();
            SourceChanged?.Invoke(this);
        }

        internal void NotifySourceDependencyChanged(string reason)
        {
            InvalidateResolvedSourceCache();
            Interlocked.Increment(ref _sourceRevision);
            MarkDirty();
            RuntimeShaderServices.Current?.LogWarning(
                $"Shader dependency changed for '{Name ?? FilePath ?? "UnnamedShader"}': {reason}");
            SourceChanged?.Invoke(this);
        }

        private void InvalidateResolvedSourceCache()
        {
            lock (_resolvedSourceCacheLock)
            {
                _resolvedSourceCache = null;
                _resolvedSourceCachePath = null;
                _resolvedSourceCacheText = null;
                _resolvedSourcePayloadCache = null;
                _optimizedSourceCache = null;
                _optimizedSourceCachePath = null;
                _optimizedSourceCacheText = null;
                _resolvedSourceDependencies = null;
                _uiManifestCache = null;
                _uiManifestCachePath = null;
                _uiManifestCacheText = null;
            }

            _existingUniforms.Clear();
        }

        public ShaderUiManifest GetUiManifest(bool logFailures = true)
        {
            TryGetUiManifest(out ShaderUiManifest manifest, logFailures);
            return manifest;
        }

        public bool TryGetUiManifest(out ShaderUiManifest manifest, bool logFailures = true)
        {
            string sourceText = Source?.Text ?? string.Empty;
            string? sourcePath = Source?.FilePath;

            lock (_resolvedSourceCacheLock)
            {
                if (_uiManifestCache is not null &&
                    string.Equals(_uiManifestCacheText, sourceText, StringComparison.Ordinal) &&
                    string.Equals(_uiManifestCachePath, sourcePath, StringComparison.Ordinal) &&
                    ShaderSourceResolver.AreDependenciesCurrent(_resolvedSourceDependencies))
                {
                    manifest = _uiManifestCache;
                    return true;
                }
            }

            bool resolved = TryGetResolvedSource(out string resolvedSource, annotateIncludes: false, logFailures: logFailures);
            manifest = ShaderUiManifestParser.Parse(resolvedSource, sourcePath);

            if (resolved)
            {
                lock (_resolvedSourceCacheLock)
                {
                    _uiManifestCache = manifest;
                    _uiManifestCacheText = sourceText;
                    _uiManifestCachePath = sourcePath;
                }
            }

            return resolved;
        }

        public string GetResolvedSource(bool annotateIncludes = false)
        {
            TryGetResolvedSource(out string resolvedSource, annotateIncludes, logFailures: true);
            return resolvedSource;
        }

        public bool TryGetResolvedSource(out string resolvedSource, bool annotateIncludes = false, bool logFailures = true)
        {
            bool success = TryGetResolvedShaderSource(out ResolvedShaderSource resolved, annotateIncludes, logFailures);
            resolvedSource = resolved.ResolvedSource;
            return success;
        }

        public ResolvedShaderSource GetResolvedShaderSource(bool annotateIncludes = false)
        {
            TryGetResolvedShaderSource(out ResolvedShaderSource resolvedSource, annotateIncludes, logFailures: true);
            return resolvedSource;
        }

        public bool TryGetResolvedShaderSource(out ResolvedShaderSource resolvedSource, bool annotateIncludes = false, bool logFailures = true)
        {
            string sourceText = Source?.Text ?? string.Empty;
            string? sourcePath = Source?.FilePath;

            if (!annotateIncludes)
            {
                lock (_resolvedSourceCacheLock)
                {
                    if (_resolvedSourcePayloadCache is not null &&
                        string.Equals(_resolvedSourceCacheText, sourceText, StringComparison.Ordinal) &&
                        string.Equals(_resolvedSourceCachePath, sourcePath, StringComparison.Ordinal) &&
                        ShaderSourceResolver.AreDependenciesCurrent(_resolvedSourceDependencies))
                    {
                        resolvedSource = _resolvedSourcePayloadCache;
                        return true;
                    }
                }
            }

            try
            {
                ResolvedShaderSource resolvedPayload = ShaderSourceResolver.ResolveSourcePayload(
                    sourceText,
                    sourcePath,
                    annotateIncludes: annotateIncludes);
                resolvedSource = resolvedPayload;

                if (!annotateIncludes)
                {
                    lock (_resolvedSourceCacheLock)
                    {
                        _resolvedSourceCache = resolvedPayload.ResolvedSource;
                        _resolvedSourcePayloadCache = resolvedPayload;
                        _resolvedSourceCacheText = sourceText;
                        _resolvedSourceCachePath = sourcePath;
                        _resolvedSourceDependencies = resolvedPayload.FileDependencies;
                    }

                    ShaderSourceDependencyIndex.Update(this, sourcePath, resolvedPayload.FileDependencies);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (logFailures)
                    RuntimeShaderServices.Current?.LogWarning($"Failed to resolve shader source for '{Name ?? FilePath ?? "UnnamedShader"}': {ex.Message}");

                resolvedSource = new ResolvedShaderSource(
                    sourcePath,
                    sourceText,
                    sourceText,
                    [],
                    [],
                    ShaderSourceMacroSummary.Scan(sourceText));
                return false;
            }
        }

        public string GetOptimizedSource(bool annotateIncludes = false)
        {
            TryGetOptimizedSource(out string optimizedSource, annotateIncludes, logFailures: true);
            return optimizedSource;
        }

        public bool TryGetOptimizedSource(
            out string optimizedSource,
            bool annotateIncludes = false,
            bool logFailures = true,
            ResolvedShaderSourceOptimizationOptions? options = null)
        {
            string sourceText = Source?.Text ?? string.Empty;
            string? sourcePath = Source?.FilePath;
            bool useDefaultCache = !annotateIncludes && options is null;

            if (useDefaultCache)
            {
                lock (_resolvedSourceCacheLock)
                {
                    if (_optimizedSourceCache is not null &&
                        string.Equals(_optimizedSourceCacheText, sourceText, StringComparison.Ordinal) &&
                        string.Equals(_optimizedSourceCachePath, sourcePath, StringComparison.Ordinal) &&
                        ShaderSourceResolver.AreDependenciesCurrent(_resolvedSourceDependencies))
                    {
                        optimizedSource = _optimizedSourceCache;
                        return true;
                    }
                }
            }

            bool resolved = TryGetResolvedSource(out string resolvedSource, annotateIncludes, logFailures);
            try
            {
                ResolvedShaderSourceOptimizationResult result = ResolvedShaderSourceOptimizer.Optimize(resolvedSource, options);
                optimizedSource = result.Source;

                if (useDefaultCache && resolved)
                {
                    lock (_resolvedSourceCacheLock)
                    {
                        _optimizedSourceCache = optimizedSource;
                        _optimizedSourceCacheText = sourceText;
                        _optimizedSourceCachePath = sourcePath;
                    }
                }

                return resolved;
            }
            catch (Exception ex)
            {
                if (logFailures)
                    RuntimeShaderServices.Current?.LogWarning($"Failed to optimize shader source for '{Name ?? FilePath ?? "UnnamedShader"}': {ex.Message}");

                optimizedSource = resolvedSource;
                return resolved;
            }
        }

        public enum EExtensionBehavior
        {
            Enable,
            Require,
            Warn,
            Disable
        }

        /// <summary>
        /// Checks if the shader utilizes a specific extension.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="allowedBehaviors"></param>
        /// <returns></returns>
        public bool HasExtension(string name, params EExtensionBehavior[] allowedBehaviors)
        {
            if (!TryGetExtensionBehavior(name, out EExtensionBehavior behavior))
                return false;

            if (allowedBehaviors.Length == 0)
                return true;

            for (int i = 0; i < allowedBehaviors.Length; i++)
                if (allowedBehaviors[i] == behavior)
                    return true;
            return false;
        }

        /// <summary>
        /// Allocation-free overload for the common single-behavior query used while
        /// selecting a mesh renderer version.
        /// </summary>
        public bool HasExtension(string name, EExtensionBehavior allowedBehavior)
            => TryGetExtensionBehavior(name, out EExtensionBehavior behavior) &&
               behavior == allowedBehavior;

        private bool TryGetExtensionBehavior(string name, out EExtensionBehavior behavior)
        {
            behavior = EExtensionBehavior.Disable;
            string? text = Source?.Text;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name))
                return false;

            const string directive = "#extension";
            int searchStart = 0;
            while (searchStart < text.Length)
            {
                int directiveIndex = text.IndexOf(directive, searchStart, StringComparison.OrdinalIgnoreCase);
                if (directiveIndex < 0)
                    return false;

                int lineEnd = directiveIndex + directive.Length;
                while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
                    lineEnd++;

                ReadOnlySpan<char> line = text.AsSpan(
                    directiveIndex + directive.Length,
                    lineEnd - directiveIndex - directive.Length).TrimStart();
                if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    int cursor = name.Length;
                    if (cursor == line.Length || char.IsWhiteSpace(line[cursor]) || line[cursor] == ':')
                    {
                        int colon = line[cursor..].IndexOf(':');
                        if (colon >= 0)
                        {
                            ReadOnlySpan<char> behaviorText = line[(cursor + colon + 1)..].Trim();
                            if (behaviorText.Equals("enable", StringComparison.OrdinalIgnoreCase))
                                behavior = EExtensionBehavior.Enable;
                            else if (behaviorText.Equals("require", StringComparison.OrdinalIgnoreCase))
                                behavior = EExtensionBehavior.Require;
                            else if (behaviorText.Equals("warn", StringComparison.OrdinalIgnoreCase))
                                behavior = EExtensionBehavior.Warn;
                            else if (behaviorText.Equals("disable", StringComparison.OrdinalIgnoreCase))
                                behavior = EExtensionBehavior.Disable;
                            else
                                return false;
                            return true;
                        }
                    }
                }

                searchStart = lineEnd + 1;
            }

            return false;
        }

        public ConcurrentDictionary<string, bool> _existingUniforms = new();

        public bool HasUniform(string uniformName)
        {
            //Check the cache first
            if (_existingUniforms.TryGetValue(uniformName, out bool exists))
                return exists;

            if (Source is null)
                return false;

            if (!TryGetOptimizedSource(out string text, logFailures: false) || string.IsNullOrEmpty(text))
                return false;

            //If the uniform name has a . in it, it's in a struct
            if (uniformName.Contains('.'))
            {
                //Split the uniform name into parts
                string[] parts = uniformName.Split('.');
                if (parts.Length < 2)
                    return false;

                //Search for the struct declaration
                int index = text.IndexOf($"struct {parts[0]}", StringComparison.InvariantCultureIgnoreCase);
                if (index == -1)
                    return false;

                //Search for the uniform declaration within the struct
                index = text.IndexOf($"uniform {parts[1]}", index, StringComparison.InvariantCultureIgnoreCase);
                return index != -1;
            }
            else
            {
                // Check for "uniform <type> <name>" pattern - the name follows the type
                // Match patterns like "uniform vec3 CameraPosition" or "uniform DirLight DirectionalLights[2]"
                bool found = text.Contains($"uniform {uniformName}", StringComparison.InvariantCultureIgnoreCase);
                if (!found)
                {
                    // Also check for the uniform name appearing after "uniform <type> "
                    // This handles "uniform SomeType uniformName" declarations
                    found = System.Text.RegularExpressions.Regex.IsMatch(
                        text, 
                        $@"uniform\s+\w+\s+{System.Text.RegularExpressions.Regex.Escape(uniformName)}\s*[;\[=]",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                _existingUniforms[uniformName] = found;
                return found;
            }
        }
    }
}
