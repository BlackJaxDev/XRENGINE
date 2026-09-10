using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Strictly validates Slang reflection and SPIR-V physical layout against engine ABI contracts.
/// </summary>
internal static partial class SlangShaderReflection
{
    internal static SlangShaderReflectionResult Validate(
        string reflectionJson,
        byte[] spirv,
        IReadOnlyList<ShaderAbiResourceContract> contracts,
        ShaderStageFlags stageFlags,
        EShaderType shaderType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reflectionJson);
        ArgumentNullException.ThrowIfNull(contracts);
        Dictionary<(uint Set, uint Binding), ShaderAbiResourceContract> byBinding = [];
        Dictionary<string, ShaderAbiResourceContract> byName = new(StringComparer.Ordinal);
        foreach (ShaderAbiResourceContract contract in contracts)
        {
            ValidateContract(contract);
            if (!byBinding.TryAdd((contract.Set, contract.Binding), contract) || !byName.TryAdd(contract.Name, contract))
                throw new InvalidOperationException($"Shader ABI contracts contain duplicate resource '{contract.Name}' or binding {contract.Set}:{contract.Binding}.");
        }

        IReadOnlyList<DescriptorBindingInfo> reflectedBindings = VulkanShaderReflection.ExtractBindingsStrict(spirv, stageFlags);
        PhysicalModule physical = new(spirv);
        using JsonDocument document = JsonDocument.Parse(reflectionJson);
        JsonElement parameters = document.RootElement.TryGetProperty("parameters", out JsonElement foundParameters) &&
            foundParameters.ValueKind == JsonValueKind.Array
            ? foundParameters
            : throw new InvalidOperationException("Slang reflection JSON does not contain a parameter array.");

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<DescriptorBindingInfo> descriptorBindings = new(contracts.Count);
        List<AutoUniformBlockInfo> autoBlocks = [];
        foreach (JsonElement parameter in parameters.EnumerateArray())
        {
            string name = RequiredString(parameter, "name", "reflection parameter");
            if (!byName.TryGetValue(name, out ShaderAbiResourceContract? contract))
                throw new InvalidOperationException($"Slang reflection parameter '{name}' has no explicit engine ABI contract.");
            if (!seen.Add(name))
                throw new InvalidOperationException($"Slang reflection contains duplicate parameter '{name}'.");

            uint reflectedBinding = RequiredBindingIndex(parameter);
            if (reflectedBinding != contract.Binding)
                throw new InvalidOperationException($"Slang parameter '{name}' declares binding {reflectedBinding}; ABI requires {contract.Binding}.");
            uint reflectedSet = GetOptionalUInt(RequiredProperty(parameter, "binding", name), "space", defaultValue: 0);
            if (reflectedSet != contract.Set)
                throw new InvalidOperationException($"Slang parameter '{name}' declares space {reflectedSet}; ABI requires set {contract.Set}.");
            ValidateJsonResource(parameter, contract);

            DescriptorBindingInfo descriptor = FindDescriptor(reflectedBindings, contract);
            descriptorBindings.Add(descriptor with
            {
                Name = contract.PhysicalName,
                DeclaredOwner = Enum.Parse<EVulkanDescriptorOwner>((contract.DescriptorLifetime
                    ?? throw new InvalidOperationException($"Resource '{name}' requires an explicit descriptor lifetime.")).ToString()),
                DeclaredFrequency = ToFrequency(contract.Frequency),
            });
            PhysicalResource physicalResource = physical.GetResource(contract.Set, contract.Binding);
            ValidatePhysicalResource(physicalResource, contract);
            if (contract.Kind == ShaderAbiResourceKind.UniformBuffer)
                ValidateUniformMembers(parameter, physicalResource, contract);
            else if (contract.Kind == ShaderAbiResourceKind.StorageBuffer)
                ValidateStorageMembers(parameter, physicalResource, contract);

            if (contract.Kind == ShaderAbiResourceKind.UniformBuffer && contract.Owner is ShaderAbiResourceOwner.Engine or ShaderAbiResourceOwner.Material)
            {
                if (contract.Kind != ShaderAbiResourceKind.UniformBuffer)
                    throw new InvalidOperationException($"Engine-owned ABI resource '{contract.Name}' must be a uniform buffer.");
                AutoUniformBlockInfo block = CreateAutoUniformBlock(contract, shaderType);
                VulkanAutoUniformBindingSchema schema = VulkanAutoUniformBindingSchema.Compile(block, 0);
                if (!schema.IsFastPathEligible)
                    throw new InvalidOperationException($"Native ABI provider mapping failed: {schema.FallbackReason}");
                foreach (VulkanAutoUniformBindingOperation operation in schema.Operations)
                    if (operation.Frequency != block.Frequency ||
                        (contract.Owner == ShaderAbiResourceOwner.Material && operation.SourceKind != EVulkanAutoUniformSourceKind.MaterialOrRuntime))
                        throw new InvalidOperationException($"Native provider '{operation.Member.Name}' does not match declared owner/frequency.");
                autoBlocks.Add(block);
            }
        }

        if (seen.Count != contracts.Count)
        {
            string missing = contracts.First(static x => true).Name;
            foreach (ShaderAbiResourceContract contract in contracts)
                if (!seen.Contains(contract.Name))
                {
                    missing = contract.Name;
                    break;
                }
            throw new InvalidOperationException($"Slang reflection omitted ABI resource '{missing}'.");
        }

        if (reflectedBindings.Count != contracts.Count)
            throw new InvalidOperationException("SPIR-V exposes descriptor bindings not covered by the explicit ABI contract.");

        return new SlangShaderReflectionResult(descriptorBindings, autoBlocks, physical.VertexLocations);
    }

    private static void ValidateJsonResource(JsonElement parameter, ShaderAbiResourceContract contract)
    {
        JsonElement type = RequiredProperty(parameter, "type", contract.Name);
        string kind = RequiredString(type, "kind", contract.Name);
        string expected = contract.Kind switch
        {
            ShaderAbiResourceKind.UniformBuffer => "constantBuffer",
            ShaderAbiResourceKind.StorageBuffer => "resource",
            ShaderAbiResourceKind.CombinedImageSampler => "resource",
            ShaderAbiResourceKind.SampledImage => "resource",
            ShaderAbiResourceKind.Sampler => "resource",
            ShaderAbiResourceKind.StorageImage => "resource",
            _ => throw new ArgumentOutOfRangeException(nameof(contract.Kind), contract.Kind, null),
        };
        if (!string.Equals(kind, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Slang parameter '{contract.Name}' has kind '{kind}'; ABI requires '{expected}'.");
        if (kind == "resource" && type.TryGetProperty("elementCount", out _))
            throw new InvalidOperationException($"Slang resource '{contract.Name}' is an unsupported descriptor array.");
        if (contract.Kind == ShaderAbiResourceKind.UniformBuffer)
        {
            JsonElement elementLayout = RequiredProperty(type, "elementVarLayout", contract.Name);
            ValidateUInt(RequiredProperty(elementLayout, "binding", contract.Name), "size", contract.ByteSize, contract.Name);
        }
    }

    private static DescriptorBindingInfo FindDescriptor(
        IReadOnlyList<DescriptorBindingInfo> descriptors,
        ShaderAbiResourceContract contract)
    {
        for (int index = 0; index < descriptors.Count; index++)
        {
            DescriptorBindingInfo descriptor = descriptors[index];
            if (descriptor.Set != contract.Set || descriptor.Binding != contract.Binding)
                continue;
            if (descriptor.Count != 1)
                throw new NotSupportedException($"SPIR-V descriptor array {contract.Set}:{contract.Binding} is not supported by the Slang ABI.");
            if (descriptor.DescriptorType != ToDescriptorType(contract.Kind))
                throw new InvalidOperationException($"SPIR-V binding {contract.Set}:{contract.Binding} descriptor type '{descriptor.DescriptorType}' does not match ABI '{contract.Kind}'.");
            if (contract.Kind == ShaderAbiResourceKind.CombinedImageSampler && descriptor.ExpectedImageViewType != ImageViewType.Type2D)
                throw new NotSupportedException($"Slang combined sampler '{contract.Name}' must use a non-array 2D image.");
            return descriptor;
        }
        throw new InvalidOperationException($"SPIR-V omitted ABI descriptor {contract.Set}:{contract.Binding} for '{contract.Name}'.");
    }

    private static void ValidatePhysicalResource(PhysicalResource resource, ShaderAbiResourceContract contract)
    {
        if (resource.Set != contract.Set || resource.Binding != contract.Binding)
            throw new InvalidOperationException($"SPIR-V resource '{contract.Name}' changed its descriptor binding.");
        if (contract.Kind == ShaderAbiResourceKind.UniformBuffer && !resource.IsBlock)
            throw new InvalidOperationException($"SPIR-V resource '{contract.Name}' is not a uniform block.");
        if (contract.Kind == ShaderAbiResourceKind.StorageBuffer && !resource.IsBlock)
            throw new InvalidOperationException($"SPIR-V resource '{contract.Name}' is not a structured storage block.");
    }

    private static void ValidateUniformMembers(
        JsonElement parameter,
        PhysicalResource physical,
        ShaderAbiResourceContract contract)
    {
        JsonElement elementType = RequiredProperty(RequiredProperty(parameter, "type", contract.Name), "elementType", contract.Name);
        JsonElement fields = RequiredProperty(elementType, "fields", contract.Name);
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() != contract.Members.Length)
            throw new InvalidOperationException($"Slang uniform block '{contract.Name}' member count does not match its ABI contract.");

        foreach (ShaderAbiMemberContract member in contract.Members)
        {
            JsonElement field = FindField(fields, member.PhysicalName, contract.Name);
            JsonElement binding = RequiredProperty(field, "binding", member.PhysicalName);
            ValidateUInt(binding, "offset", member.Offset, member.PhysicalName);
            ValidateUInt(binding, "size", member.Size, member.PhysicalName);
            PhysicalMember actual = physical.GetMember(member.PhysicalName);
            if (actual.Offset != member.Offset || actual.Size != member.Size)
                throw new InvalidOperationException($"SPIR-V member '{member.PhysicalName}' offset/size does not match ABI contract.");
            if (!string.Equals(actual.PhysicalType, member.PhysicalType, StringComparison.Ordinal))
                throw new InvalidOperationException($"SPIR-V member '{member.PhysicalName}' type '{actual.PhysicalType}' does not match ABI '{member.PhysicalType}'.");
            ValidateOptionalLayout(member, actual, binding);
        }
    }

    private static void ValidateStorageMembers(
        JsonElement parameter,
        PhysicalResource physical,
        ShaderAbiResourceContract contract)
    {
        JsonElement type = RequiredProperty(parameter, "type", contract.Name);
        if (!string.Equals(RequiredString(type, "baseShape", contract.Name), "structuredBuffer", StringComparison.Ordinal))
            throw new NotSupportedException($"Storage ABI resource '{contract.Name}' must be a structured buffer.");
        PhysicalStorageElement element = physical.GetStorageElement();
        if (element.Stride != contract.ByteSize)
            throw new InvalidOperationException($"SPIR-V structured buffer '{contract.Name}' stride {element.Stride} does not match ABI element size {contract.ByteSize}.");
        if (element.MemberCount != contract.Members.Length)
            throw new InvalidOperationException($"SPIR-V storage element '{contract.Name}' member count does not match its ABI contract.");
        foreach (ShaderAbiMemberContract member in contract.Members)
        {
            PhysicalMember actual = element.GetMember(member.PhysicalName);
            if (actual.Offset != member.Offset || actual.Size != member.Size ||
                !string.Equals(actual.PhysicalType, member.PhysicalType, StringComparison.Ordinal))
                throw new InvalidOperationException($"SPIR-V storage element member '{member.PhysicalName}' does not match ABI.");
            ValidateOptionalLayout(member, actual, default);
        }
    }

    private static void ValidateOptionalLayout(ShaderAbiMemberContract expected, PhysicalMember actual, JsonElement binding)
    {
        if (actual.ArrayCount != expected.ArrayCount)
            throw new InvalidOperationException($"SPIR-V member '{expected.PhysicalName}' array count does not match ABI.");
        if (actual.ArrayStride != expected.ArrayStride)
            throw new InvalidOperationException($"SPIR-V member '{expected.PhysicalName}' array stride does not match ABI.");
        if (expected.ArrayCount != 0 && binding.ValueKind != JsonValueKind.Undefined && GetUInt(binding, "elementStride") != expected.ArrayStride)
            throw new InvalidOperationException($"Slang reflection member '{expected.PhysicalName}' array stride does not match ABI.");
        if (actual.MatrixOrder != expected.MatrixOrder)
            throw new InvalidOperationException($"SPIR-V member '{expected.PhysicalName}' matrix-major decoration does not match ABI.");
        if (actual.MatrixStride != expected.MatrixStride)
            throw new InvalidOperationException($"SPIR-V member '{expected.PhysicalName}' matrix stride does not match ABI.");
    }

    private static AutoUniformBlockInfo CreateAutoUniformBlock(ShaderAbiResourceContract resource, EShaderType shaderType)
    {
        List<AutoUniformMember> members = new(resource.Members.Length);
        foreach (ShaderAbiMemberContract member in resource.Members)
        {
            // Existing uniform writers copy Matrix4x4 bytes without transposition.
            if (member.PhysicalType.StartsWith("float4x4", StringComparison.Ordinal) &&
                (member.MatrixOrder != ShaderAbiMatrixOrder.RowMajor || member.MatrixStride != 16))
                throw new NotSupportedException($"Native auto-uniform matrix '{member.ProviderName}' requires physical RowMajor storage with stride 16.");
            members.Add(new AutoUniformMember(
                member.ProviderName,
                ToGlslType(member.PhysicalType),
                ToEngineType(member.PhysicalType),
                member.ArrayCount != 0,
                member.ArrayCount,
                member.ArrayStride,
                member.Offset,
                member.Size,
                null,
                null));
        }
        return new AutoUniformBlockInfo(
            resource.Name,
            resource.PhysicalName,
            resource.Set,
            resource.Binding,
            resource.ByteSize,
            members,
            shaderType,
            ToFrequency(resource.Frequency));
    }

    private static DescriptorType ToDescriptorType(ShaderAbiResourceKind kind)
        => kind switch
        {
            ShaderAbiResourceKind.UniformBuffer => DescriptorType.UniformBuffer,
            ShaderAbiResourceKind.StorageBuffer => DescriptorType.StorageBuffer,
            ShaderAbiResourceKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
            ShaderAbiResourceKind.SampledImage => DescriptorType.SampledImage,
            ShaderAbiResourceKind.Sampler => DescriptorType.Sampler,
            ShaderAbiResourceKind.StorageImage => DescriptorType.StorageImage,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static void ValidateContract(ShaderAbiResourceContract contract)
    {
        if (contract.DescriptorLifetime is not { } lifetime || !Enum.IsDefined(lifetime) ||
            !Enum.IsDefined(contract.Owner) || !Enum.IsDefined(contract.Frequency))
            throw new InvalidOperationException("Native resources require defined supplier, frequency, and descriptor-lifetime metadata.");
        if (contract.Kind is ShaderAbiResourceKind.SampledImage or ShaderAbiResourceKind.Sampler or ShaderAbiResourceKind.StorageImage)
            throw new NotSupportedException("The native pilot supports combined 2D texture samplers; separate samplers/images and storage images require an explicit ABI extension.");
        if (string.IsNullOrWhiteSpace(contract.Name) || string.IsNullOrWhiteSpace(contract.PhysicalName))
            throw new InvalidOperationException("Shader ABI resource names must be explicit.");
        if (contract.Owner == ShaderAbiResourceOwner.Material && contract.Frequency != ShaderAbiFrequency.Material)
            throw new InvalidOperationException($"Material ABI resource '{contract.Name}' must use Material frequency.");
        if (contract.Owner == ShaderAbiResourceOwner.Engine && contract.Frequency is ShaderAbiFrequency.Unknown or ShaderAbiFrequency.Material)
            throw new InvalidOperationException($"Engine ABI resource '{contract.Name}' has an incompatible frequency.");
        HashSet<string> physicalNames = new(StringComparer.Ordinal);
        foreach (ShaderAbiMemberContract member in contract.Members)
        {
            if (!physicalNames.Add(member.PhysicalName) || (ulong)member.Offset + member.Size > contract.ByteSize)
                throw new InvalidOperationException($"Resource '{contract.Name}' has duplicate or out-of-range ABI members.");
            if (string.IsNullOrWhiteSpace(member.PhysicalName) || string.IsNullOrWhiteSpace(member.ProviderName))
                throw new InvalidOperationException($"ABI resource '{contract.Name}' contains a member without explicit physical and provider names.");
            if (contract.Owner == ShaderAbiResourceOwner.Engine &&
                !Enum.TryParse<EEngineUniform>(member.ProviderName, ignoreCase: false, out _))
                throw new InvalidOperationException($"Engine ABI member '{member.ProviderName}' is not a recognized engine or temporal provider.");
        }
    }

    private static string ToGlslType(string physicalType)
        => NormalizePhysicalValueType(physicalType) switch
        {
            "float" => "float",
            "int" => "int",
            "uint" => "uint",
            "float2" => "vec2",
            "float3" => "vec3",
            "float4" => "vec4",
            "float3x3" => "mat3",
            "float4x4" => "mat4",
            _ => throw new NotSupportedException($"ABI physical type '{physicalType}' cannot be copied by the auto-uniform binder."),
        };

    private static EShaderVarType ToEngineType(string physicalType)
        => NormalizePhysicalValueType(physicalType) switch
        {
            "float" => EShaderVarType._float,
            "int" => EShaderVarType._int,
            "uint" => EShaderVarType._uint,
            "float2" => EShaderVarType._vec2,
            "float3" => EShaderVarType._vec3,
            "float4" => EShaderVarType._vec4,
            "float3x3" => EShaderVarType._mat3,
            "float4x4" => EShaderVarType._mat4,
            _ => throw new NotSupportedException($"ABI physical type '{physicalType}' cannot be copied by the auto-uniform binder."),
        };

    private static string NormalizePhysicalValueType(string physicalType)
        => physicalType.EndsWith("[]", StringComparison.Ordinal)
            ? physicalType[..^2]
            : physicalType;

    private static EVulkanBindingFrequency ToFrequency(ShaderAbiFrequency frequency)
        => frequency switch
        {
            ShaderAbiFrequency.Unknown => EVulkanBindingFrequency.Unknown,
            ShaderAbiFrequency.Frame => EVulkanBindingFrequency.Frame,
            ShaderAbiFrequency.View => EVulkanBindingFrequency.View,
            ShaderAbiFrequency.Pass => EVulkanBindingFrequency.Pass,
            ShaderAbiFrequency.Material => EVulkanBindingFrequency.Material,
            ShaderAbiFrequency.Object => EVulkanBindingFrequency.Object,
            ShaderAbiFrequency.Instance => EVulkanBindingFrequency.Instance,
            ShaderAbiFrequency.RuntimeCallback => EVulkanBindingFrequency.RuntimeCallback,
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
        };

    private static string RequiredString(JsonElement element, string property, string context)
        => RequiredProperty(element, property, context).GetString()
            ?? throw new InvalidOperationException($"Slang reflection '{context}' property '{property}' is empty.");

    private static JsonElement RequiredProperty(JsonElement element, string property, string context)
        => element.TryGetProperty(property, out JsonElement value)
            ? value
            : throw new InvalidOperationException($"Slang reflection '{context}' omitted '{property}'.");

    private static uint RequiredBindingIndex(JsonElement parameter)
        => GetUInt(RequiredProperty(parameter, "binding", "parameter"), "index");

    private static JsonElement FindField(JsonElement fields, string name, string resourceName)
    {
        foreach (JsonElement field in fields.EnumerateArray())
            if (string.Equals(RequiredString(field, "name", resourceName), name, StringComparison.Ordinal))
                return field;
        throw new InvalidOperationException($"Slang uniform block '{resourceName}' omitted ABI member '{name}'.");
    }

    private static void ValidateUInt(JsonElement element, string property, uint expected, string context)
    {
        if (GetUInt(element, property) != expected)
            throw new InvalidOperationException($"Slang reflection '{context}' property '{property}' does not match ABI.");
    }

    private static uint GetUInt(JsonElement element, string property)
        => RequiredProperty(element, property, "binding").GetUInt32();

    private static uint GetOptionalUInt(JsonElement element, string property, uint defaultValue)
        => element.TryGetProperty(property, out JsonElement value) ? value.GetUInt32() : defaultValue;
}
