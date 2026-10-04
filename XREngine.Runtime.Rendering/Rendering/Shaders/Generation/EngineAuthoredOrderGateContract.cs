using System.Runtime.CompilerServices;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Proves final-position rank gating against the complete canonical source and physical descriptor of a color program.</summary>
public static class EngineAuthoredOrderGateContract
{
    public const string Schema = "xrengine.engine.authored-order-gate.v1";
    public const string VertexProfile = "static-position-normal-order-gate-v1";
    public const string SourceRanksName = "AuthoredSourceRanks";
    public const string UniformName = "AuthoredOrderGate";
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, Proof> Proofs = new();
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, object> UberGateProofs = new();

    /// <summary>Selects an explicit alternate vertex profile only after proving the original source.</summary>
    public static bool TryGetKey(ShaderProgramArtifact source, out EngineMaterialVariantKey key, out string reason)
    {
        if (TryGetUnlitKey(source, out key, out reason)) return true;
        Proof proof = Proofs.GetValue(source, static artifact => new Proof(artifact));
        key = proof.Key;
        reason = proof.Reason ?? (proof.IsGate ? "An ordering companion cannot be used as the original source program." : string.Empty);
        return proof.Reason is null && !proof.IsGate;
    }

    /// <summary>Identifies a verified gate module before retaining its live GPU-produced rank resource.</summary>
    public static bool IsGateProgram(ShaderProgramArtifact artifact)
    {
        if (artifact.SemanticSchemaIdentity == EngineUnlitMaterialShaderGenerator.OrderGateSchema)
            return TryGetUnlitGateKey(artifact, out _, out _);
        if (UberGateProofs.TryGetValue(artifact, out _)) return true;
        Proof proof = Proofs.GetValue(artifact, static value => new Proof(value));
        return proof.Reason is null && proof.IsGate;
    }

    /// <summary>Links an exact original descriptor to a gate with identical input resources and unchanged fragment entry.</summary>
    public static bool TryValidate(ShaderProgramArtifact source, ShaderProgramArtifact gate,
        out EngineAuthoredOrderGateBinding binding, out string reason)
    {
        if (source.SemanticSchemaIdentity.StartsWith("xrengine.engine.unlit-", StringComparison.Ordinal))
            return TryValidateUnlit(source, gate, out binding, out reason);
        binding = default;
        Proof original = Proofs.GetValue(source, static artifact => new Proof(artifact));
        Proof alternate = Proofs.GetValue(gate, static artifact => new Proof(artifact));
        reason = original.Reason ?? alternate.Reason ??
            "The ordered companion does not retain the exact source vertex, fragment, and resource descriptor; recook both programs.";
        if (original.Reason is not null || alternate.Reason is not null || original.IsGate || !alternate.IsGate ||
            original.Key != alternate.Key || source.Coordinates != gate.Coordinates ||
            source.FragmentEntryPoint != gate.FragmentEntryPoint || source.VertexEntryPoint != gate.VertexEntryPoint ||
            !SameVertexInputs(source, gate) || gate.Resources.Length != source.Resources.Length + 2)
            return false;
        foreach (ShaderStageResourceLayout resource in source.Resources)
        {
            if (resource.Contract.Set == 0 && resource.Contract.Binding is 2 or 3)
                return false;
            ShaderStageResourceLayout? match = null;
            foreach (ShaderStageResourceLayout candidate in gate.Resources)
                if (candidate.Contract.Set == resource.Contract.Set && candidate.Contract.Binding == resource.Contract.Binding)
                    match = candidate;
            if (match is null || !SameResource(resource, match)) return false;
        }
        foreach ((string name, int limit) in source.RequiredLimits)
            if (!gate.RequiredLimits.TryGetValue(name, out int gateLimit) || gateLimit < limit) return false;
        binding = new(source.Identity, gate.Identity, original.Semantic, alternate.Key);
        reason = string.Empty;
        return true;
    }

    private static bool TryGetUnlitKey(ShaderProgramArtifact source, out EngineMaterialVariantKey key, out string reason)
    {
        key = default;
        reason = "The source is not a verified canonical unlit receiver.";
        if (!EngineUnlitShaderProvenance.TryValidate(source, out reason)) return false;
        EngineMaterialSemanticIdentity semantic = UnlitSemantic(source.SemanticSchemaIdentity);
        if (!semantic.IsUnlit()) return false;
        key = EngineUnlitMaterialShaderGenerator.CompanionKey(semantic, EngineUnlitMaterialShaderGenerator.Pass, orderGate: true);
        reason = string.Empty;
        return true;
    }

    private static bool TryGetUnlitGateKey(ShaderProgramArtifact gate, out EngineMaterialVariantKey key, out string reason)
    {
        key = default;
        reason = "The selected unlit ordering program has no exact declared semantic and pass.";
        if (gate.SemanticSchemaIdentity != EngineUnlitMaterialShaderGenerator.OrderGateSchema ||
            gate.DescriptorBytes.IsDefaultOrEmpty) return false;
        using JsonDocument descriptor = JsonDocument.Parse(gate.DescriptorBytes.AsMemory());
        if (!descriptor.RootElement.TryGetProperty("materialVariant", out JsonElement declaration)) return false;
        key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, gate.Pass, gate.Target);
        return key.Semantic.IsUnlit() && key == EngineUnlitMaterialShaderGenerator.CompanionKey(
            key.Semantic, EngineUnlitMaterialShaderGenerator.Pass, orderGate: true) &&
            EngineUnlitShaderProvenance.TryValidateCompanion(gate, key, out reason);
    }

    private static bool TryValidateUnlit(ShaderProgramArtifact source, ShaderProgramArtifact gate,
        out EngineAuthoredOrderGateBinding binding, out string reason)
    {
        binding = default;
        if (!TryGetUnlitKey(source, out EngineMaterialVariantKey sourceKey, out reason) ||
            !TryGetUnlitGateKey(gate, out EngineMaterialVariantKey gateKey, out reason) || sourceKey != gateKey ||
            source.Coordinates != gate.Coordinates || source.VertexEntryPoint != gate.VertexEntryPoint ||
            source.FragmentEntryPoint != gate.FragmentEntryPoint || !SameVertexInputs(source, gate) ||
            gate.Resources.Length != source.Resources.Length + 2)
            return false;
        bool ranks = false, controls = false;
        foreach (ShaderStageResourceLayout resource in gate.Resources)
        {
            if (resource.Contract.Set == 0 && resource.Contract.Binding == 2)
            { if (!IsRankResource(resource)) return false; ranks = true; }
            else if (resource.Contract.Set == 0 && resource.Contract.Binding == 3)
            { if (!IsGateUniform(resource)) return false; controls = true; }
            else
            {
                ShaderStageResourceLayout? original = null;
                foreach (ShaderStageResourceLayout candidate in source.Resources)
                    if (candidate.Contract.Set == resource.Contract.Set && candidate.Contract.Binding == resource.Contract.Binding)
                    { original = candidate; break; }
                if (original is null || !SameResource(original, resource)) return false;
            }
        }
        if (!ranks || !controls) return false;
        binding = new(source.Identity, gate.Identity, sourceKey.Semantic, gateKey);
        reason = string.Empty;
        return true;
    }

    private static EngineMaterialSemanticIdentity UnlitSemantic(string schema) => schema switch
    {
        EngineUnlitMaterialShaderGenerator.ColorSchema => EngineMaterialSemanticIdentity.UnlitColorV1,
        EngineUnlitMaterialShaderGenerator.TextureSchema => EngineMaterialSemanticIdentity.UnlitTextureV2,
        EngineUnlitMaterialShaderGenerator.OpaqueTextureSchema => EngineMaterialSemanticIdentity.UnlitOpaqueTextureV3,
        EngineUnlitMaterialShaderGenerator.AlphaTextureSchema => EngineMaterialSemanticIdentity.UnlitAlphaTextureV4,
        EngineUnlitMaterialShaderGenerator.TextureArraySliceSchema => EngineMaterialSemanticIdentity.UnlitTextureArraySliceV5,
        _ => default,
    };

    /// <summary>Registers an exact per-material Uber pair after validating its prepared literals, complete source closure, and physical layouts.</summary>
    public static bool TryValidateUberBase(Guid materialId, UberBaseMaterialProfile profile,
        ShaderProgramArtifact source, ShaderProgramArtifact gate, out EngineAuthoredOrderGateBinding binding, out string reason)
    {
        binding = default;
        reason = "The Uber ordering companion does not match the exact prepared source and final-position gate.";
        if (source.Identity != profile.CookedArtifactIdentity ||
            !EngineUberBaseMaterialAdmission.TryValidateArtifact(materialId, profile, source, EngineUberBaseShaderContract.Pass, out _) ||
            !EngineUberBaseMaterialAdmission.TryValidateArtifact(materialId, profile, gate, EngineUberBaseShaderContract.OrderPass, out _)) return false;
        ShaderProgramArtifact verifiedSource = ShaderProgramArtifactReader.Read(source.DescriptorBytes.AsSpan(), source.Artifact.Bytes);
        ShaderProgramArtifact verifiedGate = ShaderProgramArtifactReader.Read(gate.DescriptorBytes.AsSpan(), gate.Artifact.Bytes);
        if (!SameDescriptor(source, verifiedSource) || !SameDescriptor(gate, verifiedGate) ||
            source.Identity != verifiedSource.Identity || gate.Identity != verifiedGate.Identity || !SameVertexInputs(source, gate) ||
            gate.Resources.Length != source.Resources.Length + 2) return false;
        foreach (ShaderStageResourceLayout resource in source.Resources)
        {
            ShaderStageResourceLayout? match = null;
            foreach (ShaderStageResourceLayout candidate in gate.Resources)
                if (candidate.Contract.Set == resource.Contract.Set && candidate.Contract.Binding == resource.Contract.Binding) match = candidate;
            if (match is null || !SameResource(resource, match)) return false;
        }
        UberGateProofs.GetValue(gate, static _ => new object());
        binding = new(source.Identity, gate.Identity, EngineMaterialSemanticIdentity.UberBaseV1,
            new(EngineMaterialSemanticIdentity.UberBaseV1, ShaderCompileTarget.WebGPUWgsl, EngineUberBaseShaderContract.OrderPass,
                EngineUberBaseShaderContract.VertexProfile, "canonical-uber-final-position-v1"));
        reason = string.Empty;
        return true;
    }

    /// <summary>The only rank storage shape admitted for a proven alternate program.</summary>
    public static bool IsRankResource(ShaderStageResourceLayout resource)
        => resource.Contract is { Name: SourceRanksName, PhysicalName: "authoredSourceRanks_0", Set: 0, Binding: 2,
                Kind: ShaderAbiResourceKind.StorageBuffer, Owner: ShaderAbiResourceOwner.Engine,
                Frequency: ShaderAbiFrequency.Frame, ByteSize: 4, DescriptorLifetime: null } &&
            resource.Contract.Members.IsEmpty && resource.Visibility == ShaderStageVisibility.Vertex &&
            resource.BindingType == "read-only-storage" && !resource.DynamicOffset && resource.RuntimeArray;

    private sealed class Proof
    {
        internal bool IsGate { get; }
        internal EngineMaterialVariantKey Key { get; }
        internal EngineMaterialSemanticIdentity Semantic { get; }
        internal string? Reason { get; }

        internal Proof(ShaderProgramArtifact artifact)
        {
            Reason = "Ordered direct replay requires a verified canonical authored coverage source and exact final-position companion; recook the shader catalog.";
            try
            {
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.SemanticSchemaIdentity is EngineOctahedralImpostorShaderContract.Schema or EngineOctahedralImpostorShaderContract.OrderGateSchema)
                {
                    if (!SameDescriptor(artifact, verified) || verified.Identity != artifact.Identity) return;
                    IsGate = verified.SemanticSchemaIdentity == EngineOctahedralImpostorShaderContract.OrderGateSchema;
                    Semantic = EngineMaterialSemanticIdentity.OctahedralImpostorV1;
                    Key = EngineOctahedralImpostorShaderContract.Key(orderGate: true);
                    if (!EngineOctahedralImpostorShaderProvenance.TryValidate(verified, IsGate, out _)) return;
                    Reason = null;
                    return;
                }
                if (verified.SemanticSchemaIdentity is EngineAuthoredTexturedShaderGenerator.Schema or EngineAuthoredTexturedShaderGenerator.OrderGateSchema)
                {
                    if (!SameDescriptor(artifact, verified) || verified.Identity != artifact.Identity) return;
                    IsGate = verified.SemanticSchemaIdentity == EngineAuthoredTexturedShaderGenerator.OrderGateSchema;
                    Semantic = EngineMaterialSemanticIdentity.AuthoredLitTexturedV1;
                    if (IsGate)
                    {
                        using JsonDocument gateDocument = JsonDocument.Parse(verified.DescriptorBytes.AsMemory());
                        if (!gateDocument.RootElement.TryGetProperty("materialVariant", out JsonElement declaration)) return;
                        Key = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                        if (!EngineAuthoredTexturedShaderGenerator.TryGetCompanionTextureFlags(Key, out _) ||
                            !EngineAuthoredTexturedShaderProvenance.TryValidateCompanion(verified, Key, out _)) return;
                    }
                    else
                    {
                        if (!EngineAuthoredTexturedShaderProvenance.TryValidate(verified, out int flags, out _)) return;
                        Key = EngineAuthoredTexturedShaderGenerator.CompanionKey(flags, EngineAuthoredTexturedShaderGenerator.Pass, orderGate: true);
                    }
                    Reason = null;
                    return;
                }
                if (verified.SemanticSchemaIdentity is EngineTexturedAlphaShaderGenerator.Schema or EngineTexturedAlphaShaderGenerator.OrderGateSchema)
                {
                    if (!SameDescriptor(artifact, verified) || verified.Identity != artifact.Identity) return;
                    IsGate = verified.SemanticSchemaIdentity == EngineTexturedAlphaShaderGenerator.OrderGateSchema;
                    Semantic = EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1;
                    Key = new(Semantic, ShaderCompileTarget.WebGPUWgsl, EngineTexturedAlphaShaderGenerator.Pass,
                        EngineTexturedAlphaShaderGenerator.OrderGateVertexProfile, "linear-hdr-local-shadows-v1");
                    if (IsGate ? !EngineTexturedAlphaShaderProvenance.TryValidateCompanion(verified, Key, out _) :
                        !EngineTexturedAlphaShaderProvenance.TryValidate(verified, out _)) return;
                    Reason = null;
                    return;
                }
                if (verified.Identity != artifact.Identity || !SameDescriptor(artifact, verified) || verified.Target != ShaderCompileTarget.WebGPUWgsl ||
                    verified.ComputeEntryPoint is not null || verified.NativeVertexCompanion is not null || verified.Pass != "forward-coverage" ||
                    verified.VertexEntryPoint != "standardLitVertex" || verified.FragmentEntryPoint != "standardLitFragment") return;
                using JsonDocument document = JsonDocument.Parse(verified.DescriptorBytes.AsMemory());
                JsonElement descriptor = document.RootElement;
                IsGate = verified.SemanticSchemaIdentity == Schema;
                bool authored = verified.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.ColorCoverageSchema;
                Semantic = authored ? EngineMaterialSemanticIdentity.AuthoredLitV2 : EngineMaterialSemanticIdentity.StandardLitColorV2;
                if (authored)
                {
                    if (verified.SourceLanguage != "MaterialRecipe" ||
                        !EngineLitMaterialShaderProvenance.TryValidate(verified, out _) ||
                        !EngineAuthoredLitMaterialAdmission.HasPhysicalPbrAbi(verified, false, false, true)) return;
                    Key = new(EngineMaterialSemanticIdentity.StandardLitColorV2, ShaderCompileTarget.WebGPUWgsl,
                        "forward-coverage", VertexProfile, "linear-hdr-local-shadows-v1");
                }
                else
                {
                    if (verified.SourceLanguage != "Slang" ||
                        !IsGate && verified.SemanticSchemaIdentity != "xrengine.engine.lit-color-coverage.v2" ||
                        !descriptor.TryGetProperty("materialVariant", out JsonElement declaration)) return;
                    EngineMaterialVariantKey declared = ShaderProgramArtifactReader.ReadMaterialVariantKey(declaration, verified.Pass, verified.Target);
                    if (declared.Semantic != EngineMaterialSemanticIdentity.StandardLitColorV2 ||
                        declared.VertexProfile != (IsGate ? VertexProfile : "static-position-normal-v1") ||
                        declared.OutputProfile is not ("linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1")) return;
                    Key = declared with { VertexProfile = VertexProfile };
                    if (!HasCanonicalSources(descriptor, Key.OutputProfile, IsGate)) return;
                }
                int baseResources = Key.OutputProfile switch
                {
                    "linear-hdr-local-shadows-v1" => 14,
                    "linear-hdr-directional-shadow-v1" => 9,
                    _ => 6,
                };
                if (verified.Resources.Length != baseResources + (IsGate ? 2 : 0)) return;
                bool ranks = false, parameters = false;
                foreach (ShaderStageResourceLayout resource in verified.Resources)
                {
                    if (resource.Contract.Set == 0 && resource.Contract.Binding is 2 or 3)
                    {
                        if (!IsGate) return;
                        if (IsRankResource(resource)) ranks = true;
                        else if (IsGateUniform(resource)) parameters = true;
                        else return;
                    }
                    else if (resource.Contract.Kind is ShaderAbiResourceKind.StorageBuffer or ShaderAbiResourceKind.StorageImage)
                        return;
                }
                if (IsGate && (!ranks || !parameters || !HasGateLimits(verified))) return;
                Reason = null;
            }
            catch (InvalidDataException) { }
            catch (JsonException) { }
            catch (ArgumentException) { }
        }
    }

    private static bool HasGateLimits(ShaderProgramArtifact artifact)
        => artifact.RequiredLimits.TryGetValue("maxBindingsPerBindGroup", out int bindings) && bindings >= 4 &&
            artifact.RequiredLimits.TryGetValue("maxStorageBuffersPerShaderStage", out int storage) && storage >= 1 &&
            artifact.RequiredLimits.TryGetValue("maxStorageBufferBindingSize", out int storageBytes) && storageBytes >= 4 &&
            artifact.RequiredLimits.TryGetValue("maxUniformBuffersPerShaderStage", out int uniforms) && uniforms >= 3 &&
            artifact.RequiredLimits.TryGetValue("maxDynamicUniformBuffersPerPipelineLayout", out int dynamic) &&
            dynamic >= (artifact.Resources.Length == 16 ? 7 : artifact.Resources.Length == 11 ? 6 : 5);

    private static bool IsGateUniform(ShaderStageResourceLayout resource)
    {
        if (resource.Contract is not { Name: UniformName, PhysicalName: "authoredOrderGate_0", Set: 0, Binding: 3,
                Kind: ShaderAbiResourceKind.UniformBuffer, Owner: ShaderAbiResourceOwner.Engine,
                Frequency: ShaderAbiFrequency.Object, ByteSize: 16, DescriptorLifetime: null } ||
            resource.Visibility != ShaderStageVisibility.Vertex || resource.BindingType != "uniform" ||
            !resource.DynamicOffset || resource.RuntimeArray || resource.Contract.Members.Length != 4) return false;
        for (int index = 0; index < 4; index++)
        {
            ShaderAbiMemberContract member = resource.Contract.Members[index];
            string provider = index switch { 0 => "AuthoredSourceIndex", 1 => "AuthoredActiveRank", 2 => "AuthoredSourceCount", _ => "AuthoredOrderEnabled" };
            string physical = index switch { 0 => "sourceIndex_0", 1 => "activeRank_0", 2 => "sourceCount_0", _ => "enabled_0" };
            if (member.ProviderName != provider || member.PhysicalName != physical || member.Offset != 4 * index ||
                member is not { Size: 4, PhysicalType: "u32", ArrayCount: 0, ArrayStride: 0,
                    MatrixOrder: ShaderAbiMatrixOrder.None, MatrixStride: 0, CpuFieldName: null }) return false;
        }
        return true;
    }

    private static bool SameVertexInputs(ShaderProgramArtifact source, ShaderProgramArtifact gate)
    {
        if (source.VertexBuffers.Length != gate.VertexBuffers.Length) return false;
        for (int index = 0; index < source.VertexBuffers.Length; index++)
        {
            ShaderVertexBufferLayout original = source.VertexBuffers[index], alternate = gate.VertexBuffers[index];
            if (original.Slot != alternate.Slot || original.Stride != alternate.Stride || original.StepMode != alternate.StepMode ||
                !original.Attributes.AsSpan().SequenceEqual(alternate.Attributes.AsSpan())) return false;
        }
        return true;
    }

    private static bool SameDescriptor(ShaderProgramArtifact artifact, ShaderProgramArtifact verified)
    {
        if (artifact.Name != verified.Name || artifact.Pass != verified.Pass || artifact.SourcePath != verified.SourcePath ||
            artifact.SourceLanguage != verified.SourceLanguage || artifact.SemanticSchemaIdentity != verified.SemanticSchemaIdentity ||
            artifact.Coordinates != verified.Coordinates || artifact.VertexEntryPoint != verified.VertexEntryPoint ||
            artifact.FragmentEntryPoint != verified.FragmentEntryPoint || artifact.ComputeEntryPoint != verified.ComputeEntryPoint ||
            artifact.ComputeWorkgroupSize != verified.ComputeWorkgroupSize || artifact.NativeVertexCompanion != verified.NativeVertexCompanion ||
            artifact.Target != verified.Target ||
            !SameVertexInputs(artifact, verified) || artifact.Resources.Length != verified.Resources.Length ||
            artifact.RequiredLimits.Count != verified.RequiredLimits.Count) return false;
        for (int index = 0; index < artifact.Resources.Length; index++)
            if (!SameResource(artifact.Resources[index], verified.Resources[index])) return false;
        foreach ((string name, int limit) in artifact.RequiredLimits)
            if (!verified.RequiredLimits.TryGetValue(name, out int original) || original != limit) return false;
        return true;
    }

    private static bool SameResource(ShaderStageResourceLayout source, ShaderStageResourceLayout gate)
    {
        ShaderAbiResourceContract original = source.Contract, alternate = gate.Contract;
        return source.Visibility == gate.Visibility && source.BindingType == gate.BindingType &&
            source.DynamicOffset == gate.DynamicOffset && source.RuntimeArray == gate.RuntimeArray &&
            original.Name == alternate.Name && original.PhysicalName == alternate.PhysicalName &&
            original.Set == alternate.Set && original.Binding == alternate.Binding && original.Kind == alternate.Kind &&
            original.Owner == alternate.Owner && original.Frequency == alternate.Frequency && original.ByteSize == alternate.ByteSize &&
            original.DescriptorLifetime == alternate.DescriptorLifetime && original.Members.AsSpan().SequenceEqual(alternate.Members.AsSpan());
    }

    private static bool HasCanonicalSources(JsonElement descriptor, string outputProfile, bool gate)
    {
        if (!descriptor.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array || defines.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Array || includes.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("specialization", out JsonElement specialization) || specialization.ValueKind != JsonValueKind.Object || specialization.EnumerateObject().Any() ||
            !descriptor.TryGetProperty("sourceMap", out JsonElement sourceMap) ||
            !sourceMap.TryGetProperty("path", out JsonElement sourcePath) || sourcePath.ValueKind != JsonValueKind.String ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array)
            return false;
        EngineLitMaterialShaderSource[] sources = Sources(outputProfile, gate);
        if (!MatchesPath(sourcePath.GetString()!, sources[0].Path)) return false;
        uint seen = 0;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !dependency.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String) return false;
            for (int index = 0; index < sources.Length; index++)
            {
                if (!MatchesPath(pathValue.GetString()!, sources[index].Path)) continue;
                if ((seen & (1u << index)) != 0 || hashValue.GetString() != sources[index].Sha256) return false;
                seen |= 1u << index;
            }
        }
        // The production Slang compiler also watches unrelated staged inputs.
        return seen == (1u << sources.Length) - 1u;
    }

    private static bool MatchesPath(string path, string file)
        => path == file || path.EndsWith("/" + file, StringComparison.Ordinal);

    private static EngineLitMaterialShaderSource[] Sources(string outputProfile, bool gate)
    {
        EngineLitMaterialShaderSource directional = new("StandardLitColorDirectionalShadow.slang", "eb50a3041d0ebfc17f8ed75d3e15b3196926df32d5cf5d4bf0e81d040c9b07fe");
        EngineLitMaterialShaderSource[] originals = outputProfile switch
        {
            "linear-hdr-local-shadows-v1" => [
                new("StandardLitColorCoverageLocalShadows.slang", "362f3a0573e8f05beb241e8855de37b31500455100c58e456babee9e358024ed"),
                new("StandardLitColorLocalShadows.slang", "07a11ad4fd6d3f762239038d97a50ca610688a61c7c88621c0a2e8bbc24c1052"), directional,
                new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc")],
            "linear-hdr-directional-shadow-v1" => [
                new("StandardLitColorCoverageDirectionalShadow.slang", "2f46a02b20859e7f7ecfb28b6f1a3c91b9984031fe867ef596b08dfa6566cb09"), directional],
            _ => [new("StandardLitColorCoverage.slang", "3c65556f5d64eef7a902151d9c41ec3eae24069425e848728343a15824475816"),
                new("StandardLitColor.slang", "50074cecbde9666073ff50275511151c5b01096e155f92115b5ef4c591a59e9c")],
        };
        if (!gate) return originals;
        EngineLitMaterialShaderSource wrapper = outputProfile switch
        {
            "linear-hdr-local-shadows-v1" => new("StandardLitColorCoverageLocalShadowsAuthoredOrder.slang", "9ead07557436cc8a09f1220a2241b29663c21e49201c3e7c25991af981201697"),
            "linear-hdr-directional-shadow-v1" => new("StandardLitColorCoverageDirectionalShadowAuthoredOrder.slang", "4af1b20733064f007401fae1c9ccd24453f8fbfabfc12e860588c433375cdced"),
            _ => new("StandardLitColorCoverageAuthoredOrder.slang", "c737f8216228f0f22b3518a15821a79d0fe0cc63e16fd948c61732a4015c38a9"),
        };
        return [wrapper, new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"), .. originals];
    }
}
