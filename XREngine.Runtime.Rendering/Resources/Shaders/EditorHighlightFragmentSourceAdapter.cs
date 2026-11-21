using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

/// <summary>
/// Adds an editor mask output to a forward GLSL fragment entry without changing
/// its color, depth, sample mask, discard, or early-return behavior.
/// </summary>
public static partial class EditorHighlightFragmentSourceAdapter
{
    public const uint AbiVersion = 2;
    public const string FlagsUniformName = "XRENGINE_EditorHighlightFlags";
    public const string MaskOutputName = "XRENGINE_EditorHighlightMaskOut";
    private const string UserEntryName = "XRENGINE_EditorHighlightUserMain";

    /// <summary>
    /// Adapts resolved GLSL during program creation. The caller must validate the
    /// active compiled output interface and retain this ABI in the program key.
    /// </summary>
    public static bool TryAdapt(string resolvedGlsl, uint maskLocation,
        out string adaptedGlsl, out string failureReason)
    {
        adaptedGlsl = string.Empty;
        failureReason = string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedGlsl))
        {
            failureReason = "The editor mask requires a GLSL fragment source.";
            return false;
        }
        if (maskLocation == 0 || maskLocation > int.MaxValue)
        {
            failureReason = "The editor mask requires a separate nonzero color location.";
            return false;
        }

        string code = MaskCommentsAndStrings(resolvedGlsl);
        Match version = VersionDirective().Match(code);
        if (!version.Success || !MainDefinition().IsMatch(code))
        {
            failureReason = "The editor mask requires a GLSL version directive and a main entry.";
            return false;
        }
        if (MainMacro().IsMatch(code))
        {
            failureReason = "The editor mask cannot adapt a source-defined main macro.";
            return false;
        }
        if (ReservedIdentifier().IsMatch(code))
        {
            failureReason = "The fragment source uses a reserved editor mask identifier.";
            return false;
        }
        // Resolve includes before this step. Keep conditional source entries intact;
        // only the real GLSL compiler can identify the active output declarations.
        int insertion = version.Index + version.Length;
        StringBuilder source = new(resolvedGlsl.Length + 640);
        source.Append(resolvedGlsl.AsSpan(0, insertion));
        source.Append("\n#define main ").AppendLine(UserEntryName);
        source.Append(resolvedGlsl.AsSpan(insertion));
        source.AppendLine("\n#undef main");
        source.Append("layout(location = ").Append(maskLocation.ToString(CultureInfo.InvariantCulture))
            .Append(") out vec4 ").Append(MaskOutputName).AppendLine(";");
        source.Append("uniform uint ").Append(FlagsUniformName)
            .AppendLine("; // XRENGINE_FREQUENCY(Object)");
        source.AppendLine("void main()\n{");
        source.Append("    ").Append(MaskOutputName).AppendLine(" = vec4(");
        source.Append("        (").Append(FlagsUniformName).AppendLine(" & 1u) != 0u ? 1.0 : 0.0,");
        source.Append("        (").Append(FlagsUniformName).AppendLine(" & 2u) != 0u ? 1.0 : 0.0,");
        source.Append("        (").Append(FlagsUniformName).AppendLine(" & 4u) != 0u ? 1.0 : 0.0, 1.0);");
        source.Append("    ").Append(UserEntryName).AppendLine("();\n}");
        adaptedGlsl = source.ToString();
        return true;
    }

    private static string MaskCommentsAndStrings(string source)
    {
        char[] code = source.ToCharArray();
        for (int index = 0; index < code.Length; ++index)
        {
            char current = source[index];
            bool lineComment = current == '/' && index + 1 < code.Length && source[index + 1] == '/';
            bool blockComment = current == '/' && index + 1 < code.Length && source[index + 1] == '*';
            if (!lineComment && !blockComment && current != '"')
                continue;

            int end = index + (current == '"' ? 1 : 2);
            while (end < code.Length)
            {
                if (lineComment && source[end] is '\r' or '\n')
                    break;
                if (blockComment && source[end] == '*' && end + 1 < code.Length && source[end + 1] == '/')
                {
                    end += 2;
                    break;
                }
                if (current == '"')
                {
                    if (source[end] == '\\')
                    {
                        end = Math.Min(end + 2, code.Length);
                        continue;
                    }
                    if (source[end] == '"')
                    {
                        ++end;
                        break;
                    }
                }
                ++end;
            }
            for (int masked = index; masked < end; ++masked)
            {
                if (code[masked] is not ('\r' or '\n'))
                    code[masked] = ' ';
            }
            index = end - 1;
        }
        return new string(code);
    }

    [GeneratedRegex(@"\A\uFEFF?\s*#\s*version[^\r\n]*(?:\r?\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionDirective();

    [GeneratedRegex(@"\bvoid\s+main\s*\(\s*(?:void\s*)?\)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex MainDefinition();

    [GeneratedRegex(@"^[ \t]*#[ \t]*(?:define|undef)[ \t]+main\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MainMacro();

    [GeneratedRegex(@"\bXRENGINE_EditorHighlight(?:Flags|MaskOut|UserMain)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedIdentifier();

}
