using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Versioned exact lowering of the engine's authored two-texture alpha frontend.</summary>
public static class EngineTexturedAlphaShaderGenerator
{
    public const string Schema = "xrengine.engine.authored-lit-texture-alpha.v1";
    public const string Pass = "forward-textured-alpha";
    public const string VertexProfile = "position-normal-uv-v1";
    public const string OrderGateSchema = "xrengine.engine.authored-textured-alpha-order-gate.v1";
    public const string OrderGateVertexProfile = "position-normal-uv-order-gate-v1";
    public const string DesktopFragmentPath = "Common/LitTexturedAlphaForward.fs";
    public const string DesktopStagingDirectory = "Desktop";

    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredCanonicalSources { get; } =
    [
        new("TexturedAlphaLocalShadows.slang", "76d3735b7e9682bfb5c7ec8ca6fedefe2da64434913e29b58e09f2148c00719f"),
        new("TexturedAlphaDirectionalShadow.slang", "a7be46288bf9dce8287659f55d6c2b07facee4d929401b65b04114409f41cab3"),
        new("TexturedAlphaSampling.slang", "d0c3a08daa81eb71e39ca3b56533fd3501bc03fa1ee89f7de4ac08a79f092663"),
        new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc"),
    ];

    /// <summary>The entire normalized authored snippet graph, including inactive pass branches.</summary>
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredDesktopSources { get; } =
    [
        new("Common/LitTexturedAlphaForward.fs", "91dea6a2d0b6080e9a681d31f64321dab9a34024143703e01eef8ca2f8bb3fe4"),
        new("Snippets/ExactTransparencyPpll.glsl", "0a11e1086909cd3476a5f400c64b79d586ebc49b9bd324280bd0376192757f58"),
        new("Snippets/ExactTransparencyDepthPeel.glsl", "a2bad4809a51dc086c33444e22e9992f6bc3da2fa8d38ebb21205fbb37babdaa"),
        new("Snippets/ForwardLighting.glsl", "639636a250a335b0c960618783135d9c3404366c0f005426128dfabe28f741f4"),
        new("Snippets/LightStructs.glsl", "fb66908be5d31f18fa636b6a4dd53e4025cd149113c89f58edc3b85f83d9b954"),
        new("Snippets/LightAttenuation.glsl", "dad7a438f800f40f7cadf28099c0d9559c0632314364eeed4e270d0d683287ba"),
        new("Snippets/ShadowSampling.glsl", "f404d3585da7d87332a019d9190c56b7c8aeb82d61679fa40d2aa742d876e726"),
        new("Snippets/ShadowMomentEncoding.glsl", "b0ad566842e7e9ea654685bcadba629cf2000c88088251cffc5792a9ddf1b17b"),
        new("Snippets/AmbientOcclusionSampling.glsl", "6dcc0b3ba5a7f19d97425a1888b1afce3da420473d0bafbcd9aff85f31c079ab"),
        new("Snippets/NormalEncoding.glsl", "926f6f879f1cebbbdbe7eb911d0ccfe3e57a2353345cffb69508ddf7ab90e7e9"),
    ];

    public static EngineLitMaterialShaderPlan Plan(string name, string surface, ShaderCompileTarget target)
    {
        if (string.IsNullOrWhiteSpace(name) || target != ShaderCompileTarget.WebGPUWgsl || surface is not ("masked" or "alpha-blend"))
            throw new NotSupportedException("Textured alpha requires a named WebGPU masked or sorted alpha-blend authored material.");
        return new(name, "TexturedAlphaLocalShadows.slang", Schema, true, false, surface);
    }

    /// <summary>Requires the canonical logical source path and exact normalized authored shader text.</summary>
    public static bool TryValidateDesktopSource(string relativePath, string source, out string reason)
    {
        reason = "Textured alpha requires the exact canonical Common/LitTexturedAlphaForward.fs source and unchanged snippet graph.";
        if (relativePath.Replace('\\', '/') != DesktopFragmentPath || NormalizedHash(source) != RequiredDesktopSources[0].Sha256)
            return false;
        reason = string.Empty;
        return true;
    }

    /// <summary>Validates every source in the closed engine snippet graph without accepting same-named replacements.</summary>
    public static bool TryValidateDesktopSources(Func<string, string?> readSource, out string reason)
    {
        ArgumentNullException.ThrowIfNull(readSource);
        foreach (EngineLitMaterialShaderSource source in RequiredDesktopSources)
        {
            string? text = readSource(source.Path);
            if (text is null || NormalizedHash(text) != source.Sha256)
            {
                reason = $"Textured alpha canonical source '{source.Path}' is absent or modified.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    public static string NormalizedHash(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))));

    /// <summary>Complete physical layout hashes, independent of descriptor property ordering.</summary>
    public static string LayoutHash(string pass, bool orderGate = false) => (pass, orderGate) switch
    {
        ("forward-textured-alpha", false) => "3acad2f79d9c6be1326e6da3af55a7fa7feef4b90e56d9ec316fe5eca7c6d4c1",
        ("forward-textured-alpha", true) => "0b25cd9d6b9f9717f913f06d01472d148762cb80fd79dc687d2f3407eed24305",
        ("depth-normal", false) => "85c45ead4753872d9afef75ac2ff0097136fda6de759a8989540d921cec090da",
        ("depth", false) => "e555981aecf11bf0acc2af32d0a2162488a01386969a8d59210c142c9339a0c4",
        ("point-shadow-depth", false) => "2ebba07d27b1d5cc44b3dd6cfc274a200373a2cc802de95b76c51e7448e68eab",
        ("spot-shadow-depth", false) => "e555981aecf11bf0acc2af32d0a2162488a01386969a8d59210c142c9339a0c4",
        _ => throw new NotSupportedException($"Textured alpha has no layout for '{pass}'."),
    };

    public static ImmutableArray<EngineLitMaterialShaderSource> CompanionSources(string pass, bool orderGate = false)
        => (pass, orderGate) switch
        {
            (Pass, true) => [
        new("TexturedAlphaLocalShadowsAuthoredOrder.slang", "6bff7be695da1b5ba98e9ec31d462d03389c4dbd5fd531c91bd5896c34f463af"),
        new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"),
                .. RequiredCanonicalSources],
            ("depth-normal", false) => [new("TexturedAlphaDepthNormal.slang", "0b227c33047e9421e8d7ee4999ddb384aefe1c1f7f9a5e87516cb4036348ecd6"), RequiredCanonicalSources[2]],
            ("depth", false) => [new("TexturedAlphaDepth.slang", "fb0ffa8795bc2285471d0825d3dd43ec42c9e9d9968521075081021e02bb923b"), RequiredCanonicalSources[2]],
            ("point-shadow-depth", false) => [new("TexturedAlphaPointDepth.slang", "8d2af4fe01de56561e305dc4136b471ea376bf98f709bafe4f455037d99328cd"), RequiredCanonicalSources[2]],
            ("spot-shadow-depth", false) => [new("TexturedAlphaSpotDepth.slang", "12206801db56a79403f703c939d0f8695d16cb0117f6b4eda6345a76e62632be"), RequiredCanonicalSources[2]],
            _ => [],
        };
}
