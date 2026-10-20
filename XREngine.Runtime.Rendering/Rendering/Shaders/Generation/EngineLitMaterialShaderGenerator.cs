using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>
/// Selects the engine's physical PBR shader frontend for a target-specific authored
/// surface cook. The returned source is compiled and ABI-checked by ShaderCooker;
/// desktop GLSL source and the authored material are never rewritten.
/// </summary>
public static partial class EngineLitMaterialShaderGenerator
{
    public const string ColorSchema = "xrengine.engine.authored-lit-color.v1";
    public const string TextureSchema = "xrengine.engine.authored-lit-texture.v1";
    public const string NormalTextureSchema = "xrengine.engine.authored-lit-texture-normal.v1";
    public const string ColorCoverageSchema = "xrengine.engine.authored-lit-color-coverage.v2";

    // Engine frontend hashes are part of this versioned material ABI. A project
    // staging root may contain copies, but it cannot substitute a same-named
    // shader or include and have that code admitted as the engine PBR frontend.
    private static readonly EngineLitMaterialShaderSource Color = new("StandardLitColor.slang",
        "50074cecbde9666073ff50275511151c5b01096e155f92115b5ef4c591a59e9c");
    private static readonly EngineLitMaterialShaderSource Sampling = new("StandardLitTextureSampling.slang",
        "54b4fb19055f6413f755295090ce615dab078cbe9891a91bd001ff88938f0d35");
    private static readonly EngineLitMaterialShaderSource Texture = new("StandardLitTexture.slang",
        "af1afd388c334aa5e5dabf42db0860974d9791858c01b32c80ade6615df8af14");
    private static readonly EngineLitMaterialShaderSource NormalTexture = new("StandardLitTextureNormal.slang",
        "5b08006510c9db101f5f7a2522c1cd7390bb6e6bfe58a2f696a0a0419d93cb6d");
    private static readonly EngineLitMaterialShaderSource ColorCoverage = new("StandardLitColorCoverageLocalShadows.slang",
        "362f3a0573e8f05beb241e8855de37b31500455100c58e456babee9e358024ed");
    private static readonly EngineLitMaterialShaderSource LocalShadows = new("StandardLitColorLocalShadows.slang",
        "07a11ad4fd6d3f762239038d97a50ca610688a61c7c88621c0a2e8bbc24c1052");
    private static readonly EngineLitMaterialShaderSource DirectionalShadow = new("StandardLitColorDirectionalShadow.slang",
        "eb50a3041d0ebfc17f8ed75d3e15b3196926df32d5cf5d4bf0e81d040c9b07fe");
    private static readonly EngineLitMaterialShaderSource LocalShadowSampling = new("LocalShadowSampling.slang",
        "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc");

    public static IReadOnlyList<EngineLitMaterialShaderSource> RequiredCanonicalSources(EngineLitMaterialShaderPlan plan)
        => plan.SemanticSchemaIdentity switch
        {
            EngineAuthoredTexturedShaderGenerator.Schema => EngineAuthoredTexturedShaderGenerator.RequiredCanonicalSources,
            EngineTexturedAlphaShaderGenerator.Schema => EngineTexturedAlphaShaderGenerator.RequiredCanonicalSources,
            ColorSchema => [Color],
            ColorCoverageSchema => [ColorCoverage, LocalShadows, DirectionalShadow, LocalShadowSampling],
            TextureSchema => [Color, Sampling, Texture],
            NormalTextureSchema => [Color, Sampling, NormalTexture],
            _ => throw new NotSupportedException($"Unknown authored PBR frontend '{plan.SemanticSchemaIdentity}'."),
        };

    public static EngineLitMaterialShaderPlan Plan(string name, string shadingModel, string surface,
        string baseColor, string normal, ShaderCompileTarget target)
    {
        string context = $"Authored material '{name}' pass '{surface}' target '{target}'";
        if (string.IsNullOrWhiteSpace(name)) throw new NotSupportedException($"{context}: a material name is required.");
        if (target != ShaderCompileTarget.WebGPUWgsl)
            throw new NotSupportedException($"{context}: WGSL generation requires the WebGPUWgsl target.");
        if (shadingModel != "lit")
            throw new NotSupportedException($"{context}: shading model '{shadingModel}' is unsupported; expected the engine PBR lit model.");
        bool coverage = surface is "opaque-coverage" or "masked" or "alpha-blend" or "premultiplied-alpha" or "additive";
        if (surface != "opaque" && !coverage)
            throw new NotSupportedException($"{context}: surface '{surface}' has no modeled engine coverage contract.");
        if (normal is not ("vertex" or "texture"))
            throw new NotSupportedException($"{context}: normal source '{normal}' is unsupported.");
        if (baseColor == "texture-alpha")
        {
            if (normal != "vertex")
                throw new NotSupportedException($"{context}: exact textured alpha uses vertex normals only.");
            return EngineTexturedAlphaShaderGenerator.Plan(name, surface, target);
        }
        if (coverage)
        {
            if (baseColor != "tint" || normal != "vertex")
                throw new NotSupportedException($"{context}: authored coverage requires uniform-alpha color; textured coverage has no exact desktop/cooked surface contract.");
            return new(name, "StandardLitColorCoverageLocalShadows.slang", ColorCoverageSchema, false, false, surface);
        }
        return baseColor switch
        {
            "tint" when normal == "vertex" => new(name, "StandardLitColor.slang", ColorSchema, false, false),
            "texture" when normal == "vertex" => new(name, "StandardLitTexture.slang", TextureSchema, true, false),
            "texture" when normal == "texture" => new(name, "StandardLitTextureNormal.slang", NormalTextureSchema, true, true),
            "tint" => throw new NotSupportedException($"{context}: a normal texture requires a base-color texture in this bounded profile."),
            _ => throw new NotSupportedException($"{context}: base color source '{baseColor}' is unsupported."),
        };
    }
}

/// <summary>Canonical engine source and surface ABI selected by authored lit features.</summary>
public readonly record struct EngineLitMaterialShaderPlan(string Name, string SlangSource, string SemanticSchemaIdentity,
    bool UsesBaseColorTexture, bool UsesNormalTexture, string Surface = "opaque", int AuthoredTextureFlags = 0)
{
    public bool UsesCoverage => SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.ColorCoverageSchema;
    public string Pass => SemanticSchemaIdentity == EngineAuthoredTexturedShaderGenerator.Schema
        ? EngineAuthoredTexturedShaderGenerator.Pass : SemanticSchemaIdentity == EngineTexturedAlphaShaderGenerator.Schema
        ? EngineTexturedAlphaShaderGenerator.Pass : UsesCoverage ? "forward-coverage" : "opaque-forward";
}

/// <summary>Trusted input of the versioned engine PBR lowering.</summary>
public readonly record struct EngineLitMaterialShaderSource(string Path, string Sha256);
