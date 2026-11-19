using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Rendering;

public static partial class EditorHighlightFragmentSourceAdapter
{
    public const string PickingKeyUniformName = "XRENGINE_EditorPickingKey";
    public const string PickingCoverageUniformName = "XRENGINE_EditorPickingCoverage";
    public const string PickingOutputName = "XRENGINE_EditorPickingOut";
    public const string PickingColorFunctionName = "XRENGINE_EditorPickingReadColor";

    /// <summary>
    /// Adds a mesh identity output after the authored entry returns. Discard and
    /// sample coverage remain part of the same fragment invocation.
    /// </summary>
    public static bool TryAdaptWithPicking(string resolvedGlsl, uint maskLocation, uint pickingLocation,
        out string adaptedGlsl, out string failureReason)
    {
        adaptedGlsl = string.Empty;
        if (resolvedGlsl.Contains("XRENGINE_EditorPicking", StringComparison.Ordinal))
        {
            failureReason = "The fragment source uses a reserved editor picking identifier.";
            return false;
        }
        if (pickingLocation == 0 || pickingLocation == maskLocation || pickingLocation > int.MaxValue)
        {
            failureReason = "Editor picking requires a separate nonzero color location.";
            return false;
        }
        if (!TryAdapt(resolvedGlsl, maskLocation, out string maskedSource, out failureReason))
            return false;

        int wrapper = maskedSource.LastIndexOf("void main()", StringComparison.Ordinal);
        StringBuilder source = new(maskedSource.Length + 1400);
        source.Append(maskedSource.AsSpan(0, wrapper));
        source.Append("layout(location = ").Append(pickingLocation.ToString(CultureInfo.InvariantCulture))
            .Append(") out vec4 ").Append(PickingOutputName).AppendLine(";");
        source.Append("uniform uint ").Append(PickingKeyUniformName)
            .AppendLine("; // XRENGINE_FREQUENCY(Object)");
        source.Append("uniform uint ").Append(PickingCoverageUniformName)
            .AppendLine("; // XRENGINE_FREQUENCY(Object)");
        source.Append("vec4 ").Append(PickingColorFunctionName).AppendLine("();");
        source.Append(maskedSource.AsSpan(wrapper, maskedSource.LastIndexOf('}') - wrapper));
        source.Append("    vec4 editorColor = ").Append(PickingColorFunctionName).AppendLine("();");
        source.Append("    uint editorCoverage = ").Append(PickingCoverageUniformName).AppendLine(";");
        source.AppendLine("    uint editorChannels = editorCoverage >> 8u;");
        source.AppendLine("    editorCoverage &= 255u;");
        source.AppendLine("    bool editorRgbVisible = ((editorChannels & 1u) != 0u && editorColor.r != 0.0) ||");
        source.AppendLine("        ((editorChannels & 2u) != 0u && editorColor.g != 0.0) ||");
        source.AppendLine("        ((editorChannels & 4u) != 0u && editorColor.b != 0.0);");
        source.AppendLine("    bool editorVisible = editorCoverage == 0u ||");
        source.AppendLine("        (editorCoverage == 1u && editorColor.a != 0.0) ||");
        source.AppendLine("        (editorCoverage == 2u && (editorColor.a != 0.0 || editorRgbVisible)) ||");
        source.AppendLine("        (editorCoverage == 4u && editorColor.a != 0.0 && editorRgbVisible) ||");
        source.AppendLine("        (editorCoverage == 5u && editorRgbVisible);");
        source.Append("    uint editorKey = ").Append(PickingKeyUniformName).AppendLine(";");
        source.Append("    ").Append(PickingOutputName)
            .AppendLine(" = vec4(float(editorKey & 65535u) / 65535.0, float(editorKey >> 16u) / 65535.0,");
        source.AppendLine("        2.0 / 65535.0, editorVisible && editorKey != 0u ? 1.0 : 0.0);");
        source.AppendLine("}");
        adaptedGlsl = source.ToString();
        return true;
    }

    /// <summary>
    /// Binds the color reader to the active preprocessed scene output. The caller
    /// must compile this exact source and certify its SPIR-V output interface.
    /// </summary>
    public static bool TryFinalizePickingSource(string preprocessedSource,
        out string finalizedSource, out string failureReason)
    {
        finalizedSource = string.Empty;
        failureReason = string.Empty;
        string code = PreprocessorLine().Replace(MaskCommentsAndStrings(preprocessedSource), string.Empty);
        string? colorName = null;
        int depth = 0;
        int start = 0;
        for (int i = 0; i < code.Length; ++i)
        {
            char c = code[i];
            if (c == '{')
                ++depth;
            else if (c == '}')
            {
                --depth;
                if (depth == 0)
                    start = i + 1;
            }
            else if (c == ';' && depth == 0)
            {
                Match output = SceneColorDeclaration().Match(code[start..(i + 1)]);
                if (output.Success)
                {
                    if (colorName is not null)
                    {
                        failureReason = "The editor picking adapter found multiple scene color declarations.";
                        return false;
                    }
                    colorName = output.Groups["name"].Value;
                }
                start = i + 1;
            }
        }
        if (colorName is null)
        {
            failureReason = "Editor picking requires a direct layout(location = 0) out vec4 scene color declaration.";
            return false;
        }
        finalizedSource = preprocessedSource + "\nvec4 " + PickingColorFunctionName +
            "()\n{\n    return " + colorName + ";\n}\n";
        return true;
    }

    [GeneratedRegex(@"^[ \t]*#[^\r\n]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PreprocessorLine();

    [GeneratedRegex(@"\A\s*layout\s*\(\s*location\s*=\s*0[uU]?\s*\)\s*out\s+(?:(?:highp|mediump|lowp)\s+)?vec4\s+(?<name>[A-Za-z_]\w*)\s*;\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SceneColorDeclaration();
}
