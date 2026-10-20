using System.Collections.Immutable;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Versioned exact lowering of the engine's 26-layer camera-facing impostor.</summary>
public static class EngineOctahedralImpostorShaderContract
{
    public const string Schema = "xrengine.engine.octahedral-impostor.v1";
    public const string OrderGateSchema = "xrengine.engine.octahedral-impostor-order-gate.v1";
    public const string Pass = "forward-impostor";
    public const string VertexProfile = "position-uv4-billboard-v1";
    public const string OrderGateVertexProfile = "position-uv4-billboard-order-gate-v1";
    public const string OutputProfile = "linear-hdr-rgba-v1";
    public const int ViewCount = 26;

    public static EngineMaterialVariantKey Key(bool orderGate = false)
        => new(EngineMaterialSemanticIdentity.OctahedralImpostorV1, ShaderCompileTarget.WebGPUWgsl,
            Pass, orderGate ? OrderGateVertexProfile : VertexProfile, OutputProfile);

    /// <summary>Canonical authored stages and their full include closure, with normalized line endings.</summary>
    public static ImmutableArray<EngineLitMaterialShaderSource> DesktopSources { get; } =
    [
        new("Scene3D/OctahedralImposterBillboard.vs", "c571964b8c46bd68ebce3ceab9230c988f7e5619732bd9c61f50d83c7fb10526"),
        new("Scene3D/OctahedralImposterBillboard.fs", "0a4d0fad1feec0a513eaec6c55b66f5155c74d98c89686ce36530e5dab491fe5"),
        new("Common/OctahedralImposter.glsl", "5f9cc2bb08f08ff6e56ec9b7d069c5550390edc3d81f2f90284a1d39542e1a65"),
    ];

    public static ImmutableArray<EngineLitMaterialShaderSource> CompanionSources(bool orderGate = false)
        => orderGate ? [
        new("OctahedralImpostorAuthoredOrder.slang", "eeea55600a5cd2990d3f8094055020f0078ad331dad1a0c0c3370194ba881e4d"),
        new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"),
        new("OctahedralImpostor.slang", "8f4b1903cefb21bbae2a75ffb8036e7ec5caa037032583c0bdd94567a8cf28ac"),
        ] : [
        new("OctahedralImpostor.slang", "8f4b1903cefb21bbae2a75ffb8036e7ec5caa037032583c0bdd94567a8cf28ac"),
        ];

    public static string LayoutHash(bool orderGate)
        => orderGate ? "bf89e4fa07cc2a2fbdd56b9fd6a3899e0968f69e65bec5597ed915257b7c8363" : "d1963e03e21d482ea59800b79d1ff92302c58c651f2124049bce38c448fe18eb";

    public static bool TryValidateDesktopSources(Func<string, string?> readSource, out string reason)
    {
        ArgumentNullException.ThrowIfNull(readSource);
        foreach (EngineLitMaterialShaderSource source in DesktopSources)
        {
            string? text = readSource(source.Path);
            if (text is null || EngineTexturedAlphaShaderGenerator.NormalizedHash(text) != source.Sha256)
            {
                reason = $"Octahedral impostor source '{source.Path}' is absent or modified; its exact browser companion must be recooked.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}
