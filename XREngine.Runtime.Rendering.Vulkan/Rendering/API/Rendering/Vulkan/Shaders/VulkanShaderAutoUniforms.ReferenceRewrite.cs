using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanShaderAutoUniforms
{
    private static readonly Regex UniformReferenceTokenRegex = new(
        "(?<comment>//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/)|" +
        "(?<literal>\"(?:\\\\.|[^\"\\\\])*\")|" +
        "(?<identifier>[A-Za-z_][A-Za-z0-9_]*)|(?<punctuation>[^\\s])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Qualifies global uniform uses without changing struct fields or member access.
    /// </summary>
    private static string RewriteUniformReferences(
        string source,
        IReadOnlyDictionary<string, string> references)
    {
        StringBuilder output = new(source.Length);
        int copiedThrough = 0;
        bool followsMemberAccess = false;
        bool awaitingStructBody = false;
        int structDepth = 0;
        foreach (Match token in UniformReferenceTokenRegex.Matches(source))
        {
            if (token.Groups["comment"].Success)
                continue;

            string text = token.Value;
            bool identifier = token.Groups["identifier"].Success;
            if (identifier && text == "struct" && !followsMemberAccess)
                awaitingStructBody = true;
            if (token.Groups["punctuation"].Success)
            {
                if (text == "{" && (awaitingStructBody || structDepth > 0))
                {
                    structDepth++;
                    awaitingStructBody = false;
                }
                else if (text == "}" && structDepth > 0)
                    structDepth--;
                else if (text == ";")
                    awaitingStructBody = false;
            }

            if (structDepth == 0 && !awaitingStructBody && !followsMemberAccess && identifier &&
                references.TryGetValue(token.Value, out string? qualified))
            {
                output.Append(source, copiedThrough, token.Index - copiedThrough);
                output.Append(qualified);
                copiedThrough = token.Index + token.Length;
            }

            // Whitespace and comments do not separate a dot from its field name.
            followsMemberAccess = token.Groups["punctuation"].Success && text == ".";
        }
        output.Append(source, copiedThrough, source.Length - copiedThrough);
        return output.ToString();
    }
}
