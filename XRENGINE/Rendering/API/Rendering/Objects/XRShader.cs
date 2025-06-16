
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering
{
    [XR3rdPartyExtensions(
        "glsl",
        "frag", "vert", "geom", "tesc", "tese", "comp",
        "fs", "vs", "gs", "tcs", "tes", "cs")]
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
            Load3rdParty(filePath);
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
    }
}
