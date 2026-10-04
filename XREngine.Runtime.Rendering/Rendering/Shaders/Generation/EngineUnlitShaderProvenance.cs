using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Proves the canonical unlit Slang/GLSL closure and exact physical raster ABI.</summary>
public static class EngineUnlitShaderProvenance
{
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, Proof> Proofs = new();
    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool TryValidate(ShaderProgramArtifact artifact, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        reason = proof.Valid && proof.Main ? string.Empty : proof.Reason;
        return proof.Valid && proof.Main;
    }

    /// <summary>Proves a catalog-declared, source-free built-in receiver rather than a per-material cook.</summary>
    public static bool TryValidateBuiltIn(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        bool accepted = proof.Valid && proof.Main && proof.BuiltIn && proof.Key == key;
        reason = accepted ? string.Empty : proof.Valid
            ? "Unlit built-in receiver does not match its declared semantic and forward profile." : proof.Reason;
        return accepted;
    }

    public static bool TryValidateCompanion(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        bool accepted = proof.Valid && !proof.Main && proof.Key == key;
        reason = accepted ? string.Empty : proof.Valid
            ? "Unlit companion does not match the requested semantic, pass, and profile." : proof.Reason;
        return accepted;
    }

    private sealed class Proof
    {
        internal bool Valid { get; }
        internal bool Main { get; }
        internal bool BuiltIn { get; }
        internal EngineMaterialVariantKey Key { get; }
        internal string Reason { get; private set; } = "Unlit requires the exact versioned source closure and physical ABI; recook the surface and companions.";

        internal Proof(ShaderProgramArtifact artifact)
        {
            try
            {
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                Reason = "Unlit artifact reconstruction, target, or vertex entry differs from its descriptor.";
                if (!SameArtifact(artifact, verified) || verified.Target != ShaderCompileTarget.WebGPUWgsl || verified.ComputeEntryPoint is not null ||
                    verified.NativeVertexCompanion is not null || verified.VertexEntryPoint != "unlitVertex") return;
                using JsonDocument parsed = JsonDocument.Parse(verified.DescriptorBytes.AsMemory());
                JsonElement descriptor = parsed.RootElement;
                Reason = "Unlit descriptor has an unmodeled define, include, specialization, or layout.";
                if (!EmptyArray(descriptor, "defines") || !EmptyArray(descriptor, "includes") ||
                    !descriptor.TryGetProperty("specialization", out JsonElement specialization) ||
                    specialization.ValueKind != JsonValueKind.Object || specialization.EnumerateObject().Any() ||
                    !descriptor.TryGetProperty("layout", out JsonElement layout)) return;
                Main = verified.SourceLanguage == "MaterialRecipe";
                int version;
                bool orderGate = verified.SemanticSchemaIdentity == EngineUnlitMaterialShaderGenerator.OrderGateSchema;
                Reason = "Unlit semantic, pass, stage entry, or material-variant declaration differs from its modeled source.";
                if (Main)
                {
                    version = VersionForSchema(verified.SemanticSchemaIdentity);
                    if (version == 0 || verified.Pass != EngineUnlitMaterialShaderGenerator.Pass ||
                        verified.FragmentEntryPoint != "unlitFragment") return;
                    if (descriptor.TryGetProperty("materialVariant", out JsonElement declaration))
                    {
                        Key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                        EngineMaterialSemanticIdentity builtInSemantic = new(EngineMaterialSemantic.Unlit, version);
                        if (Key != EngineUnlitMaterialShaderGenerator.BuiltInKey(builtInSemantic) ||
                            verified.Name != EngineUnlitMaterialShaderGenerator.BuiltInName(builtInSemantic)) return;
                        BuiltIn = true;
                    }
                }
                else
                {
                    if (verified.SourceLanguage != "Slang" ||
                        !descriptor.TryGetProperty("materialVariant", out JsonElement declaration)) return;
                    Key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                    if (!Key.Semantic.IsUnlit()) return;
                    version = Key.Semantic.Version;
                    if (verified.SemanticSchemaIdentity != (orderGate ? EngineUnlitMaterialShaderGenerator.OrderGateSchema :
                        EngineUnlitMaterialShaderGenerator.SchemaFor(Key.Semantic)) ||
                        Key != EngineUnlitMaterialShaderGenerator.CompanionKey(Key.Semantic, verified.Pass, orderGate)) return;
                    string expectedFragment = verified.Pass switch
                    {
                        EngineUnlitMaterialShaderGenerator.Pass => "unlitFragment",
                        "depth-normal" => "unlitNormalFragment",
                        "depth" => "unlitDepthFragment",
                        "point-shadow-depth" => "unlitPointDepthFragment",
                        "spot-shadow-depth" => "unlitSpotDepthFragment",
                        _ => string.Empty,
                    };
                    if (verified.FragmentEntryPoint != expectedFragment) return;
                }
                EngineMaterialSemanticIdentity semantic = new(EngineMaterialSemantic.Unlit, version);
                Reason = "Unlit physical raster layout differs from the pinned pass ABI.";
                if (CanonicalHash(layout) != LayoutHash(version, verified.Pass, orderGate)) return;
                ImmutableArray<EngineLitMaterialShaderSource> frontend = Main
                    ? EngineUnlitMaterialShaderGenerator.RequiredCanonicalSources(semantic)
                    : EngineUnlitMaterialShaderGenerator.CompanionSources(semantic, verified.Pass, orderGate);
                ImmutableArray<EngineLitMaterialShaderSource> desktop = EngineUnlitMaterialShaderGenerator.RequiredDesktopSources(semantic);
                Reason = "Unlit source closure is incomplete or includes a modified canonical Slang/GLSL source.";
                if (!HasClosure(descriptor, frontend, desktop, Main, BuiltIn ? verified.Name : null)) return;
                Valid = true;
                Reason = string.Empty;
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Reason = "Unlit artifact proof failed: " + error.Message;
            }
        }
    }

    private static int VersionForSchema(string schema) => schema switch
    {
        EngineUnlitMaterialShaderGenerator.ColorSchema => 1,
        EngineUnlitMaterialShaderGenerator.TextureSchema => 2,
        EngineUnlitMaterialShaderGenerator.OpaqueTextureSchema => 3,
        EngineUnlitMaterialShaderGenerator.AlphaTextureSchema => 4,
        EngineUnlitMaterialShaderGenerator.TextureArraySliceSchema => 5,
        _ => 0,
    };

    private static bool HasClosure(JsonElement descriptor, ImmutableArray<EngineLitMaterialShaderSource> frontend,
        ImmutableArray<EngineLitMaterialShaderSource> desktop, bool main, string? builtInName)
    {
        if (!descriptor.TryGetProperty("sourceMap", out JsonElement map) ||
            !map.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != (main ? "generated" : "unmapped") ||
            !map.TryGetProperty("path", out JsonElement mappedPath) || mappedPath.ValueKind != JsonValueKind.String ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array ||
            dependencies.GetArrayLength() != frontend.Length + desktop.Length + (main ? 2 : 1)) return false;
        string sourcePath = mappedPath.GetString()!;
        if (main ? !sourcePath.EndsWith(".material.json", StringComparison.Ordinal)
            : !PathMatches(sourcePath, frontend[0].Path)) return false;
        if (builtInName is not null && !PathMatches(sourcePath, builtInName + ".material.json")) return false;
        uint seenFrontend = 0, seenDesktop = 0;
        bool material = false, recipe = false;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !dependency.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String) return false;
            string path = pathValue.GetString()!, hash = hashValue.GetString()!;
            if (main && path == sourcePath && !material)
            {
                if (builtInName is not null && hash != BuiltInMaterialHash(builtInName)) return false;
                material = true;
                continue;
            }
            if (path.EndsWith(".recipe.json", StringComparison.Ordinal) && !recipe)
            {
                if (builtInName is not null &&
                    (!PathMatches(path, builtInName + ".recipe.json") || hash != BuiltInRecipeHash(builtInName))) return false;
                recipe = true;
                continue;
            }
            if (Match(frontend, path, hash, ref seenFrontend) || Match(desktop, path, hash, ref seenDesktop)) continue;
            return false;
        }
        return (!main || material) && recipe && seenFrontend == (1u << frontend.Length) - 1u &&
            seenDesktop == (1u << desktop.Length) - 1u;
    }

    private static string BuiltInMaterialHash(string name) => name switch
    {
        "engine-unlit-color" => "2ddb6eeafde5901bd8664647c249bef651e741d88d8910120d1a631a32fc4d15",
        "engine-unlit-texture" => "e307fbf922a408c1341679e549379b9bac8e24e3ff7ec10aac6569994214a49f",
        "engine-unlit-opaque-texture" => "9f3d09e89d4d468c38765fd1990de184354ec062e8875342286aa7acc39a1558",
        "engine-unlit-alpha-texture" => "da6bc6c8066819b6b6516b88857d614a8cb55d283dd9361c9e3aba8247703359",
        "engine-unlit-texture-array-slice" => "d54db2878b5c44eb74d8d13a5943ac7e9fef5ac18ab0a14ecd163d6f2b9a0847",
        _ => string.Empty,
    };

    private static string BuiltInRecipeHash(string name) => name switch
    {
        "engine-unlit-color" => "786df5be81b6dd9954e581505776c93fb7d102d79bcdcf56b031a730e41988ab",
        "engine-unlit-texture" => "e276f2593fe25b2faa73d05b5edf8f16c45049b1350c0228bb03a351cf49466e",
        "engine-unlit-opaque-texture" => "aa9de0cc5e687ea6f0afabb158ba62f76582a212b52bf9ef9e1e89e86e34d2a2",
        "engine-unlit-alpha-texture" => "65035ca0c05caf942faa966bc127b8bdd12a531c8ad582e78bc446bd3dc347bd",
        "engine-unlit-texture-array-slice" => "53e4cf2eda064e53c4cdc986e01d41c7b5dda558273e14b5abe36b0982ff7af2",
        _ => string.Empty,
    };

    private static bool Match(ImmutableArray<EngineLitMaterialShaderSource> sources, string path, string hash, ref uint seen)
    {
        for (int index = 0; index < sources.Length; index++)
        {
            if (!PathMatches(path, sources[index].Path)) continue;
            if ((seen & (1u << index)) != 0 || hash != sources[index].Sha256) return false;
            seen |= 1u << index;
            return true;
        }
        return false;
    }

    private static bool PathMatches(string path, string relative)
        => path == relative || path.EndsWith("/" + relative, StringComparison.Ordinal);

    private static bool EmptyArray(JsonElement descriptor, string name)
        => descriptor.TryGetProperty(name, out JsonElement values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 0;

    private static bool SameArtifact(ShaderProgramArtifact source, ShaderProgramArtifact verified)
    {
        if (source.Identity != verified.Identity || source.Name != verified.Name || source.Pass != verified.Pass ||
            source.SourcePath != verified.SourcePath || source.SourceLanguage != verified.SourceLanguage ||
            source.SemanticSchemaIdentity != verified.SemanticSchemaIdentity || source.Coordinates != verified.Coordinates ||
            source.VertexEntryPoint != verified.VertexEntryPoint || source.FragmentEntryPoint != verified.FragmentEntryPoint ||
            source.ComputeEntryPoint != verified.ComputeEntryPoint || source.NativeVertexCompanion != verified.NativeVertexCompanion ||
            source.ComputeWorkgroupSize != verified.ComputeWorkgroupSize || source.Target != verified.Target ||
            source.VertexBuffers.Length != verified.VertexBuffers.Length || source.Resources.Length != verified.Resources.Length ||
            source.RequiredLimits.Count != verified.RequiredLimits.Count) return false;
        for (int index = 0; index < source.VertexBuffers.Length; index++)
        {
            ShaderVertexBufferLayout left = source.VertexBuffers[index], right = verified.VertexBuffers[index];
            if (left with { Attributes = right.Attributes } != right || !left.Attributes.AsSpan().SequenceEqual(right.Attributes.AsSpan())) return false;
        }
        for (int index = 0; index < source.Resources.Length; index++)
        {
            ShaderStageResourceLayout left = source.Resources[index], right = verified.Resources[index];
            if (left with { Contract = right.Contract } != right || left.Contract with { Members = right.Contract.Members } != right.Contract ||
                !left.Contract.Members.AsSpan().SequenceEqual(right.Contract.Members.AsSpan())) return false;
        }
        foreach ((string key, int value) in source.RequiredLimits)
            if (!verified.RequiredLimits.TryGetValue(key, out int expected) || value != expected) return false;
        return true;
    }

    private static string LayoutHash(int version, string pass, bool orderGate) => (version, pass, orderGate) switch
    {
        (1, "forward-unlit", false) => "405a9fe1a59d60c6d97e19144045788a32339ecda1ec5a5e3cbd976aca2fedf1",
        (2, "forward-unlit", false) or (3, "forward-unlit", false) => "783f91a44598e2d18512c46e0a091828b5cf730744de2d7ff9e858932fe84f38",
        (4, "forward-unlit", false) => "ece5586ac339efdf936dc3a09705e8d702ca0bc9aa4da7313f758c80b136f2a0",
        (5, "forward-unlit", false) => "47008eef686a2d8adf247fec86e664a04094397e1a59ca59815f539a0657eba8",
        (1, "depth-normal", false) => "2a030164369d192cb201c4a3ab5882b7d2e3011f1fe9afdad024fff5ff22836e",
        (2 or 3 or 5, "depth-normal", false) => "96cc01a0d039f0a5f3ff5cf6ebc75bab1b62803241dbc03e402becc71067e087",
        (4, "depth-normal" or "depth" or "spot-shadow-depth", false) => "ece5586ac339efdf936dc3a09705e8d702ca0bc9aa4da7313f758c80b136f2a0",
        (4, "point-shadow-depth", false) => "77a9c1451bc4fd6c60c3bdcbd4cb2f42c5bd29deda9f840c259da928cc40dc77",
        (1, "forward-unlit", true) => "92635351e1b60d2db3c636672c771bb8cf6eef1ac0cb7be6e36ed6e0c54ffea2",
        (2, "forward-unlit", true) => "21ab50434a0edb49241a310467a363d86ed685169cf1f59fc26b618b3b2f39ca",
        (4, "forward-unlit", true) => "bfd70c59e972d053b26c24e3c13f40ae5cd19717fb7397cca38281480d5a534c",
        (5, "forward-unlit", true) => "6bb88412f4a0dd291dc71b2dbeb979909f05c66c985bb57ff527adc1430a1327",
        _ => throw new NotSupportedException("Unlit pass has no pinned physical layout."),
    };

    private static string CanonicalHash(JsonElement value)
    {
        StringBuilder builder = new();
        AppendCanonical(builder, value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            builder.Append('{');
            bool first = true;
            foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(JsonSerializer.Serialize(property.Name, CanonicalOptions)).Append(':');
                AppendCanonical(builder, property.Value);
            }
            builder.Append('}');
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            builder.Append('[');
            bool first = true;
            foreach (JsonElement element in value.EnumerateArray())
            {
                if (!first) builder.Append(',');
                first = false;
                AppendCanonical(builder, element);
            }
            builder.Append(']');
        }
        else if (value.ValueKind == JsonValueKind.String) builder.Append(JsonSerializer.Serialize(value.GetString(), CanonicalOptions));
        else builder.Append(value.GetRawText());
    }
}
