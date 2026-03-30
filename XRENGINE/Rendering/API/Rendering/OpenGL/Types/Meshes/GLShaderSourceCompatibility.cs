using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering.OpenGL
{
    internal static partial class GLShaderSourceCompatibility
    {
        private const string CompatibilityComment = "// Auto-injected gl_PerVertex redeclaration for separable OpenGL program compatibility.";

        [GeneratedRegex(@"\bin\s+gl_PerVertex\b", RegexOptions.Compiled)]
        private static partial Regex InGlPerVertexRegex();

        [GeneratedRegex(@"\bout\s+gl_PerVertex\b", RegexOptions.Compiled)]
        private static partial Regex OutGlPerVertexRegex();

        public static string InjectMissingGLPerVertexBlocks(string source, EShaderType shaderType, bool separableProgram)
        {
            if (!separableProgram || string.IsNullOrWhiteSpace(source))
                return source;

            string newLine = DetectNewLine(source);

            string? inputBlock = NeedsInputBlock(shaderType) && !InGlPerVertexRegex().IsMatch(source)
                ? BuildInputBlock(shaderType, newLine)
                : null;

            string? outputBlock = NeedsOutputBlock(shaderType) && !OutGlPerVertexRegex().IsMatch(source)
                ? BuildOutputBlock(shaderType, newLine)
                : null;

            if (inputBlock is null && outputBlock is null)
                return source;

            int insertAt = FindInsertionIndex(source);

            StringBuilder builder = new(source.Length + 256);
            builder.Append(source, 0, insertAt);
            builder.Append(CompatibilityComment).Append(newLine);

            if (inputBlock is not null)
                builder.Append(inputBlock).Append(newLine).Append(newLine);

            if (outputBlock is not null)
                builder.Append(outputBlock).Append(newLine).Append(newLine);

            builder.Append(source, insertAt, source.Length - insertAt);
            return builder.ToString();
        }

        private static bool NeedsInputBlock(EShaderType shaderType)
            => shaderType is EShaderType.Geometry or EShaderType.TessControl or EShaderType.TessEvaluation;

        private static bool NeedsOutputBlock(EShaderType shaderType)
            => shaderType is EShaderType.Vertex or EShaderType.Geometry or EShaderType.TessControl or EShaderType.TessEvaluation;

        private static string BuildInputBlock(EShaderType shaderType, string newLine)
            => shaderType switch
            {
                EShaderType.Geometry or EShaderType.TessControl or EShaderType.TessEvaluation
                    => string.Join(newLine, [
                        "in gl_PerVertex",
                        "{",
                        "    vec4 gl_Position;",
                        "    float gl_PointSize;",
                        "    float gl_ClipDistance[];",
                        "} gl_in[];"
                    ]),
                _ => string.Empty,
            };

        private static string BuildOutputBlock(EShaderType shaderType, string newLine)
            => shaderType switch
            {
                EShaderType.Vertex or EShaderType.Geometry or EShaderType.TessEvaluation
                    => string.Join(newLine, [
                        "out gl_PerVertex",
                        "{",
                        "    vec4 gl_Position;",
                        "    float gl_PointSize;",
                        "    float gl_ClipDistance[];",
                        "};"
                    ]),
                EShaderType.TessControl
                    => string.Join(newLine, [
                        "out gl_PerVertex",
                        "{",
                        "    vec4 gl_Position;",
                        "    float gl_PointSize;",
                        "    float gl_ClipDistance[];",
                        "} gl_out[];"
                    ]),
                _ => string.Empty,
            };

        private static int FindInsertionIndex(string source)
        {
            int index = 0;
            while (index < source.Length)
            {
                int lineEnd = source.IndexOf('\n', index);
                if (lineEnd < 0)
                    return source.Length;

                int nextLineStart = lineEnd + 1;
                int lineLength = lineEnd > index && source[lineEnd - 1] == '\r'
                    ? lineEnd - index - 1
                    : lineEnd - index;

                string line = source.Substring(index, lineLength);
                if (!IsPreambleLine(line))
                    return index;

                index = nextLineStart;
            }

            return source.Length;
        }

        private static bool IsPreambleLine(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0)
                return true;

            if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) ||
                trimmed.StartsWith("*/", StringComparison.Ordinal))
            {
                return true;
            }

            if (!trimmed.StartsWith('#'))
                return false;

            return trimmed.StartsWith("#version", StringComparison.Ordinal) ||
                trimmed.StartsWith("#extension", StringComparison.Ordinal) ||
                trimmed.StartsWith("#line", StringComparison.Ordinal);
        }

        private static string DetectNewLine(string source)
            => source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }
}