using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Proves the exact normal/specular frontend, authored desktop closure, and physical raster ABI.</summary>
public static class EngineAuthoredTexturedShaderProvenance
{
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, Proof> Proofs = new();
    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool TryValidate(ShaderProgramArtifact artifact, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        reason = proof.Reason ?? (proof.Authored ? string.Empty : "The authored-textured receiver requires its exact authored MaterialRecipe.");
        return proof.Reason is null && proof.Authored;
    }

    public static bool TryValidate(ShaderProgramArtifact artifact, out int textureFlags, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        textureFlags = proof.TextureFlags;
        reason = proof.Reason ?? (proof.Authored ? string.Empty : "The authored-textured receiver requires its exact authored MaterialRecipe.");
        return proof.Reason is null && proof.Authored;
    }

    public static bool TryValidateCompanion(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        reason = proof.Reason ?? (proof.Key == key && !proof.Authored ? string.Empty : "The authored-textured companion does not match the requested pass and profile.");
        return proof.Reason is null && !proof.Authored && proof.Key == key;
    }

    private sealed class Proof
    {
        internal bool Authored { get; }
        internal int TextureFlags { get; }
        internal EngineMaterialVariantKey Key { get; }
        internal string? Reason { get; }

        internal Proof(ShaderProgramArtifact artifact)
        {
            Reason = "Authored textured requires a verified canonical source closure and exact physical ABI; recook the material and its companions.";
            try
            {
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (!SameArtifact(artifact, verified) || verified.Target != ShaderCompileTarget.WebGPUWgsl ||
                    verified.ComputeEntryPoint is not null || verified.NativeVertexCompanion is not null) return;
                using JsonDocument document = JsonDocument.Parse(verified.DescriptorBytes.AsMemory());
                JsonElement descriptor = document.RootElement;
                if (!EmptyArray(descriptor, "defines") || !EmptyArray(descriptor, "includes") ||
                    !descriptor.TryGetProperty("specialization", out JsonElement specialization) ||
                    specialization.ValueKind != JsonValueKind.Object || specialization.EnumerateObject().Any()) return;
                bool gate = verified.SemanticSchemaIdentity == EngineAuthoredTexturedShaderGenerator.OrderGateSchema;
                Authored = verified.SourceLanguage == "MaterialRecipe";
                if (verified.SemanticSchemaIdentity != (gate ? EngineAuthoredTexturedShaderGenerator.OrderGateSchema : EngineAuthoredTexturedShaderGenerator.Schema) ||
                    !descriptor.TryGetProperty("layout", out JsonElement layout) ||
                    CanonicalHash(layout) != EngineAuthoredTexturedShaderGenerator.LayoutHash(verified.Pass, gate) ||
                    !HasEntries(verified)) return;
                if (Authored)
                {
                    if (gate || verified.Pass != EngineAuthoredTexturedShaderGenerator.Pass || descriptor.TryGetProperty("materialVariant", out _) ||
                        !descriptor.TryGetProperty("authoredTextureFlags", out JsonElement flagsValue) || !flagsValue.TryGetInt32(out int flags) ||
                        !EngineAuthoredTexturedShaderGenerator.ValidTextureFlags(flags) || !HasAuthoredClosure(descriptor, flags)) return;
                    TextureFlags = flags;
                }
                else
                {
                    if (verified.SourceLanguage != "Slang" || !descriptor.TryGetProperty("materialVariant", out JsonElement declaration)) return;
                    Key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                    if (!EngineAuthoredTexturedShaderGenerator.TryGetCompanionTextureFlags(Key, out int flags) ||
                        gate != (Key.Pass == EngineAuthoredTexturedShaderGenerator.Pass) ||
                        !HasCompanionClosure(descriptor, verified.Pass, gate)) return;
                    TextureFlags = flags;
                }
                Reason = null;
            }
            catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        }
    }

    private static bool HasEntries(ShaderProgramArtifact artifact)
        => (artifact.Pass, artifact.VertexEntryPoint, artifact.FragmentEntryPoint) is
            (EngineAuthoredTexturedShaderGenerator.Pass, "standardLitVertex", "standardLitFragment") or
            ("depth-normal", "depthNormalVertex", "depthNormalFragment") or
            ("depth", "coverageDepthVertex", "coverageDepthFragment") or
            ("point-shadow-depth", "pointShadowDepthVertex", "pointShadowDepthFragment") or
            ("spot-shadow-depth", "spotShadowDepthVertex", "spotShadowDepthFragment");

    private static bool HasAuthoredClosure(JsonElement descriptor, int textureFlags)
    {
        if (!SourcePath(descriptor, out string materialPath) || !materialPath.EndsWith(".material.json", StringComparison.Ordinal) ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array) return false;
        ImmutableArray<EngineLitMaterialShaderSource> frontend = EngineAuthoredTexturedShaderGenerator.RequiredCanonicalSources;
        ImmutableArray<EngineLitMaterialShaderSource> desktop = EngineAuthoredTexturedShaderGenerator.DesktopSources(textureFlags);
        if (dependencies.GetArrayLength() != frontend.Length + desktop.Length + 2) return false;
        uint seenFrontend = 0, seenDesktop = 0;
        bool material = false, recipe = false;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!Dependency(dependency, out string path, out string hash)) return false;
            if (path == materialPath && !material) { material = true; continue; }
            if (path.EndsWith(".recipe.json", StringComparison.Ordinal) && !recipe) { recipe = true; continue; }
            if (Match(frontend, path, hash, string.Empty, ref seenFrontend) ||
                Match(desktop, path, hash, EngineAuthoredTexturedShaderGenerator.DesktopStagingDirectory + "/", ref seenDesktop)) continue;
            return false;
        }
        return material && recipe && seenFrontend == (1u << frontend.Length) - 1 && seenDesktop == (1u << desktop.Length) - 1;
    }

    private static bool HasCompanionClosure(JsonElement descriptor, string pass, bool gate)
    {
        ImmutableArray<EngineLitMaterialShaderSource> sources = EngineAuthoredTexturedShaderGenerator.CompanionSources(pass, gate);
        if (sources.IsEmpty || !SourcePath(descriptor, out string sourcePath) || !MatchesPath(sourcePath, sources[0].Path) ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array ||
            dependencies.GetArrayLength() != sources.Length + 1) return false;
        uint seen = 0;
        bool recipe = false;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!Dependency(dependency, out string path, out string hash)) return false;
            if (path.EndsWith(".recipe.json", StringComparison.Ordinal) && !recipe) { recipe = true; continue; }
            if (!Match(sources, path, hash, string.Empty, ref seen)) return false;
        }
        return recipe && seen == (1u << sources.Length) - 1;
    }

    private static bool Match(ImmutableArray<EngineLitMaterialShaderSource> sources, string path, string hash, string prefix, ref uint seen)
    {
        for (int index = 0; index < sources.Length; index++)
        {
            if (!MatchesPath(path, prefix + sources[index].Path)) continue;
            if ((seen & (1u << index)) != 0 || hash != sources[index].Sha256) return false;
            seen |= 1u << index;
            return true;
        }
        return false;
    }

    private static bool MatchesPath(string path, string source)
        => path == source || path.EndsWith("/" + source, StringComparison.Ordinal);

    private static bool EmptyArray(JsonElement descriptor, string name)
        => descriptor.TryGetProperty(name, out JsonElement values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 0;

    private static bool SourcePath(JsonElement descriptor, out string path)
    {
        path = string.Empty;
        if (!descriptor.TryGetProperty("sourceMap", out JsonElement map) ||
            !map.TryGetProperty("path", out JsonElement value) || value.ValueKind != JsonValueKind.String) return false;
        path = value.GetString()!;
        return true;
    }

    private static bool Dependency(JsonElement value, out string path, out string hash)
    {
        path = hash = string.Empty;
        if (!value.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String) return false;
        path = pathValue.GetString()!;
        hash = hashValue.GetString()!;
        return true;
    }

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
                builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
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
        else if (value.ValueKind == JsonValueKind.String)
            builder.Append(JsonSerializer.Serialize(value.GetString(), CanonicalOptions));
        else builder.Append(value.GetRawText());
    }
}
