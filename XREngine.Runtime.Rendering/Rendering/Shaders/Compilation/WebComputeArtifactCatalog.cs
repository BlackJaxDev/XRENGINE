using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Exact engine compute kernels selected by hash-verified package metadata.</summary>
public sealed class WebComputeArtifactCatalog
{
    public const string PackedSkinningKernel = "packed-skinning";
    public const string LuminanceReductionKernel = "luminance-reduction";

    private readonly ImmutableDictionary<string, ShaderProgramArtifact> _artifacts;

    public WebComputeArtifactCatalog(IEnumerable<KeyValuePair<string, string>> entries, ShaderProgramArtifactCatalog artifacts)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(artifacts);
        ImmutableDictionary<string, ShaderProgramArtifact>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, ShaderProgramArtifact>(StringComparer.Ordinal);
        foreach ((string kernel, string descriptorIdentity) in entries)
        {
            if (!IsSupportedKernel(kernel))
                throw new InvalidDataException($"ComputeArtifact.KernelUnsupported: '{kernel}'.");
            string identity = ShaderProgramArtifactCatalog.ValidateIdentity(descriptorIdentity)
                ?? throw new InvalidDataException($"ComputeArtifact.IdentityMissing: '{kernel}'.");
            if (!artifacts.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                throw new InvalidDataException($"ComputeArtifact.Missing: '{kernel}' refers to unverified descriptor '{identity}'.");
            ValidateKernel(kernel, artifact);
            if (!builder.TryAdd(kernel, artifact))
                throw new InvalidDataException($"ComputeArtifact.DuplicateKernel: '{kernel}'.");
        }
        _artifacts = builder.ToImmutable();
    }

    public int Count => _artifacts.Count;

    public bool TryResolve(string kernel, [NotNullWhen(true)] out ShaderProgramArtifact? artifact)
        => _artifacts.TryGetValue(kernel, out artifact);

    public static bool IsSupportedKernel(string? kernel)
        => kernel is PackedSkinningKernel or LuminanceReductionKernel;

    /// <summary>Validates the exact physical ABI selected by an engine compute identity.</summary>
    public static void ValidateKernel(string kernel, ShaderProgramArtifact artifact)
    {
        if (kernel == PackedSkinningKernel) ValidatePackedSkinning(artifact);
        else if (kernel == LuminanceReductionKernel) ValidateLuminanceReduction(artifact);
        else throw new InvalidDataException($"ComputeArtifact.KernelUnsupported: '{kernel}'.");
    }

    /// <summary>Rejects a reduction module unless it matches the engine's bounded two-dispatch ABI.</summary>
    public static void ValidateLuminanceReduction(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != LuminanceReductionKernel ||
            artifact.ComputeEntryPoint != "reduce" || artifact.VertexEntryPoint is not null ||
            artifact.FragmentEntryPoint is not null ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(256, 1, 1) ||
            artifact.VertexBuffers.Length != 0 || artifact.Resources.Length != 4)
            throw new InvalidDataException("ComputeArtifact.DescriptorMismatch: luminance reduction requires the engine compute ABI.");

        using JsonDocument document = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
        JsonElement descriptor = document.RootElement;
        if (descriptor.TryGetProperty("materialVariant", out _) ||
            !descriptor.TryGetProperty("pipeline", out JsonElement pipeline) ||
            pipeline.ValueKind != JsonValueKind.Object || pipeline.EnumerateObject().Any() ||
            !descriptor.TryGetProperty("layout", out JsonElement layout) ||
            !layout.TryGetProperty("bindings", out JsonElement bindings) ||
            bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() != 4)
            throw new InvalidDataException("ComputeArtifact.DescriptorMismatch: luminance reduction cannot declare raster bindings.");

        string[] names = ["Source", "Partials", "Result", "Parameters"];
        string[] physicalNames = ["source", "partials", "result", "parameters"];
        string[] kinds = ["texture-2d-array-float", "storage", "storage", "uniform"];
        int[] bytes = [0, 8, 4, 64];
        for (int index = 0; index < names.Length; index++)
        {
            JsonElement binding = bindings[index];
            JsonElement visibility = Required(binding, "visibility", JsonValueKind.Array);
            if (Required(binding, "name", JsonValueKind.String).GetString() != names[index] ||
                Required(binding, "physicalName", JsonValueKind.String).GetString() != physicalNames[index] ||
                RequiredInt(binding, "group") != 0 || RequiredInt(binding, "binding") != index ||
                Required(binding, "kind", JsonValueKind.String).GetString() != kinds[index] ||
                Required(binding, "owner", JsonValueKind.String).GetString() != "Engine" ||
                Required(binding, "frequency", JsonValueKind.String).GetString() != "Object" ||
                RequiredInt(binding, "bytes") != bytes[index] || RequiredBoolean(binding, "dynamic") ||
                (binding.TryGetProperty("runtimeArray", out JsonElement runtimeArray) && runtimeArray.ValueKind == JsonValueKind.True) != (index is 1 or 2) ||
                visibility.GetArrayLength() != 1 || visibility[0].ValueKind != JsonValueKind.String ||
                visibility[0].GetString() != "compute" ||
                Required(binding, "members", JsonValueKind.Array).GetArrayLength() != (index == 3 ? 9 : 0))
                throw new InvalidDataException($"ComputeArtifact.BindingMismatch: luminance binding {index}.");
        }
        JsonElement members = Required(bindings[3], "members", JsonValueKind.Array);
        string[] memberNames = ["origin", "extent", "mip", "layers", "tilesX", "tilesY", "tileCount", "mode", "weights"];
        string[] providers = ["Origin", "Extent", "Mip", "Layers", "TilesX", "TilesY", "TileCount", "Mode", "Weights"];
        string[] types = ["vec2<u32>", "vec2<u32>", "u32", "u32", "u32", "u32", "u32", "u32", "vec4<f32>"];
        int[] offsets = [0, 8, 16, 20, 24, 28, 32, 36, 48];
        int[] sizes = [8, 8, 4, 4, 4, 4, 4, 4, 16];
        for (int index = 0; index < members.GetArrayLength(); index++)
        {
            JsonElement member = members[index];
            if (Required(member, "name", JsonValueKind.String).GetString() != memberNames[index] ||
                Required(member, "provider", JsonValueKind.String).GetString() != providers[index] ||
                Required(member, "type", JsonValueKind.String).GetString() != types[index] ||
                RequiredInt(member, "offset") != offsets[index] || RequiredInt(member, "bytes") != sizes[index])
                throw new InvalidDataException($"ComputeArtifact.BindingMismatch: luminance parameter {index}.");
        }
    }

    /// <summary>Rejects a hash-bound module unless it implements the engine's fixed deformation ABI.</summary>
    public static void ValidatePackedSkinning(ShaderProgramArtifact artifact)
    {
        if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.Pass != "skinning" ||
            artifact.ComputeEntryPoint != "skin" || artifact.VertexEntryPoint is not null ||
            artifact.FragmentEntryPoint is not null ||
            artifact.SemanticSchemaIdentity != "xrengine.engine.compute.v1" ||
            artifact.ComputeWorkgroupSize != new ShaderComputeWorkgroupSize(64, 1, 1) ||
            artifact.VertexBuffers.Length != 0 || artifact.Resources.Length != 6)
            throw new InvalidDataException("ComputeArtifact.DescriptorMismatch: packed skinning requires the engine compute ABI.");

        using JsonDocument document = JsonDocument.Parse(artifact.DescriptorBytes.ToArray());
        JsonElement descriptor = document.RootElement;
        if (descriptor.TryGetProperty("materialVariant", out _) ||
            !descriptor.TryGetProperty("pipeline", out JsonElement pipeline) ||
            pipeline.ValueKind != JsonValueKind.Object || pipeline.EnumerateObject().Any() ||
            !descriptor.TryGetProperty("layout", out JsonElement layout) ||
            !layout.TryGetProperty("bindings", out JsonElement bindings) ||
            bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() != 6)
            throw new InvalidDataException("ComputeArtifact.DescriptorMismatch: packed skinning cannot declare a raster pipeline or material variant.");

        string[] names = ["PackedSkinningData", "BonePalette", "ActiveMorphs", "DeformedPositions", "DeformedAttributes", "Update"];
        string[] physicalNames = ["data", "palette", "activeMorphs", "vertices", "attributes", "update"];
        string[] kinds = ["read-only-storage", "read-only-storage", "read-only-storage", "storage", "storage", "uniform"];
        int[] bytes = [4, 16, 8, 4, 16, 16];
        for (int index = 0; index < names.Length; index++)
        {
            JsonElement binding = bindings[index];
            JsonElement visibility = Required(binding, "visibility", JsonValueKind.Array);
            if (Required(binding, "name", JsonValueKind.String).GetString() != names[index] ||
                Required(binding, "physicalName", JsonValueKind.String).GetString() != physicalNames[index] ||
                RequiredInt(binding, "group") != 0 || RequiredInt(binding, "binding") != index ||
                Required(binding, "kind", JsonValueKind.String).GetString() != kinds[index] ||
                Required(binding, "owner", JsonValueKind.String).GetString() != "Engine" ||
                Required(binding, "frequency", JsonValueKind.String).GetString() != "Object" ||
                RequiredInt(binding, "bytes") != bytes[index] ||
                RequiredBoolean(binding, "dynamic") != (index == 5) ||
                (binding.TryGetProperty("runtimeArray", out JsonElement runtimeArray) && runtimeArray.ValueKind == JsonValueKind.True) != (index != 5) ||
                visibility.GetArrayLength() != 1 || visibility[0].ValueKind != JsonValueKind.String ||
                visibility[0].GetString() != "compute")
                throw new InvalidDataException($"ComputeArtifact.BindingMismatch: packed skinning binding {index}.");
            JsonElement members = Required(binding, "members", JsonValueKind.Array);
            if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() != (index == 5 ? 4 : 0))
                throw new InvalidDataException($"ComputeArtifact.BindingMismatch: packed skinning binding {index} members.");
        }
        JsonElement updateMembers = Required(bindings[5], "members", JsonValueKind.Array);
        string[] memberNames = ["activeCount", "reserved0", "reserved1", "reserved2"];
        string[] providers = ["ActiveMorphCount", "Reserved0", "Reserved1", "Reserved2"];
        for (int index = 0; index < 4; index++)
        {
            JsonElement member = updateMembers[index];
            if (Required(member, "name", JsonValueKind.String).GetString() != memberNames[index] ||
                Required(member, "provider", JsonValueKind.String).GetString() != providers[index] ||
                RequiredInt(member, "offset") != index * 4 ||
                RequiredInt(member, "bytes") != 4 ||
                Required(member, "type", JsonValueKind.String).GetString() != "u32")
                throw new InvalidDataException($"ComputeArtifact.BindingMismatch: packed skinning update member {index}.");
        }
    }

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != kind)
            throw new InvalidDataException($"ComputeArtifact.DescriptorMalformed: expected '{name}' as {kind}.");
        return value;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        JsonElement value = Required(parent, name, JsonValueKind.Number);
        if (!value.TryGetInt32(out int result))
            throw new InvalidDataException($"ComputeArtifact.DescriptorMalformed: expected '{name}' as a 32-bit integer.");
        return result;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"ComputeArtifact.DescriptorMalformed: expected '{name}' as a boolean.");
        return value.ValueKind == JsonValueKind.True;
    }
}
