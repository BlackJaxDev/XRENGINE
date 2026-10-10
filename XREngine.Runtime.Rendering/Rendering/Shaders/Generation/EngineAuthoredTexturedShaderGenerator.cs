using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Exact authored forward normal/specular families and their pinned raster contracts.</summary>
public static class EngineAuthoredTexturedShaderGenerator
{
    public const string Schema = "xrengine.engine.authored-lit-textured.v1";
    public const string Pass = "forward-authored-textured";
    public const string OrderGateSchema = "xrengine.engine.authored-textured-order-gate.v1";
    public const string DesktopStagingDirectory = "Desktop";
    public const string TextureVertexProfile = "position-normal-optional-tangent-uv-v1";
    public const string OrderGateVertexProfile = "position-normal-optional-tangent-uv-order-gate-v1";
    public const string DepthVertexProfile = "position-normal-uv-v1";

    public static bool ValidTextureFlags(int flags) => flags is 1 or 2 or 3 or 5 or 6 or 7;

    public static string VertexProfile(int flags, bool orderGate = false)
    {
        ValidateFlags(flags);
        return orderGate ? OrderGateVertexProfile : TextureVertexProfile;
    }

    public static string DesktopFragmentPath(int flags) => flags switch
    {
        1 => "Common/LitTexturedNormalForward.fs",
        2 => "Common/LitTexturedSpecForward.fs",
        3 => "Common/LitTexturedNormalSpecForward.fs",
        5 => "Common/LitTexturedNormalAlphaForward.fs",
        6 => "Common/LitTexturedSpecAlphaForward.fs",
        7 => "Common/LitTexturedNormalSpecAlphaForward.fs",
        _ => throw new ArgumentOutOfRangeException(nameof(flags)),
    };

    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredCanonicalSources { get; } =
    [
        new("AuthoredTexturedLocalShadows.slang", "9b85e67842a8a8cf21b81ce94e32680d0cb67cd80213c4ec3b73591f8f7629eb"),
        new("StandardLitColorDirectionalShadow.slang", "eb50a3041d0ebfc17f8ed75d3e15b3196926df32d5cf5d4bf0e81d040c9b07fe"),
        new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc"),
        new("AuthoredTexturedSampling.slang", "413b6e6677fc6bd92ed5dea00913173ad684ffaff50f325f69dcd53ec647edb2"),
        new("AuthoredTexturedNormalMapping.slang", "596b49170acfe319a84e1765b450b900ed8be7d9cda283f873e3cf2dd0a0c224"),
        new("AuthoredTexturedVertex.slang", "ac588df177fc394f898d90220bedbdf5e184141d6b5860ea79253bd8b9180db6"),
    ];

    private static readonly ImmutableArray<EngineLitMaterialShaderSource> DesktopFragments =
    [
        new("Common/LitTexturedNormalForward.fs", "9c292ae986ed8322111a877db84ca85a5ec5ed3515ab0305c7fccae5fb2f6872"),
        new("Common/LitTexturedSpecForward.fs", "54e5536cf9f0147f94ca16967e9e486043057b8df5a9d9011cfd087557afead0"),
        new("Common/LitTexturedNormalSpecForward.fs", "b7d744ddccb3abefdb3a0edde96d8a02687ee2963dc9feaac7efb2b60afc4d8f"),
        new("Common/LitTexturedNormalAlphaForward.fs", "2a098c1d5a9b20b6d66a9d881d799263a5f1a220a73c88a1a7ec6057920a3bf6"),
        new("Common/LitTexturedSpecAlphaForward.fs", "5b3dec32a771dd62c24443678fd59e5e5a86ada3f0829d54d5a04b67b5286f3e"),
        new("Common/LitTexturedNormalSpecAlphaForward.fs", "1b0b3ccdebf8f57f271725f458f07fcf48c6abd66ff7b2879f09f51f936d502e"),
    ];
    private static readonly ImmutableArray<EngineLitMaterialShaderSource> DesktopSnippets =
    [
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
    private static readonly EngineLitMaterialShaderSource DesktopNormal = new("Snippets/SurfaceDetailNormalMapping.glsl", "1f35d80a2c3fecef7d7d3257a481baa2262a59620031f7801608ef3d52a30b02");
    private static readonly ImmutableArray<EngineLitMaterialShaderSource>[] DesktopClosures = CreateDesktopClosures();
    private static readonly EngineMaterialVariantKey[,] CompanionKeys = CreateCompanionKeys();

    private static ImmutableArray<EngineLitMaterialShaderSource>[] CreateDesktopClosures()
    {
        ImmutableArray<EngineLitMaterialShaderSource>[] sources = new ImmutableArray<EngineLitMaterialShaderSource>[8];
        int index = 0;
        foreach (int flags in new[] { 1, 2, 3, 5, 6, 7 })
            sources[flags] = (flags & 1) != 0
                ? [DesktopFragments[index++], .. DesktopSnippets, DesktopNormal]
                : [DesktopFragments[index++], .. DesktopSnippets];
        return sources;
    }

    public static ImmutableArray<EngineLitMaterialShaderSource> DesktopSources(int textureFlags)
    {
        ValidateFlags(textureFlags);
        return DesktopClosures[textureFlags];
    }

    public static EngineLitMaterialShaderPlan Plan(string name, int textureFlags, string surface, ShaderCompileTarget target)
    {
        ValidateFlags(textureFlags);
        bool opacity = (textureFlags & 4) != 0;
        if (string.IsNullOrWhiteSpace(name) || target != ShaderCompileTarget.WebGPUWgsl ||
            !(surface == "alpha-blend" || surface == (opacity ? "masked" : "opaque")))
            throw new NotSupportedException("Authored textured surfaces require a named WebGPU exact forward family and compatible coverage mode.");
        return new(name, "AuthoredTexturedLocalShadows.slang", Schema, true,
            (textureFlags & 1) != 0, surface, textureFlags);
    }

    public static bool TryValidateDesktopSource(int textureFlags, string relativePath, string source, out string reason)
    {
        reason = "Authored textured materials require the exact canonical source path and unchanged source graph.";
        if (!ValidTextureFlags(textureFlags) || relativePath.Replace('\\', '/') != DesktopFragmentPath(textureFlags) ||
            NormalizedHash(source) != DesktopSources(textureFlags)[0].Sha256) return false;
        reason = string.Empty;
        return true;
    }

    public static bool TryValidateDesktopSources(int textureFlags, Func<string, string?> readSource, out string reason)
    {
        ArgumentNullException.ThrowIfNull(readSource);
        foreach (EngineLitMaterialShaderSource source in DesktopSources(textureFlags))
        {
            string? text = readSource(source.Path);
            if (text is null || NormalizedHash(text) != source.Sha256)
            {
                reason = $"Authored textured canonical source '{source.Path}' is absent or modified.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    public static string NormalizedHash(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))));

    public static EngineMaterialVariantKey CompanionKey(int textureFlags, string pass, bool orderGate = false)
    {
        ValidateFlags(textureFlags);
        int index = (pass, orderGate) switch
        {
            (Pass, true) => 0,
            ("depth-normal", false) => 1,
            ("depth", false) => 2,
            ("point-shadow-depth", false) => 3,
            ("spot-shadow-depth", false) => 4,
            _ => throw new NotSupportedException($"Authored textured has no companion '{pass}'."),
        };
        return CompanionKeys[textureFlags, index];
    }

    private static EngineMaterialVariantKey[,] CreateCompanionKeys()
    {
        EngineMaterialVariantKey[,] keys = new EngineMaterialVariantKey[8, 5];
        string[] passes = [Pass, "depth-normal", "depth", "point-shadow-depth", "spot-shadow-depth"];
        string[] outputs = ["linear-hdr-local-shadows", "normal-rgba16f", "depth-normal", "radial-r16f", "projected-r16f"];
        foreach (int flags in new[] { 1, 2, 3, 5, 6, 7 })
            for (int index = 0; index < passes.Length; index++)
                keys[flags, index] = new(EngineMaterialSemanticIdentity.AuthoredLitTexturedV1, ShaderCompileTarget.WebGPUWgsl,
                    passes[index], index < 2 ? VertexProfile(flags, index == 0) : DepthVertexProfile,
                    $"{outputs[index]}-f{flags}-v1");
        return keys;
    }

    public static bool TryGetCompanionTextureFlags(EngineMaterialVariantKey key, out int textureFlags)
    {
        textureFlags = 0;
        string output = key.OutputProfile;
        if (key.Semantic != EngineMaterialSemanticIdentity.AuthoredLitTexturedV1 || key.Target != ShaderCompileTarget.WebGPUWgsl ||
            string.IsNullOrEmpty(output) || output.Length < 6 || output[^6] != '-' || output[^5] != 'f' ||
            !output.EndsWith("-v1", StringComparison.Ordinal)) return false;
        int flags = output[^4] - '0';
        if (!ValidTextureFlags(flags)) return false;
        try
        {
            if (key != CompanionKey(flags, key.Pass, key.Pass == Pass)) return false;
        }
        catch (NotSupportedException) { return false; }
        textureFlags = flags;
        return true;
    }

    public static string LayoutHash(string pass, bool orderGate = false) => (pass, orderGate) switch
    {
        ("forward-authored-textured", false) => "9bd1be3aa52de75c3ba3c41ae5edaa91660939ed9413ec88b6308f81a29678b5",
        ("forward-authored-textured", true) => "1ef938c5ce01575717879b0756ab0f2113bed590b5701757513665a9d61d8622",
        ("depth-normal", false) => "6ff1976e0fba45389becefd82ffe91926b771b659bb86a539d44c145852fe6bf",
        ("depth", false) => "7662dfa6e9b475ad1206794b8fad1c1ad5a69b4993af0e67502f3daab531b17a",
        ("point-shadow-depth", false) => "6828c2afa494f93ea726fb49447598d14c78b371ca0e4383f6be1a115b083442",
        ("spot-shadow-depth", false) => "7662dfa6e9b475ad1206794b8fad1c1ad5a69b4993af0e67502f3daab531b17a",
        _ => throw new NotSupportedException($"Authored textured has no layout for '{pass}'."),
    };

    public static ImmutableArray<EngineLitMaterialShaderSource> CompanionSources(string pass, bool orderGate = false)
        => (pass, orderGate) switch
        {
            (Pass, true) => [new("AuthoredTexturedLocalShadowsAuthoredOrder.slang", "f408ce14a02a790f4d80f93f0e991606c8f3ff8524838c7f26f8e55a4de23ef1"),
                new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"), .. RequiredCanonicalSources],
            ("depth-normal", false) => [new("AuthoredTexturedDepthNormal.slang", "6fd88aef39f5b36b6fc64eaf694be21e47c061b10dea584dc579c8780f7fa9f1"),
                RequiredCanonicalSources[3], RequiredCanonicalSources[4], RequiredCanonicalSources[5]],
            ("depth", false) => [new("AuthoredTexturedDepth.slang", "e0d441348440dda991b81c3f5f009349c857084c3bf5db5952ee7972ad2553fd"), RequiredCanonicalSources[3]],
            ("point-shadow-depth", false) => [new("AuthoredTexturedPointDepth.slang", "da9a5392d1b45f729b984d61d416d9cc0525fac9a043ed41be8dd7de78ede0ab"), RequiredCanonicalSources[3]],
            ("spot-shadow-depth", false) => [new("AuthoredTexturedSpotDepth.slang", "2f896bd057cd598c59cfe259160a76fbffe794cd6e1fe42e82b6a5aee9ef2f91"), RequiredCanonicalSources[3]],
            _ => [],
        };

    private static void ValidateFlags(int flags)
    {
        if (!ValidTextureFlags(flags)) throw new ArgumentOutOfRangeException(nameof(flags), "Expected a canonical normal/specular authored family.");
    }
}
