using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Shaders;

public static class ForwardDepthNormalVariantFactory
{
    private const string ForwardLightingFunction = "XRENGINE_CalculateForwardLighting";
    private static readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, CachedFragmentVariantResult>> FragmentVariantSourceCache = new();

    private const string UberDissolveGuard = "XRENGINE_UBER_DISABLE_DISSOLVE";

    /// <summary>Uber features that must be compiled out for full coverage.</summary>
    private static readonly string[] UberCoverageGuards =
    [
        UberDissolveGuard,
        "XRENGINE_UBER_DISABLE_PARALLAX",
    ];

    /// <summary>Uber parameters that must be zero for full coverage.</summary>
    private static readonly string[] UberCoverageParameters =
    [
        "_Mode",
        "_ViewVisibilityMask",
        "_FaceDiscard",
        "_UvTileDiscard",
    ];

    private static readonly Lazy<XRShader> SharedSurfaceShader = new(
        static () => XRShader.EngineShader(Path.Combine("Common", "DepthNormalPrePass.fs"), EShaderType.Fragment));

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

    private static readonly Regex AmbientOcclusionSamplingStatementRegex = new(
        @"^\s*[^\n;]*XRENGINE_SampleAmbientOcclusion\s*\([^\n;]*\)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PreprocessorDirectiveRegex = new(
        @"^\s*#(?:if|ifdef|ifndef|elif|else|endif)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private sealed class CachedFragmentVariantResult(bool success, string variantSource)
    {
        public bool Success { get; } = success;
        public string VariantSource { get; } = variantSource;
    }

    internal static void ClearCaches()
        => FragmentVariantSourceCache.Clear();

    public static XRMaterial? CreateMaterialVariant(XRMaterial sourceMaterial)
    {
        ArgumentNullException.ThrowIfNull(sourceMaterial);

        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            sourceMaterial.EngineSemantic == EngineMaterialSemanticIdentity.UberBaseV1)
            return UberBaseVariantFactory.Create(sourceMaterial, EStandardLitColorAuxiliaryPass.DepthNormal);

        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            sourceMaterial.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitTexturedV1)
            return AuthoredTexturedVariantFactory.Create(sourceMaterial, EStandardLitColorAuxiliaryPass.DepthNormal);

        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            sourceMaterial.EngineSemantic == EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1)
            return TexturedAlphaVariantFactory.Create(sourceMaterial, EStandardLitColorAuxiliaryPass.DepthNormal);

        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            sourceMaterial.EngineSemantic.IsUnlit())
            return UnlitVariantFactory.Create(sourceMaterial, EStandardLitColorAuxiliaryPass.DepthNormal);

        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked &&
            sourceMaterial.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
            return StandardLitTextureVariantFactory.CreateDepthNormal(sourceMaterial);

        if (sourceMaterial.EngineSemantic.IsColorCoverage())
            return StandardLitColorVariantFactory.Create(sourceMaterial, EStandardLitColorAuxiliaryPass.DepthNormal);

        if (sourceMaterial.RenderPass != (int)EDefaultRenderPass.OpaqueForward &&
            sourceMaterial.RenderPass != (int)EDefaultRenderPass.MaskedForward)
            return null;

        XRShader? fragmentShader = sourceMaterial.FragmentShaders.FirstOrDefault();
        string? fragmentSource = fragmentShader?.Source?.Text;
        if (fragmentShader is null || string.IsNullOrWhiteSpace(fragmentSource))
            return null;

        XRShader? explicitVariantShader = ShaderHelper.GetDepthNormalPrePassForwardVariant(fragmentShader);
        string variantSource = string.Empty;
        if (explicitVariantShader is null && !TryCreateFragmentVariantSource(fragmentSource, out variantSource))
            return null;

        if (CanUseSharedSurfaceShader(sourceMaterial, fragmentSource))
            return CreateSharedSurfaceVariant(sourceMaterial);

        return CreateMaterialVariant(sourceMaterial, explicitVariantShader ?? new XRShader(EShaderType.Fragment, variantSource));
    }

    /// <summary>
    /// Returns true when the material's lit pass covers every rasterized fragment.
    /// The shared shader has no discard path, so it must not write depth for a
    /// material that can discard a fragment in its lit pass.
    /// </summary>
    private static bool CanUseSharedSurfaceShader(XRMaterial sourceMaterial, string fragmentSource)
    {
        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked ||
            sourceMaterial.RenderPass != (int)EDefaultRenderPass.OpaqueForward ||
            sourceMaterial.VertexShaders.Count != 0 ||
            !sourceMaterial.CanUseSharedOpaqueShadowMaterial() ||
            fragmentSource.Contains("gl_FragDepth", StringComparison.Ordinal))
            return false;

        if (!fragmentSource.Contains(UberDissolveGuard, StringComparison.Ordinal))
            return !fragmentSource.Contains("discard", StringComparison.Ordinal);

        foreach (string guard in UberCoverageGuards)
            if (!fragmentSource.Contains("#define " + guard, StringComparison.Ordinal))
                return false;

        // These features stay compiled in, so their saved values decide coverage.
        // A value change invalidates this variant through the binding version.
        ShaderVar[] parameters = sourceMaterial.Parameters ?? [];
        foreach (ShaderVar parameter in parameters)
            if (Array.IndexOf(UberCoverageParameters, parameter.Name) >= 0 &&
                Convert.ToDouble(parameter.GenericValue) != 0.0)
                return false;
        return true;
    }

    private static XRMaterial CreateSharedSurfaceVariant(XRMaterial sourceMaterial)
        => new(SharedSurfaceShader.Value)
        {
            RenderPass = sourceMaterial.RenderPass,
            BillboardMode = sourceMaterial.BillboardMode,
            TransparencyMode = sourceMaterial.TransparencyMode,
            RenderOptions = CreateRenderOptions(sourceMaterial.RenderOptions),
        };

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
        if (string.IsNullOrWhiteSpace(fragmentSource))
        {
            variantSource = string.Empty;
            return false;
        }

        ulong sourceHash = ComputeSourceHash(fragmentSource);
        ConcurrentDictionary<string, CachedFragmentVariantResult> bucket = FragmentVariantSourceCache.GetOrAdd(
            sourceHash,
            static _ => new(StringComparer.Ordinal));

        CachedFragmentVariantResult cachedResult = bucket.GetOrAdd(
            fragmentSource,
            static source => CreateCachedFragmentVariantResult(source));

        variantSource = cachedResult.VariantSource;
        return cachedResult.Success;
    }

    private static CachedFragmentVariantResult CreateCachedFragmentVariantResult(string fragmentSource)
    {
        bool success = TryCreateFragmentVariantSourceCore(fragmentSource, out string variantSource);
        return new(success, success ? variantSource : string.Empty);
    }

    private static bool TryCreateFragmentVariantSourceCore(string fragmentSource, out string variantSource)
    {
        variantSource = string.Empty;

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

    private static ulong ComputeSourceHash(string source)
    {
        const ulong fnvOffset = 14695981039346656037ul;
        const ulong fnvPrime = 1099511628211ul;

        ulong hash = fnvOffset;
        for (int i = 0; i < source.Length; i++)
        {
            hash ^= source[i];
            hash *= fnvPrime;
        }

        hash ^= (ulong)source.Length;
        hash *= fnvPrime;
        return hash;
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
        const string normalOutput = "layout (location = 0) out vec2 Normal;";
        const string normalEncodingSnippet = "#pragma snippet \"NormalEncoding\"";

        int versionLineEnd = source.IndexOf('\n');
        if (versionLineEnd < 0)
            return normalOutput + Environment.NewLine + normalEncodingSnippet + Environment.NewLine + Environment.NewLine + source.TrimStart();

        string header = source[..(versionLineEnd + 1)];
        string body = source[(versionLineEnd + 1)..].TrimStart('\r', '\n');
        return header + Environment.NewLine + normalOutput + Environment.NewLine + normalEncodingSnippet + Environment.NewLine + Environment.NewLine + body;
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

        if (PreprocessorDirectiveRegex.IsMatch(body[..totalLightMatch.Index]))
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
        string prefix = RemoveAmbientOcclusionSamplingStatements(body[..totalLightMatch.Index]).TrimEnd();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            foreach (string line in prefix.Split(["\r\n", "\n"], StringSplitOptions.None))
                builder.Append("    ").AppendLine(line);

            builder.AppendLine();
        }

        builder.Append("    Normal = XRENGINE_EncodeNormal(")
            .Append(normalExpression.Trim())
            .AppendLine(");");

        replacementBody = builder.ToString();
        return true;
    }

    private static string RemoveAmbientOcclusionSamplingStatements(string source)
        => AmbientOcclusionSamplingStatementRegex.Replace(source, string.Empty);

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
