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

    // Engine frontend hashes are part of this versioned material ABI. A project
    // staging root may contain copies, but it cannot substitute a same-named
    // shader or include and have that code admitted as the engine PBR frontend.
    private static readonly EngineLitMaterialShaderSource Color = new("StandardLitColor.slang",
        "50074cecbde9666073ff50275511151c5b01096e155f92115b5ef4c591a59e9c");
    private static readonly EngineLitMaterialShaderSource Sampling = new("StandardLitTextureSampling.slang",
        "dfae127a0b03b8022d5fa269c62728e9de12bdf60a50eacce3584c9fec3fb24c");
    private static readonly EngineLitMaterialShaderSource Texture = new("StandardLitTexture.slang",
        "af1afd388c334aa5e5dabf42db0860974d9791858c01b32c80ade6615df8af14");
    private static readonly EngineLitMaterialShaderSource NormalTexture = new("StandardLitTextureNormal.slang",
        "5b08006510c9db101f5f7a2522c1cd7390bb6e6bfe58a2f696a0a0419d93cb6d");

    public static IReadOnlyList<EngineLitMaterialShaderSource> RequiredCanonicalSources(EngineLitMaterialShaderPlan plan)
        => plan.SemanticSchemaIdentity switch
        {
            ColorSchema => [Color],
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
        if (surface != "opaque")
            throw new NotSupportedException($"{context}: surface '{surface}' is unsupported; authored coverage and blending require a separate pass contract.");
        if (normal is not ("vertex" or "texture"))
            throw new NotSupportedException($"{context}: normal source '{normal}' is unsupported.");
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
    bool UsesBaseColorTexture, bool UsesNormalTexture);

/// <summary>Trusted input of the versioned engine PBR lowering.</summary>
public readonly record struct EngineLitMaterialShaderSource(string Path, string Sha256);
