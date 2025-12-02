
using System.Collections.Concurrent;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering
{
    [XRAssetInspector("XREngine.Editor.AssetEditors.XRShaderInspector")]
    [XRAssetContextMenu("Optimize Shader...", "XREngine.Editor.UI.Tools.ShaderAssetMenuActions", "OpenInShaderLockingTool")]
    [XRAssetContextMenu("Analyze Shader...", "XREngine.Editor.UI.Tools.ShaderAssetMenuActions", "OpenInShaderAnalyzer")]
    [XR3rdPartyExtensions(
        "glsl", "shader",
        "frag", "vert", "geom", "tesc", "tese", "comp", "task", "mesh",
        "fs", "vs", "gs", "tcs", "tes", "cs", "ts", "ms")]
    public class XRShader : GenericRenderObject
    {
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
            //string ext = Path.GetExtension(filePath);
            //if (string.Equals(ext, ".asset", StringComparison.OrdinalIgnoreCase))
            //{
                // Native asset format - deserialize from YAML
                var loaded = Engine.Assets.Load<XRShader>(filePath);
                if (loaded is not null)
                {
                    Type = loaded.Type;
                    Source = loaded.Source;
                    GenerateAsync = loaded.GenerateAsync;
                }
            //}
            //else
            //{
            //    // 3rd party shader file format
            //    Load3rdParty(filePath);
            //}
        }
        public override bool Load3rdParty(string filePath)
        {
            TextFile file = new();
            file.LoadText(filePath);
            Source = file;
            return true;
        }
        public override async Task<bool> Load3rdPartyAsync(string filePath)
        {
            TextFile file = new();
            await file.LoadTextAsync(filePath);
            Source = file;
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
                case nameof(Source):
                    if (field is TextFile newSource)
                        newSource.TextChanged += OnSourceTextChanged;
                    OnSourceTextChanged();
                    break;
            }
        }

        private void OnSourceTextChanged()
        {
            //When the source text changes, we need to mark the shader as dirty so it can be recompiled
            MarkDirty();
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
            if (Source is null)
                return false;

            string? text = Source.Text;
            if (text is null)
                return false;

            int index = text.IndexOf($"#extension {name}", StringComparison.InvariantCultureIgnoreCase);
            if (index == -1)
                return false;

            //If the user passes no behaviors, then any behavior is allowed
            if (allowedBehaviors.Length == 0)
                return true;

            int end = text.IndexOf('\r', index);
            if (end == -1)
                return false;

            string line = text[index..end];

            //#extension extension_name​ : behavior​
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return false;

            string behavior = parts[3];
            EExtensionBehavior behaviorEnum = behavior switch
            {
                "enable" => EExtensionBehavior.Enable,
                "require" => EExtensionBehavior.Require,
                "warn" => EExtensionBehavior.Warn,
                "disable" => EExtensionBehavior.Disable,
                _ => EExtensionBehavior.Disable
            };

            return allowedBehaviors.Contains(behaviorEnum);
        }

        public ConcurrentDictionary<string, bool> _existingUniforms = new();

        public bool HasUniform(string uniformName)
        {
            //Check the cache first
            if (_existingUniforms.TryGetValue(uniformName, out bool exists))
                return exists;

            if (Source is null)
                return false;

            string? text = Source.Text;
            if (text is null)
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
                return text.Contains($"uniform {uniformName}", StringComparison.InvariantCultureIgnoreCase);
            }
        }
    }
}
