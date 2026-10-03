using System.Runtime.CompilerServices;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Proves the canonical opaque PBR receiver and caster programs selected by a generated surface.</summary>
public static class EngineLitShadowCompanionContract
{
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, Proof> Proofs = new();

    /// <summary>Reads the live generated source before choosing an equivalent engine receiver.</summary>
    public static bool TryGetReceiverKey(XRMaterial material, IShaderProgramArtifactResolver? resolver,
        bool localShadows, out EngineMaterialVariantKey key, out string reason)
    {
        key = default;
        reason = "Opaque shadow replay requires a canonical generated AuthoredLitV1 surface; recook the authored material.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitV1 ||
            !EngineAuthoredLitNativeAdmission.TryRead(material, resolver, out _, out StandardLitTextureSurface texture,
                out bool textured, out reason))
            return false;
        key = new(textured ? EngineMaterialSemanticIdentity.StandardLitTextureV1 : EngineMaterialSemanticIdentity.StandardLitColorV1,
            ShaderCompileTarget.WebGPUWgsl, "opaque-forward", textured ? texture.VertexProfile : "static-position-normal-v1",
            localShadows ? "linear-hdr-local-shadows-v1" : "linear-hdr-directional-shadow-v1");
        return true;
    }

    /// <summary>Checks a catalog companion once, retaining no writable descriptor or module data.</summary>
    public static bool TryValidate(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
    {
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        if (proof.Reason is null && proof.Key == key)
        {
            reason = string.Empty;
            return true;
        }
        reason = proof.Reason ?? "The shadow companion declaration differs from the requested variant; recook the shadow catalog.";
        return false;
    }

    private sealed class Proof
    {
        internal EngineMaterialVariantKey Key { get; }
        internal string? Reason { get; }

        internal Proof(ShaderProgramArtifact artifact)
        {
            Reason = "The shadow companion requires its canonical Slang source closure and exact physical ABI; recook the shadow catalog.";
            try
            {
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.Identity != artifact.Identity || verified.Target != ShaderCompileTarget.WebGPUWgsl ||
                    verified.SourceLanguage != "Slang" || verified.ComputeEntryPoint is not null)
                    return;
                using JsonDocument document = JsonDocument.Parse(verified.DescriptorBytes.AsMemory());
                JsonElement descriptor = document.RootElement;
                if (!descriptor.TryGetProperty("materialVariant", out JsonElement declaration)) return;
                Key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                if (!TryGetSources(Key, out EngineLitMaterialShaderSource[] sources)) return;
                if (!HasCanonicalSources(descriptor, sources)) return;
                bool textured = Key.Semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1;
                if (Key.Pass == "opaque-forward")
                {
                    if (verified.SemanticSchemaIdentity != (textured ? "xrengine.engine.lit-texture.v1" : "xrengine.engine.raster.v1") ||
                        verified.VertexEntryPoint != "standardLitVertex" || verified.FragmentEntryPoint != "standardLitFragment" ||
                        !EngineAuthoredLitMaterialAdmission.HasPhysicalPbrAbi(verified, textured,
                            Key.VertexProfile == "position-normal-tangent-uv-v1", coverage: false, directionalShadows: true,
                            localShadows: Key.OutputProfile == "linear-hdr-local-shadows-v1")) return;
                }
                else if (!HasCasterAbi(verified, Key)) return;
                Reason = null;
            }
            catch (InvalidDataException) { }
            catch (JsonException) { }
        }
    }

    private static bool HasCanonicalSources(JsonElement descriptor, EngineLitMaterialShaderSource[] sources)
    {
        if (!descriptor.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array || defines.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Array || includes.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("specialization", out JsonElement specialization) || specialization.ValueKind != JsonValueKind.Object || specialization.EnumerateObject().Any() ||
            !descriptor.TryGetProperty("sourceMap", out JsonElement sourceMap) ||
            !sourceMap.TryGetProperty("path", out JsonElement sourcePath) || sourcePath.ValueKind != JsonValueKind.String ||
            !MatchesPath(sourcePath.GetString()!, sources[0].Path) ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array)
            return false;
        uint seen = 0;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !dependency.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String)
                return false;
            string path = pathValue.GetString()!;
            for (int index = 0; index < sources.Length; index++)
            {
                if (!MatchesPath(path, sources[index].Path)) continue;
                if ((seen & (1u << index)) != 0 || hashValue.GetString() != sources[index].Sha256) return false;
                seen |= 1u << index;
            }
        }
        // Ordinary Slang cooks conservatively watch unrelated staged files too.
        return seen == (1u << sources.Length) - 1u;
    }

    private static bool MatchesPath(string path, string file)
        => path == file || path.EndsWith("/" + file, StringComparison.Ordinal);

    private static bool TryGetSources(EngineMaterialVariantKey key, out EngineLitMaterialShaderSource[] sources)
    {
        sources = [];
        if (key.Target != ShaderCompileTarget.WebGPUWgsl) return false;
        EngineLitMaterialShaderSource directional = new("StandardLitColorDirectionalShadow.slang", "eb50a3041d0ebfc17f8ed75d3e15b3196926df32d5cf5d4bf0e81d040c9b07fe");
        EngineLitMaterialShaderSource sampling = new("StandardLitTextureSampling.slang", "dfae127a0b03b8022d5fa269c62728e9de12bdf60a50eacce3584c9fec3fb24c");
        EngineLitMaterialShaderSource localSampling = new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc");
        bool local = key.OutputProfile == "linear-hdr-local-shadows-v1";
        if (key.Pass == "opaque-forward" && (local || key.OutputProfile == "linear-hdr-directional-shadow-v1"))
        {
            if (key.Semantic == EngineMaterialSemanticIdentity.StandardLitColorV1 && key.VertexProfile == "static-position-normal-v1")
                sources = local ? [new("StandardLitColorLocalShadows.slang", "07a11ad4fd6d3f762239038d97a50ca610688a61c7c88621c0a2e8bbc24c1052"), directional, localSampling] : [directional];
            else if (key.Semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1 && key.VertexProfile == "position-normal-uv-v1")
                sources = local ? [new("StandardLitTextureLocalShadows.slang", "8c6ac74d7fa38606d0bb071edacbb21d5d45f6bdd8148f175232f844bdb078f4"), directional, sampling, localSampling]
                    : [new("StandardLitTextureDirectionalShadow.slang", "5c1d232a2a053f980763c35478428814f707c1fa7b9866b0dccbf85bfc74df5b"), directional, sampling];
            else if (key.Semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1 && key.VertexProfile == "position-normal-tangent-uv-v1")
                sources = local ? [new("StandardLitTextureNormalLocalShadows.slang", "4e00ceb4080f8ae1415f8ca76ea6024e0862924d75c72cc4d9eb47082e447c1a"), directional, sampling, localSampling]
                    : [new("StandardLitTextureNormalDirectionalShadow.slang", "164151aec8f024ed6ea13b4c603071450c5130f91ec8a1d54cdc2bc19e7bedfb"), directional, sampling];
        }
        else if (key.VertexProfile == "static-position-v1")
        {
            if (key.Semantic == EngineMaterialSemanticIdentity.OpaqueShadowDepthV1 && key.Pass == "depth" && key.OutputProfile == "depth-normal-v1")
                sources = [new("Depth.vert.slang", "0a2917f0dbd836de1689df13e295bae48cf4ca6649d2042ecf5dd4ad06fba2f6")];
            else if (key.Semantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1 && key.Pass == "point-shadow-depth" && key.OutputProfile == "radial-r16f-v1")
                sources = [new("PointShadowDepth.slang", "09e1624675b2940c2ae2d42ece0b5b500e4d0e474caaf9094f8b5c0db94ec8d9")];
            else if (key.Semantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1 && key.Pass == "spot-shadow-depth" && key.OutputProfile == "projected-r16f-v1")
                sources = [new("SpotShadowDepth.slang", "a242df865c257f3ffcaf9d0f8b96bd1acf074de9477f843221a25e4d1d86fbeb")];
        }
        return sources.Length != 0;
    }

    private static bool HasCasterAbi(ShaderProgramArtifact artifact, EngineMaterialVariantKey key)
    {
        bool point = key.Semantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1;
        bool spot = key.Semantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1;
        if (artifact.SemanticSchemaIdentity != "xrengine.engine.raster.v1" ||
            artifact.VertexEntryPoint != (point ? "pointShadowDepthVertex" : spot ? "spotShadowDepthVertex" : "depthVertex") ||
            artifact.FragmentEntryPoint != (point ? "pointShadowDepthFragment" : spot ? "spotShadowDepthFragment" : null) ||
            artifact.VertexBuffers.Length != 1 || artifact.VertexBuffers[0] is not { Slot: 0, Stride: 12, StepMode: "vertex" } vertex ||
            vertex.Attributes.Length != 1 || vertex.Attributes[0] is not { Location: 0, Offset: 0, Format: "float32x3", Semantic: "position" } ||
            artifact.Resources.Length != (point ? 3 : 2)) return false;
        return Matrix(artifact.Resources[0], "View", "ViewProjection", 0, ShaderAbiFrequency.View) &&
            Matrix(artifact.Resources[1], "Object", "ModelMatrix", 1, ShaderAbiFrequency.Object) &&
            (!point || PointLight(artifact.Resources[2]));
    }

    private static bool Matrix(ShaderStageResourceLayout resource, string name, string provider, uint binding, ShaderAbiFrequency frequency)
        => Uniform(resource, name, binding, frequency, ShaderStageVisibility.Vertex, 64) && resource.Contract.Members.Length == 1 &&
            resource.Contract.Members[0] is { Offset: 0, Size: 64, PhysicalType: "mat4x4<f32>", ArrayCount: 0, ArrayStride: 0,
                MatrixOrder: ShaderAbiMatrixOrder.ColumnMajor, MatrixStride: 16 } member && member.ProviderName == provider;

    private static bool PointLight(ShaderStageResourceLayout resource)
        => Uniform(resource, "PointShadowLight", 2, ShaderAbiFrequency.View, ShaderStageVisibility.Fragment, 16) &&
            resource.Contract.Members.Length == 2 &&
            resource.Contract.Members[0] is { ProviderName: "LightPos", Offset: 0, Size: 12, PhysicalType: "vec3<f32>", ArrayCount: 0,
                ArrayStride: 0, MatrixOrder: ShaderAbiMatrixOrder.None, MatrixStride: 0 } &&
            resource.Contract.Members[1] is { ProviderName: "FarPlaneDist", Offset: 12, Size: 4, PhysicalType: "f32", ArrayCount: 0,
                ArrayStride: 0, MatrixOrder: ShaderAbiMatrixOrder.None, MatrixStride: 0 };

    private static bool Uniform(ShaderStageResourceLayout resource, string name, uint binding, ShaderAbiFrequency frequency,
        ShaderStageVisibility visibility, uint bytes)
        => resource.Contract.Name == name && resource.Contract.Set == 0 && resource.Contract.Binding == binding &&
            resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer && resource.Contract.Owner == ShaderAbiResourceOwner.Engine &&
            resource.Contract.Frequency == frequency && resource.Contract.ByteSize == bytes && resource.BindingType == "uniform" &&
            resource.Visibility == visibility && resource.DynamicOffset && !resource.RuntimeArray;
}
