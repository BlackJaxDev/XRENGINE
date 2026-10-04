using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Validates the shared authored vertex function and the engine's paired raster and compute wrappers.</summary>
public static class EngineNativeVertexShaderProvenance
{
    private static readonly ConditionalWeakTable<ShaderProgramArtifact, Proof> Proofs = new();

    public static bool TryValidate(ShaderProgramArtifact raster, IShaderProgramArtifactResolver resolver,
        [NotNullWhen(true)] out ShaderProgramArtifact? compute, out string reason)
    {
        compute = null;
        reason = "The native vertex material requires a cooked pair with its exact shared authored function closure.";
        if (raster.SemanticSchemaIdentity != EngineNativeVertexShaderGenerator.RasterSchema ||
            raster.Pass != "opaque-forward" || raster.VertexEntryPoint != "standardLitVertex" ||
            raster.FragmentEntryPoint != "standardLitFragment" || raster.ComputeEntryPoint is not null ||
            raster.Resources.Length != 7 || raster.NativeVertexCompanion?.ComputeIdentity is not { } identity)
            return false;
        Proof rasterProof = Proofs.GetValue(raster, static artifact => ValidateClosure(artifact));
        if (!rasterProof.Valid) { reason = rasterProof.Reason; return false; }
        if (!resolver.TryResolve(identity, raster.Target, out ShaderProgramArtifact? candidate))
        {
            reason = "The exact native vertex compute companion is missing from the current artifact catalog.";
            return false;
        }
        if (candidate.Identity != identity || candidate.Name != rasterProof.ComputeName ||
            candidate.SemanticSchemaIdentity != EngineNativeVertexShaderGenerator.ComputeSchema ||
            candidate.Pass != "native-material-vertex" || candidate.ComputeEntryPoint != "nativeMaterialVertex" ||
            candidate.VertexEntryPoint is not null || candidate.FragmentEntryPoint is not null ||
            candidate.ComputeWorkgroupSize is not { X: 64, Y: 1, Z: 1 } ||
            candidate.NativeVertexCompanion is not { ComputeIdentity: null })
        {
            reason = "The native vertex compute companion does not have the canonical packed-vertex physical ABI.";
            return false;
        }
        Proof computeProof = Proofs.GetValue(candidate, static artifact => ValidateClosure(artifact));
        if (!computeProof.Valid) { reason = computeProof.Reason; return false; }
        if (rasterProof.Function != computeProof.Function || rasterProof.Closure != computeProof.Closure ||
            rasterProof.Compiler != computeProof.Compiler || rasterProof.MaterialPath != computeProof.MaterialPath)
        {
            reason = "Raster and native vertex programs were not cooked from the same exact authored function and engine source closure.";
            return false;
        }
        compute = candidate;
        reason = string.Empty;
        return true;
    }

    /// <summary>Resolves an auxiliary only when its physical layout and complete authored source closure match the material.</summary>
    public static bool TryResolveAuxiliary(ShaderProgramArtifact raster, IShaderProgramArtifactResolver resolver,
        EngineNativeVertexAuxiliaryPass pass, [NotNullWhen(true)] out ShaderProgramArtifact? auxiliary, out string reason)
    {
        auxiliary = null;
        if (!TryValidate(raster, resolver, out _, out reason)) return false;
        string? identity = AuxiliaryIdentity(raster.NativeVertexCompanion!, pass);
        if (identity is null || !resolver.TryResolve(identity, raster.Target, out ShaderProgramArtifact? candidate))
        {
            reason = "The exact authored native vertex auxiliary is missing from the current artifact catalog; recook and export the material bundle.";
            return false;
        }
        Proof rasterProof = Proofs.GetValue(raster, static artifact => ValidateClosure(artifact));
        Proof auxiliaryProof = Proofs.GetValue(candidate, static artifact => ValidateClosure(artifact));
        if (!auxiliaryProof.Valid) { reason = auxiliaryProof.Reason; return false; }
        if (candidate.Identity != identity || candidate.Name != rasterProof.AuxiliaryNames[(int)pass] ||
            candidate.SemanticSchemaIdentity != EngineNativeVertexShaderGenerator.AuxiliarySchema(pass) ||
            !HasAuxiliaryEntries(candidate, pass) || candidate.ComputeEntryPoint is not null ||
            auxiliaryProof.LayoutHash != EngineNativeVertexShaderGenerator.AuxiliaryLayoutHash(pass) ||
            candidate.NativeVertexCompanion!.ComputeIdentity != raster.NativeVertexCompanion!.ComputeIdentity ||
            auxiliaryProof.Function != rasterProof.Function || auxiliaryProof.Closure != rasterProof.Closure ||
            auxiliaryProof.Compiler != rasterProof.Compiler || auxiliaryProof.MaterialPath != rasterProof.MaterialPath)
        {
            reason = "The native vertex auxiliary does not preserve the exact authored function, canonical physical layout, or material source closure.";
            return false;
        }
        auxiliary = candidate;
        reason = string.Empty;
        return true;
    }

    public static string? AuxiliaryIdentity(ShaderNativeVertexCompanion native, EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => native.DepthNormalIdentity,
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => native.DirectionalShadowIdentity,
        EngineNativeVertexAuxiliaryPass.PointShadow => native.PointShadowIdentity,
        EngineNativeVertexAuxiliaryPass.SpotShadow => native.SpotShadowIdentity,
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => native.DirectionalReceiverIdentity,
        EngineNativeVertexAuxiliaryPass.LocalReceiver => native.LocalReceiverIdentity,
        _ => throw new ArgumentOutOfRangeException(nameof(pass)),
    };

    private static bool HasAuxiliaryEntries(ShaderProgramArtifact artifact, EngineNativeVertexAuxiliaryPass pass) => pass switch
    {
        EngineNativeVertexAuxiliaryPass.DepthNormal => artifact.Pass == "depth-normal" && artifact.VertexEntryPoint == "depthNormalVertex" && artifact.FragmentEntryPoint == "depthNormalFragment",
        EngineNativeVertexAuxiliaryPass.DirectionalShadow => artifact.Pass == "depth" && artifact.VertexEntryPoint == "depthVertex" && artifact.FragmentEntryPoint is null,
        EngineNativeVertexAuxiliaryPass.PointShadow => artifact.Pass == "point-shadow-depth" && artifact.VertexEntryPoint == "pointShadowDepthVertex" && artifact.FragmentEntryPoint == "pointShadowDepthFragment",
        EngineNativeVertexAuxiliaryPass.SpotShadow => artifact.Pass == "spot-shadow-depth" && artifact.VertexEntryPoint == "spotShadowDepthVertex" && artifact.FragmentEntryPoint == "spotShadowDepthFragment",
        EngineNativeVertexAuxiliaryPass.DirectionalReceiver => artifact.Pass == "opaque-forward" && artifact.VertexEntryPoint == "standardLitVertex" && artifact.FragmentEntryPoint == "standardLitFragment",
        EngineNativeVertexAuxiliaryPass.LocalReceiver => artifact.Pass == "opaque-forward" && artifact.VertexEntryPoint == "standardLitVertex" && artifact.FragmentEntryPoint == "standardLitFragment",
        _ => false,
    };

    private static Proof ValidateClosure(ShaderProgramArtifact artifact)
    {
        const string failure = "The native vertex descriptor lacks its complete pinned engine wrappers and exact isolated authored function closure; recook the material.";
        if (artifact.SourceLanguage != "MaterialRecipe" || artifact.DescriptorBytes.IsDefaultOrEmpty ||
            artifact.NativeVertexCompanion is not { } native || native.Profile != EngineNativeVertexShaderGenerator.Profile ||
            !EngineNativeVertexShaderGenerator.IsExactFunction(native.FunctionSource) ||
            EngineNativeVertexShaderGenerator.FunctionHash(native.FunctionSource) != native.FunctionSha256 ||
            Convert.ToHexStringLower(SHA256.HashData(artifact.DescriptorBytes.AsSpan())) != artifact.Identity)
            return new(false, failure, "", "", "", "");
        try
        {
            ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
            if (artifact.ComputeEntryPoint is not null && !HasComputeAbi(artifact))
                return new(false, failure, "", "", "", "");
            if (!HasSamePhysicalLayout(verified, artifact) || verified.NativeVertexCompanion != native || verified.SourceLanguage != artifact.SourceLanguage ||
                verified.SemanticSchemaIdentity != artifact.SemanticSchemaIdentity || verified.Name != artifact.Name ||
                verified.Pass != artifact.Pass || verified.VertexEntryPoint != artifact.VertexEntryPoint ||
                verified.FragmentEntryPoint != artifact.FragmentEntryPoint || verified.ComputeEntryPoint != artifact.ComputeEntryPoint)
                return new(false, failure, "", "", "", "");
            using JsonDocument document = JsonDocument.Parse(artifact.DescriptorBytes.AsMemory());
            JsonElement root = document.RootElement;
            if (Field(root, "defines").GetArrayLength() != 0 || Field(root, "includes").GetArrayLength() != 0)
                return new(false, failure, "", "", "", "");
            JsonElement dependencies = Field(root, "dependencies");
            string material = Field(Field(root, "sourceMap"), "path").GetString()!;
            IReadOnlyList<EngineLitMaterialShaderSource> canonical = EngineNativeVertexShaderGenerator.RequiredCanonicalSources;
            if (!material.EndsWith(".material.json", StringComparison.Ordinal) || dependencies.GetArrayLength() != canonical.Count + 3)
                return new(false, failure, "", "", "", "");
            bool materialSeen = false, recipeSeen = false, functionSeen = false;
            uint seen = 0;
            foreach (JsonElement dependency in dependencies.EnumerateArray())
            {
                string path = Field(dependency, "path").GetString()!, hash = Field(dependency, "sha256").GetString()!;
                if (path == material && !materialSeen) { materialSeen = true; continue; }
                if (path.EndsWith(".recipe.json", StringComparison.Ordinal) && !recipeSeen &&
                    !canonical.Any(source => path == source.Path || path.EndsWith("/" + source.Path, StringComparison.Ordinal))) { recipeSeen = true; continue; }
                if (path == EngineNativeVertexShaderGenerator.FunctionPath && !functionSeen && hash == native.FunctionSha256)
                { functionSeen = true; continue; }
                int index = 0;
                while (index < canonical.Count && path != canonical[index].Path &&
                    !path.EndsWith("/" + canonical[index].Path, StringComparison.Ordinal)) index++;
                if (index == canonical.Count || (seen & (1u << index)) != 0 || hash != canonical[index].Sha256)
                    return new(false, failure, "", "", "", "");
                seen |= 1u << index;
            }
            if (!materialSeen || !recipeSeen || !functionSeen || seen != (1u << canonical.Count) - 1u)
                return new(false, failure, "", "", "", "");
            return new(true, "", native.FunctionSource, dependencies.GetRawText(), Field(root, "compilerIdentity").GetString()!, material)
                {
                    ComputeName = artifact.Name + "-native-vertex",
                    LayoutHash = EngineNativeVertexShaderGenerator.FunctionHash(Field(root, "layout").GetRawText()),
                    AuxiliaryNames = Enum.GetValues<EngineNativeVertexAuxiliaryPass>().Select(pass => artifact.Name + EngineNativeVertexShaderGenerator.AuxiliarySuffix(pass)).ToArray(),
                };
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        { return new(false, failure, "", "", "", ""); }
    }

    private static bool HasSamePhysicalLayout(ShaderProgramArtifact left, ShaderProgramArtifact right)
    {
        if (left.Resources.Length != right.Resources.Length || left.VertexBuffers.Length != right.VertexBuffers.Length) return false;
        for (int index = 0; index < left.Resources.Length; index++)
        {
            ShaderStageResourceLayout a = left.Resources[index], b = right.Resources[index];
            if (a.Visibility != b.Visibility || a.BindingType != b.BindingType || a.DynamicOffset != b.DynamicOffset || a.RuntimeArray != b.RuntimeArray ||
                a.Contract with { Members = b.Contract.Members } != b.Contract || !a.Contract.Members.AsSpan().SequenceEqual(b.Contract.Members.AsSpan())) return false;
        }
        for (int index = 0; index < left.VertexBuffers.Length; index++)
        {
            ShaderVertexBufferLayout a = left.VertexBuffers[index], b = right.VertexBuffers[index];
            if (a with { Attributes = b.Attributes } != b || !a.Attributes.AsSpan().SequenceEqual(b.Attributes.AsSpan())) return false;
        }
        return true;
    }

    private static bool HasComputeAbi(ShaderProgramArtifact artifact)
    {
        if (artifact.VertexBuffers.Length != 0 || artifact.Resources.Length != 3) return false;
        ShaderStageResourceLayout source = artifact.Resources[0], output = artifact.Resources[1], parameters = artifact.Resources[2];
        if (!IsArena(source, 0, "SourceGeometry", "sourceGeometry_0", "read-only-storage") ||
            !IsArena(output, 1, "OutputGeometry", "outputGeometry_0", "storage") ||
            parameters is not { Visibility: ShaderStageVisibility.Compute, BindingType: "uniform", DynamicOffset: true, RuntimeArray: false,
                Contract: { Name: "NativeVertexParameters", PhysicalName: "nativeVertexParameters_0", Set: 0, Binding: 2,
                    Kind: ShaderAbiResourceKind.UniformBuffer, Owner: ShaderAbiResourceOwner.Engine,
                    Frequency: ShaderAbiFrequency.View, ByteSize: 160, Members.Length: 16 } }) return false;
        ReadOnlySpan<string> names = ["input0", "input1", "input2", "input3", "previousInput0", "previousInput1", "previousInput2", "previousInput3",
            "sourceCurrentStream", "sourceCurrentOffset", "sourcePreviousStream", "sourcePreviousOffset", "currentOutputOffset", "previousOutputOffset", "vertexCount", "previousValid"];
        ReadOnlySpan<string> providers = ["NativeVertexInput0", "NativeVertexInput1", "NativeVertexInput2", "NativeVertexInput3",
            "PreviousNativeVertexInput0", "PreviousNativeVertexInput1", "PreviousNativeVertexInput2", "PreviousNativeVertexInput3",
            "SourceCurrentStream", "SourceCurrentOffset", "SourcePreviousStream", "SourcePreviousOffset", "CurrentOutputOffset", "PreviousOutputOffset", "VertexCount", "PreviousValid"];
        for (int index = 0; index < names.Length; index++)
        {
            ShaderAbiMemberContract member = parameters.Contract.Members[index];
            if (member.PhysicalName != names[index] + "_0" || member.ProviderName != providers[index] ||
                member.Offset != (index < 8 ? index * 16 : 128 + (index - 8) * 4) ||
                member.Size != (index < 8 ? 16 : 4) || member.PhysicalType != (index < 8 ? "vec4<f32>" : "u32") ||
                member.MatrixOrder != ShaderAbiMatrixOrder.None || member.MatrixStride != 0) return false;
        }
        return true;
    }

    private static bool IsArena(ShaderStageResourceLayout resource, uint binding, string name, string physical, string type)
        => resource is { Visibility: ShaderStageVisibility.Compute, DynamicOffset: false, RuntimeArray: true,
            Contract: { Set: 0, Kind: ShaderAbiResourceKind.StorageBuffer, Owner: ShaderAbiResourceOwner.Engine,
                Frequency: ShaderAbiFrequency.Frame, ByteSize: 4, Members.Length: 0 } } &&
            resource.BindingType == type && resource.Contract.Binding == binding && resource.Contract.Name == name && resource.Contract.PhysicalName == physical;

    private static JsonElement Field(JsonElement value, string name)
        => value.TryGetProperty(name, out JsonElement result) ? result : throw new InvalidDataException("The native vertex descriptor is incomplete.");

    private sealed record Proof(bool Valid, string Reason, string Function, string Closure, string Compiler, string MaterialPath)
    {
        public string ComputeName { get; init; } = string.Empty;
        public string LayoutHash { get; init; } = string.Empty;
        public string[] AuxiliaryNames { get; init; } = [];
    }
}
