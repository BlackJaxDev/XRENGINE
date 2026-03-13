using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

public static class ForwardDepthNormalVariantFactory
{
    private const string ForwardLightingFunction = "XRENGINE_CalculateForwardLighting";

    private static readonly Regex OutputDeclarationRegex = new(
        "^\\s*layout\\s*\\(\\s*location\\s*=\\s*\\d+\\s*\\)\\s*out\\s+[^;]+;\\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ForwardLightingSnippetRegex = new(
        "^\\s*#pragma\\s+snippet\\s+\"(?:ForwardLighting|AmbientOcclusionSampling)\"\\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TotalLightAssignmentRegex = new(
        "vec3\\s+totalLight\\s*=\\s*XRENGINE_CalculateForwardLighting\\s*\\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex NormalAssignmentRegex = new(
        "\\bvec3\\s+normal\\s*=\\s*(?<expression>.*?);",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static XRMaterial? CreateMaterialVariant(XRMaterial sourceMaterial)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        if (sourceMaterial.RenderPass != (int)EDefaultRenderPass.OpaqueForward &&
            sourceMaterial.RenderPass != (int)EDefaultRenderPass.MaskedForward)
            return null;

        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        string? fragmentSource = fragmentShader?.Source?.Text;
        if (fragmentShader is null || string.IsNullOrWhiteSpace(fragmentSource))
            return null;

        XRShader? explicitVariantShader = ShaderHelper.GetDepthNormalPrePassForwardVariant(fragmentShader);
        if (explicitVariantShader is not null)
            return CreateMaterialVariant(sourceMaterial, explicitVariantShader);

        if (!TryCreateFragmentVariantSource(fragmentSource, out string variantSource))
            return null;

        return CreateMaterialVariant(sourceMaterial, new XRShader(EShaderType.Fragment, variantSource));
    }

    private static XRMaterial CreateMaterialVariant(XRMaterial sourceMaterial, XRShader fragmentVariantShader)
    {
        List<XRShader> shaders =
        [
            .. sourceMaterial.Shaders.Where(shader => shader is not null && shader.Type != EShaderType.Fragment),
            fragmentVariantShader,
        ];

        XRMaterial variant = new(shaders)
        {
            Parameters = sourceMaterial.Parameters,
            Textures = sourceMaterial.Textures,
            RenderPass = sourceMaterial.RenderPass,
            BillboardMode = sourceMaterial.BillboardMode,
            AlphaCutoff = sourceMaterial.AlphaCutoff,
            TransparencyMode = sourceMaterial.TransparencyMode,
            TransparentTechniqueOverride = sourceMaterial.TransparentTechniqueOverride,
            TransparentSortPriority = sourceMaterial.TransparentSortPriority,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };

        variant.SettingUniforms += (_, program) => sourceMaterial.OnSettingUniforms(program);
        return variant;
    }

    public static bool TryCreateFragmentVariantSource(string fragmentSource, out string variantSource)
    {
        variantSource = string.Empty;

        if (string.IsNullOrWhiteSpace(fragmentSource))
            return false;

        if (fragmentSource.Contains("XRE_WriteWeightedBlendedOit", StringComparison.Ordinal) ||
            fragmentSource.Contains("XRE_StorePerPixelLinkedListFragment", StringComparison.Ordinal))
            return false;

        string rewrittenSource = OutputDeclarationRegex.Replace(fragmentSource, string.Empty);
        rewrittenSource = ForwardLightingSnippetRegex.Replace(rewrittenSource, string.Empty);
        rewrittenSource = InsertNormalOutputDeclaration(rewrittenSource);

        if (!TryRewriteMainBody(rewrittenSource, out variantSource))
            return false;

        return true;
    }

    private static RenderingParameters CreateRenderOptions(RenderingParameters? source)
        => new()
        {
            CullMode = source?.CullMode ?? ECullMode.Back,
            AlphaToCoverage = ERenderParamUsage.Disabled,
            BlendModeAllDrawBuffers = BlendMode.Disabled(),
            BlendModesPerDrawBuffer = null,
            DepthTest = new DepthTest()
            {
                Enabled = source?.DepthTest?.Enabled ?? ERenderParamUsage.Enabled,
                Function = source?.DepthTest?.Function ?? EComparison.Lequal,
                UpdateDepth = true,
            },
            RequiredEngineUniforms = EUniformRequirements.None,
        };

    private static string InsertNormalOutputDeclaration(string source)
    {
        const string normalOutput = "layout (location = 0) out vec3 Normal;";

        int versionLineEnd = source.IndexOf('\n');
        if (versionLineEnd < 0)
            return normalOutput + Environment.NewLine + Environment.NewLine + source.TrimStart();

        string header = source[..(versionLineEnd + 1)];
        string body = source[(versionLineEnd + 1)..].TrimStart('\r', '\n');
        return header + Environment.NewLine + normalOutput + Environment.NewLine + Environment.NewLine + body;
    }

    private static bool TryRewriteMainBody(string source, out string rewrittenSource)
    {
        rewrittenSource = string.Empty;

        int mainIndex = source.IndexOf("void main", StringComparison.Ordinal);
        if (mainIndex < 0)
            return false;

        int bodyStart = source.IndexOf('{', mainIndex);
        if (bodyStart < 0)
            return false;

        int bodyEnd = FindMatchingBrace(source, bodyStart);
        if (bodyEnd < 0)
            return false;

        string body = source[(bodyStart + 1)..bodyEnd];
        if (!TryBuildReplacementMainBody(body, out string replacementBody))
            return false;

        rewrittenSource = source[..(bodyStart + 1)] + Environment.NewLine + replacementBody + source[bodyEnd..];
        return true;
    }

    private static bool TryBuildReplacementMainBody(string body, out string replacementBody)
    {
        replacementBody = string.Empty;

        Match totalLightMatch = TotalLightAssignmentRegex.Match(body);
        if (!totalLightMatch.Success)
            return false;

        int callNameIndex = body.IndexOf(ForwardLightingFunction, totalLightMatch.Index, StringComparison.Ordinal);
        if (callNameIndex < 0)
            return false;

        int callOpenParenIndex = body.IndexOf('(', callNameIndex);
        if (callOpenParenIndex < 0)
            return false;

        int callCloseParenIndex = FindMatchingParenthesis(body, callOpenParenIndex);
        if (callCloseParenIndex < 0)
            return false;

        string callArguments = body[(callOpenParenIndex + 1)..callCloseParenIndex];
        string normalExpression = ResolveNormalExpression(body[..totalLightMatch.Index], callArguments);

        StringBuilder builder = new();
        string prefix = body[..totalLightMatch.Index].TrimEnd();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            foreach (string line in prefix.Split(["\r\n", "\n"], StringSplitOptions.None))
                builder.Append("    ").AppendLine(line);

            builder.AppendLine();
        }

        builder.Append("    Normal = normalize(")
            .Append(normalExpression.Trim())
            .AppendLine(");");

        replacementBody = builder.ToString();
        return true;
    }

    private static string ResolveNormalExpression(string bodyPrefix, string callArguments)
    {
        MatchCollection normalMatches = NormalAssignmentRegex.Matches(bodyPrefix);
        if (normalMatches.Count > 0)
            return "normal";

        List<string> arguments = SplitTopLevelArguments(callArguments);
        if (arguments.Count > 0 && !string.IsNullOrWhiteSpace(arguments[0]))
            return arguments[0];

        return "FragNorm";
    }

    private static List<string> SplitTopLevelArguments(string argumentList)
    {
        List<string> arguments = [];
        StringBuilder current = new();
        int parenthesisDepth = 0;

        foreach (char character in argumentList)
        {
            switch (character)
            {
                case '(':
                    parenthesisDepth++;
                    current.Append(character);
                    break;
                case ')':
                    parenthesisDepth--;
                    current.Append(character);
                    break;
                case ',' when parenthesisDepth == 0:
                    arguments.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0)
            arguments.Add(current.ToString().Trim());

        return arguments;
    }

    private static int FindMatchingBrace(string source, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < source.Length; i++)
        {
            char character = source[i];
            if (character == '{')
                depth++;
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int FindMatchingParenthesis(string source, int openParenthesisIndex)
    {
        int depth = 0;
        for (int i = openParenthesisIndex; i < source.Length; i++)
        {
            char character = source[i];
            if (character == '(')
                depth++;
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }
}
