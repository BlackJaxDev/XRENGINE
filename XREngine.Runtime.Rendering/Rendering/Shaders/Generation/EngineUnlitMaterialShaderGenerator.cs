using System.Collections.Immutable;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Exact, versioned target lowering of the five ordinary engine unlit surfaces.</summary>
public static class EngineUnlitMaterialShaderGenerator
{
    public const string Pass = "forward-unlit";
    public const string OrderGateSchema = "xrengine.engine.unlit-order-gate.v1";
    public const string ColorSchema = "xrengine.engine.unlit-color.v1";
    public const string TextureSchema = "xrengine.engine.unlit-texture.v2";
    public const string OpaqueTextureSchema = "xrengine.engine.unlit-opaque-texture.v3";
    public const string AlphaTextureSchema = "xrengine.engine.unlit-alpha-texture.v4";
    public const string TextureArraySliceSchema = "xrengine.engine.unlit-texture-array-slice.v5";
    private const string SurfaceHash = "6ed244d8f8fbe8cf681c878dcce8f3a38565a96ca13e4e7e627aecbd682707d0";
    private const string GateHash = "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c";
    private static readonly EngineLitMaterialShaderSource SurfaceSource = new("UnlitSurface.slang", SurfaceHash);
    private static readonly EngineLitMaterialShaderSource GateSource = new("AuthoredOrderGate.slang", GateHash);

    private static readonly ImmutableArray<EngineLitMaterialShaderSource> ForwardSources =
    [
        new("UnlitColor.slang", "df9568f187b0a99e911263ed706c7f9f31fb0bf409094852b3ce936c2f6f02b7"),
        new("UnlitTexture.slang", "c6a57bd0d4b76bb43860b976e8f9d59729a82ac1b8027f284e05fd5b38b2a5b9"),
        new("UnlitOpaqueTexture.slang", "87a1a62535ab1354ffced9b26817d75f46a33d16d45d5352d034a1f04fd2bed0"),
        new("UnlitAlphaTexture.slang", "29ac0eb4216a7f6d8fd7b835d1133b833274092d8d17ed0e1a342fcdd4955e3a"),
        new("UnlitTextureArraySlice.slang", "355bec040cf405989a269447848b4bc78acfb1e74851d5b3eea46974ac8b7280"),
    ];
    private static readonly ImmutableArray<EngineLitMaterialShaderSource> NormalSources =
    [
        new("UnlitColorDepthNormal.slang", "6c51416ba188e57b8a2d121d78fa3050d8eccb1586fed281e739b60b548a243b"),
        new("UnlitTextureDepthNormal.slang", "2b6024f2b519f861ca19e6c8bbdc5030daf4e964977b4d05a33774e16702f428"),
        new("UnlitOpaqueTextureDepthNormal.slang", "67079c5b8f1b241048f83e2409f2be425780ecc3714e072a8585114bce3c31da"),
        new("UnlitAlphaTextureDepthNormal.slang", "94330f6ac993d0ce21bf5d886579faa39f0f3a08471a58cca069e3593231e720"),
        new("UnlitTextureArraySliceDepthNormal.slang", "37549747466fb81d2e9e6b95a0029e8e559776fe47a371d421bf2e4acc577d55"),
    ];
    private static readonly ImmutableArray<EngineLitMaterialShaderSource> OrderSources =
    [
        new("UnlitColorAuthoredOrder.slang", "d034405d77edc11ad4e1f52dcb36d13930b14e71cb231299cbad8c818ffa3327"),
        new("UnlitTextureAuthoredOrder.slang", "9e4c09ff8a0d60e40aa8d24cb886ac86896ed92d47c1af25cae21ee17aa46e76"),
        default,
        new("UnlitAlphaTextureAuthoredOrder.slang", "6c10dd165fe31df0397537d5c7193ca295eed9b07bba4a3f08840b399d58a899"),
        new("UnlitTextureArraySliceAuthoredOrder.slang", "71aef9fcf17620a3d791c301b27faa5153f699e66e7482f8514e8039cb1a6aa8"),
    ];
    private static readonly ImmutableArray<EngineLitMaterialShaderSource> AlphaShadowSources =
    [
        new("UnlitAlphaTextureDepth.slang", "58e7241412f8dea0a9822fdde2005845cfd7bbcc016ec464920f96035b159bc4"),
        new("UnlitAlphaTexturePointDepth.slang", "b5e7919e0cb7fd61532c92a63391071343169c20c49d17fbc96e177140fcbeb8"),
        new("UnlitAlphaTextureSpotDepth.slang", "a213bbf883978983b32ff7cae3c0d9906acfd5bc32987b2bba6aed29d0f30a32"),
    ];
    private static readonly ImmutableArray<EngineLitMaterialShaderSource> DesktopSources =
    [
        new("Common/UnlitColoredForward.fs", "88f89e4058a1231b6271409848cb06b262cf408dae55364bd5a384ab91d0b840"),
        new("Common/UnlitTexturedForward.fs", "ae78f19c6bf5c1c7061e043f0d5ac97bed0f72062ffb925c943432139713ff3a"),
        new("Common/UnlitTexturedOpaqueForward.fs", "21f2c269072769ce711316071134e6de5e886746dd23a41b6d0da222fdf55964"),
        new("Common/UnlitAlphaTexturedForward.fs", "203b656fdd47bb24e67c36a01c6c6c70e4da2d9597be32ce889c5429b98a0f1c"),
        new("Common/UnlitTexturedArraySliceForward.fs", "19b9a48c3ea2df7443b30192e7b4c28c987b636f6f1966376360fced003c24de"),
    ];
    private static readonly EngineLitMaterialShaderSource NormalSnippet = new("Snippets/NormalEncoding.glsl", "926f6f879f1cebbbdbe7eb911d0ccfe3e57a2353345cffb69508ddf7ab90e7e9");
    private static readonly EngineLitMaterialShaderSource MomentSnippet = new("Snippets/ShadowMomentEncoding.glsl", "b0ad566842e7e9ea654685bcadba629cf2000c88088251cffc5792a9ddf1b17b");
    private static readonly EngineLitMaterialShaderSource PpllSnippet = new("Snippets/ExactTransparencyPpll.glsl", "0a11e1086909cd3476a5f400c64b79d586ebc49b9bd324280bd0376192757f58");
    private static readonly EngineLitMaterialShaderSource PeelSnippet = new("Snippets/ExactTransparencyDepthPeel.glsl", "a2bad4809a51dc086c33444e22e9992f6bc3da2fa8d38ebb21205fbb37babdaa");

    public static string CookName(Guid materialId) => materialId == Guid.Empty
        ? throw new ArgumentException("A persistent material ID is required.", nameof(materialId))
        : "mat-" + materialId.ToString("N");

    /// <summary>Selects the shared, source-free engine factory program for one canonical unlit family.</summary>
    public static EngineMaterialVariantKey BuiltInKey(EngineMaterialSemanticIdentity semantic)
    {
        _ = SchemaFor(semantic);
        string vertex = semantic == EngineMaterialSemanticIdentity.UnlitColorV1
            ? "static-position-normal-v1" : "position-normal-uv-v1";
        return new(semantic, ShaderCompileTarget.WebGPUWgsl, Pass, vertex, "linear-hdr-rgba-v1");
    }

    public static string BuiltInName(EngineMaterialSemanticIdentity semantic) => semantic switch
    {
        var value when value == EngineMaterialSemanticIdentity.UnlitColorV1 => "engine-unlit-color",
        var value when value == EngineMaterialSemanticIdentity.UnlitTextureV2 => "engine-unlit-texture",
        var value when value == EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3 => "engine-unlit-opaque-texture",
        var value when value == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 => "engine-unlit-alpha-texture",
        var value when value == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5 => "engine-unlit-texture-array-slice",
        _ => throw new NotSupportedException($"Unsupported ordinary unlit semantic '{semantic}'."),
    };

    public static string SchemaFor(EngineMaterialSemanticIdentity semantic) => semantic switch
    {
        var value when value == EngineMaterialSemanticIdentity.UnlitColorV1 => ColorSchema,
        var value when value == EngineMaterialSemanticIdentity.UnlitTextureV2 => TextureSchema,
        var value when value == EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3 => OpaqueTextureSchema,
        var value when value == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 => AlphaTextureSchema,
        var value when value == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5 => TextureArraySliceSchema,
        _ => throw new NotSupportedException($"Unsupported ordinary unlit semantic '{semantic}'."),
    };

    public static EngineUnlitMaterialShaderPlan Plan(string name, EngineMaterialSemanticIdentity semantic,
        string surface, ShaderCompileTarget target)
    {
        if (string.IsNullOrWhiteSpace(name) || target != ShaderCompileTarget.WebGPUWgsl || !semantic.IsUnlit())
            throw new NotSupportedException("Ordinary unlit requires a named, versioned WebGPU material.");
        bool sorted = surface is "alpha-blend" or "premultiplied-alpha" or "additive";
        bool supported = surface == "opaque" || sorted || semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4 && surface == "masked";
        if (!supported || semantic == EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3 && surface != "opaque")
            throw new NotSupportedException($"Unlit {semantic} does not model surface '{surface}'.");
        EngineLitMaterialShaderSource source = ForwardSources[semantic.Version - 1];
        return new(name, semantic, SchemaFor(semantic), source.Path, surface, semantic.Version != 1,
            semantic == EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5,
            semantic == EngineMaterialSemanticIdentity.UnlitAlphaTextureV4, sorted);
    }

    public static string MaterialRecipeJson(EngineUnlitMaterialShaderPlan plan)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 3, name = plan.Name, shadingModel = "unlit", surface = plan.Surface,
            baseColor = BaseColorFor(plan.Semantic), semanticVersion = plan.Semantic.Version,
        }, new JsonSerializerOptions { WriteIndented = true });

    public static string BaseColorFor(EngineMaterialSemanticIdentity semantic) => semantic.Version switch
    {
        1 when semantic.IsUnlit() => "tint",
        2 when semantic.IsUnlit() => "texture",
        3 when semantic.IsUnlit() => "opaque-texture",
        4 when semantic.IsUnlit() => "alpha-texture",
        5 when semantic.IsUnlit() => "texture-array-slice",
        _ => throw new NotSupportedException($"Unsupported ordinary unlit semantic '{semantic}'."),
    };

    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredCanonicalSources(EngineMaterialSemanticIdentity semantic)
        => [ForwardSources[RequireVersion(semantic) - 1], SurfaceSource];

    /// <summary>Canonical authored GLSL and the whole snippet graph referenced by that source.</summary>
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredDesktopSources(EngineMaterialSemanticIdentity semantic)
    {
        int version = RequireVersion(semantic);
        if (version is 2 or 3 or 4)
            return version == 4 ? [DesktopSources[version - 1], NormalSnippet, MomentSnippet, PpllSnippet, PeelSnippet]
                : [DesktopSources[version - 1], NormalSnippet, PpllSnippet, PeelSnippet];
        return [DesktopSources[version - 1], NormalSnippet];
    }

    public static ImmutableArray<EngineLitMaterialShaderSource> CompanionSources(EngineMaterialSemanticIdentity semantic,
        string pass, bool orderGate = false)
    {
        int version = RequireVersion(semantic);
        if (orderGate)
        {
            if (pass != Pass || version == 3) throw new NotSupportedException("This unlit variant has no sorted order gate.");
            return [OrderSources[version - 1], SurfaceSource, GateSource];
        }
        if (pass == "depth-normal") return [NormalSources[version - 1], SurfaceSource];
        if (version == 4)
        {
            EngineLitMaterialShaderSource entry = pass switch
            {
                "depth" => AlphaShadowSources[0],
                "point-shadow-depth" => AlphaShadowSources[1],
                "spot-shadow-depth" => AlphaShadowSources[2],
                _ => throw new NotSupportedException($"Unlit alpha has no companion pass '{pass}'."),
            };
            return [entry, SurfaceSource];
        }
        throw new NotSupportedException($"Unlit {semantic} has no companion pass '{pass}'.");
    }

    public static EngineMaterialVariantKey CompanionKey(EngineMaterialSemanticIdentity semantic, string pass,
        bool orderGate = false)
    {
        _ = CompanionSources(semantic, pass, orderGate);
        string vertex = semantic.Version == 1 ? "static-position-normal" : "position-normal-uv";
        if (orderGate) vertex += "-order-gate";
        string output = pass switch
        {
            Pass => "linear-hdr-rgba-v1",
            "depth-normal" => "normal-rgba16f-v1",
            "depth" => "depth-normal-v1",
            "point-shadow-depth" => "radial-r16f-v1",
            "spot-shadow-depth" => "projected-r16f-v1",
            _ => throw new NotSupportedException($"Unlit has no pass '{pass}'."),
        };
        return new(semantic, ShaderCompileTarget.WebGPUWgsl, pass, vertex + "-v1", output);
    }

    private static int RequireVersion(EngineMaterialSemanticIdentity semantic)
        => semantic.IsUnlit() ? semantic.Version : throw new NotSupportedException($"Unsupported ordinary unlit semantic '{semantic}'.");
}
