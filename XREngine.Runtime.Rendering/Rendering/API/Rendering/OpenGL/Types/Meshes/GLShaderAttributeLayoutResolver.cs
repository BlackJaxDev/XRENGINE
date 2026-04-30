using System.Globalization;
using System.Text.RegularExpressions;

namespace XREngine.Rendering.OpenGL
{
    internal static partial class GLShaderAttributeLayoutResolver
    {
        [GeneratedRegex(@"layout\s*\((?<layout>[^)]*)\)\s*in\s+(?<declaration>[^;]+);", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex InputDeclarationRegex();

        [GeneratedRegex(@"\blocation\s*=\s*(?<loc>\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex LocationRegex();

        [GeneratedRegex(@"//.*?$", RegexOptions.Compiled | RegexOptions.Multiline)]
        private static partial Regex SingleLineCommentRegex();

        [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline)]
        private static partial Regex MultiLineCommentRegex();

        public static IReadOnlyDictionary<string, int> ResolveVertexInputLocations(IEnumerable<XRShader> shaders)
        {
            Dictionary<string, int> locations = new(StringComparer.Ordinal);

            foreach (XRShader shader in shaders)
            {
                if (shader is null || shader.Type != EShaderType.Vertex)
                    continue;

                foreach (var pair in ResolveVertexInputLocations(GetShaderSource(shader)))
                    locations.TryAdd(pair.Key, pair.Value);
            }

            return locations;
        }

        public static IReadOnlyDictionary<string, int> ResolveVertexInputLocations(string source)
        {
            Dictionary<string, int> locations = new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(source))
                return locations;

            string uncommentedSource = StripComments(source);
            MatchCollection matches = InputDeclarationRegex().Matches(uncommentedSource);
            foreach (Match match in matches)
            {
                Match locationMatch = LocationRegex().Match(match.Groups["layout"].Value);
                if (!locationMatch.Success)
                    continue;

                if (!int.TryParse(locationMatch.Groups["loc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int location) || location < 0)
                    continue;

                string name = ResolveVariableName(match.Groups["declaration"].Value);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("gl_", StringComparison.Ordinal))
                    continue;

                locations.TryAdd(name, location);
            }

            return locations;
        }

        private static string GetShaderSource(XRShader shader)
        {
            if (shader.TryGetResolvedSource(out string resolvedSource, logFailures: false) && !string.IsNullOrWhiteSpace(resolvedSource))
                return resolvedSource;

            return shader.Source?.Text ?? string.Empty;
        }

        private static string StripComments(string source)
        {
            string withoutBlockComments = MultiLineCommentRegex().Replace(source, string.Empty);
            return SingleLineCommentRegex().Replace(withoutBlockComments, string.Empty);
        }

        private static string ResolveVariableName(string declaration)
        {
            string trimmed = declaration.Trim();
            if (trimmed.Length == 0)
                return string.Empty;

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex >= 0)
                trimmed = trimmed[..equalsIndex].TrimEnd();

            int arrayIndex = trimmed.IndexOf('[');
            if (arrayIndex >= 0)
                trimmed = trimmed[..arrayIndex].TrimEnd();

            string[] tokens = trimmed.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length == 0 ? string.Empty : tokens[^1];
        }
    }
}