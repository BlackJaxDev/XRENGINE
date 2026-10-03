using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using XREngine.Rendering;

namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>Loads hash-verified whole-program WGSL artifacts and rejects unknown physical ABI shapes.</summary>
public static class ShaderProgramArtifactReader
{
    public const int SchemaVersion = 3;
    public const string CoordinateConvention = "xrengine.webgpu.coordinates.v1";
    private const int MaxSourceBytes = 1024 * 1024;
    private const int MaxDescriptorBytes = 64 * 1024;
    private static readonly HashSet<string> LimitNames = new(StringComparer.Ordinal)
    {
        "maxVertexAttributes", "maxVertexBuffers", "maxVertexBufferArrayStride", "maxBindGroups", "maxBindingsPerBindGroup",
        "maxUniformBufferBindingSize", "maxDynamicUniformBuffersPerPipelineLayout", "maxUniformBuffersPerShaderStage",
        "maxSampledTexturesPerShaderStage", "maxSamplersPerShaderStage", "maxStorageBuffersPerShaderStage",
        "maxStorageBufferBindingSize", "maxDynamicStorageBuffersPerPipelineLayout",
        "maxComputeWorkgroupSizeX", "maxComputeWorkgroupSizeY", "maxComputeWorkgroupSizeZ",
        "maxComputeInvocationsPerWorkgroup", "maxComputeWorkgroupsPerDimension",
        "maxComputeWorkgroupStorageSize", "maxStorageTexturesPerShaderStage",
    };

    /// <summary>Checks source length/hash before exposing the explicit layout to an engine renderer.</summary>
    public static ShaderProgramArtifact Read(ReadOnlySpan<byte> descriptorBytes, ReadOnlySpan<byte> wgslBytes)
    {
        Require(descriptorBytes.Length is > 0 and <= MaxDescriptorBytes, "descriptor exceeds its byte limit");
        Require(wgslBytes.Length is > 0 and <= MaxSourceBytes, "WGSL exceeds its byte limit");
        using JsonDocument document = JsonDocument.Parse(descriptorBytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        CheckUnique(document.RootElement);
        JsonElement descriptor = document.RootElement;
        string[] keys = ["schemaVersion", "name", "pass", "sourceLanguage", "target", "entryPoints", "defines", "includes",
            "specialization", "requiredFeatures", "requiredLimits", "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline",
            "coordinates", "compilerIdentity", "source", "sourceMap", "dependencies"];
        if (descriptor.TryGetProperty("materialVariant", out _)) keys = [.. keys, "materialVariant"];
        if (descriptor.TryGetProperty("workgroupSize", out _)) keys = [.. keys, "workgroupSize"];
        ExactKeys(descriptor, keys);
        if (descriptor.TryGetProperty("materialVariant", out JsonElement variant))
            _ = ReadMaterialVariantKey(variant, Text(descriptor, "pass"), ShaderCompileTarget.WebGPUWgsl);
        string language = Text(descriptor, "sourceLanguage"), compiler = Text(descriptor, "compilerIdentity");
        Require(language is "Slang" or "WGSL" or "MaterialRecipe", "engine artifacts require Slang, WGSL, or an authored material source");
        Require(language switch
        {
            "Slang" => Regex.IsMatch(compiler, "^slang/2026\\.8/[0-9a-f]{64}$", RegexOptions.CultureInvariant),
            "MaterialRecipe" => Regex.IsMatch(compiler, "^xrengine-material-slang/1\\+slang/2026\\.8/[0-9a-f]{64}$", RegexOptions.CultureInvariant),
            _ => compiler == "xrengine-wgsl-packager/2",
        }, "unrecognized compiler identity");
        Require(Property(descriptor, "specialization", JsonValueKind.Object).EnumerateObject().Count() == 0, "specialization requires separately cooked variants");
        JsonElement dependencies = Property(descriptor, "dependencies", JsonValueKind.Array);
        Require(dependencies.GetArrayLength() is > 0 and <= 512, "invalid dependency count");
        HashSet<string> dependencyPaths = new(StringComparer.Ordinal);
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            ExactKeys(dependency, "path", "sha256");
            string dependencyPath = Text(dependency, "path");
            Require(RelativePath(dependencyPath) && dependencyPaths.Add(dependencyPath), "invalid or duplicate dependency path");
            Require(Regex.IsMatch(Text(dependency, "sha256"), "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "invalid dependency hash");
        }
        JsonElement sourceMap = Property(descriptor, "sourceMap", JsonValueKind.Object);
        ExactKeys(sourceMap, "kind", "path");
        Require(Text(sourceMap, "kind") == (language switch
        {
            "Slang" => "unmapped",
            "MaterialRecipe" => "generated",
            _ => "identity",
        }) && RelativePath(Text(sourceMap, "path")), "invalid source map");
        JsonElement source = Property(descriptor, "source", JsonValueKind.Object);
        ExactKeys(source, "path", "sha256", "byteLength", "url");
        Require(Int(source, "byteLength") == wgslBytes.Length, "WGSL byte length does not match the descriptor");
        string hash = Convert.ToHexStringLower(SHA256.HashData(wgslBytes));
        Require(Text(source, "sha256") == hash && Text(source, "url") == hash + ".wgsl", "WGSL content hash does not match the descriptor");
        string identity = Convert.ToHexStringLower(SHA256.HashData(descriptorBytes));
        return ReadLayout(descriptor, new ShaderArtifact(ShaderCompileTarget.WebGPUWgsl, wgslBytes), identity)
            with { DescriptorBytes = ImmutableArray.CreateRange(descriptorBytes.ToArray()) };
    }

    /// <summary>Validates a recipe's stage, resource, member, and vertex ABI before compilation.</summary>
    public static ShaderProgramArtifact ReadLayout(JsonElement descriptor, ShaderArtifact artifact, string identity)
    {
        CheckUnique(descriptor);
        Require(Int(descriptor, "schemaVersion") == SchemaVersion, "engine shader artifacts require schema 3");
        Require(Text(descriptor, "target") == "WebGPUWgsl" && artifact.Target == ShaderCompileTarget.WebGPUWgsl, "target must be WebGPUWgsl");
        Require(Text(descriptor, "matrixLayout") == "column-major", "only column-major WGSL matrices are admitted");
        Require(Text(descriptor, "coordinates") == CoordinateConvention, "unsupported coordinate convention");
        string schema = Text(descriptor, "semanticSchemaIdentity");
        Require(Regex.IsMatch(schema, "^xrengine\\.engine\\.[a-z][a-z0-9.-]*\\.v[1-9][0-9]*$", RegexOptions.CultureInvariant), "semantic schema must identify a versioned engine ABI");
        string name = Text(descriptor, "name"), pass = Text(descriptor, "pass");
        EngineMaterialVariantKey? materialVariant = descriptor.TryGetProperty("materialVariant", out JsonElement variant)
            ? ReadMaterialVariantKey(variant, pass, artifact.Target)
            : null;
        Require(Identifier(name, allowDash: true) && (Identifier(pass, allowDash: true)
            || Regex.IsMatch(pass, "^[a-z][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)),
            "material and pass names must be bounded identifiers");
        JsonElement entries = Property(descriptor, "entryPoints", JsonValueKind.Object);
        string? vertex = null, fragment = null, compute = null;
        ShaderStageVisibility stages = ShaderStageVisibility.None;
        foreach (JsonProperty entry in entries.EnumerateObject())
        {
            string value = entry.Value.GetString() ?? "";
            Require(Identifier(value), "entry point must be an identifier");
            switch (entry.Name)
            {
                case "vertex": vertex = value; stages |= ShaderStageVisibility.Vertex; break;
                case "fragment": fragment = value; stages |= ShaderStageVisibility.Fragment; break;
                case "compute": compute = value; stages |= ShaderStageVisibility.Compute; break;
                default: throw Invalid("unsupported shader stage '" + entry.Name + "'");
            }
        }
        Require((vertex is not null && compute is null) || (compute is not null && vertex is null && fragment is null), "provide a vertex stage with optional fragment, or one compute stage");
        ShaderComputeWorkgroupSize? workgroupSize = null;
        if (compute is not null)
        {
            JsonElement dimensions = Property(descriptor, "workgroupSize", JsonValueKind.Array);
            Require(dimensions.GetArrayLength() == 3, "compute workgroupSize must have three dimensions");
            Span<uint> values = stackalloc uint[3];
            for (int index = 0; index < values.Length; index++)
            {
                Require(dimensions[index].TryGetUInt32(out uint value) && value is > 0 and <= 1024,
                    "compute workgroup dimensions must be positive bounded integers");
                values[index] = value;
            }
            Require((ulong)values[0] * values[1] * values[2] <= 1024,
                "compute workgroup invocation count exceeds the supported profile");
            workgroupSize = new(values[0], values[1], values[2]);
        }
        else Require(!descriptor.TryGetProperty("workgroupSize", out _), "raster artifacts cannot declare compute workgroup dimensions");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.OpaqueShadowDepthV1)
            Require(vertex == "depthVertex" && fragment is null && compute is null,
                "opaque shadow depth variant requires its vertex-only depth entry point");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1)
            Require(vertex == "pointShadowDepthVertex" && fragment == "pointShadowDepthFragment" && compute is null,
                "opaque point shadow distance requires its exact vertex and fragment entry points");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1)
            Require(vertex == "spotShadowDepthVertex" && fragment == "spotShadowDepthFragment" && compute is null,
                "opaque spot shadow depth requires its exact vertex and fragment entry points");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.StandardLitColorV2)
            Require(vertex is not null && fragment is not null && compute is null,
                "lit-color coverage variants require vertex and fragment stages, including alpha-tested depth");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
            Require(vertex is not null && fragment is not null && compute is null,
                "lit-texture variants require vertex and fragment stages");
        if (materialVariant?.Semantic.IsSkybox() == true)
            Require(vertex == "skyVertex" && fragment == "skyFragment" && compute is null,
                "skybox variants require their exact vertex and fragment entry points");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.UICanvasSurfaceV1)
            Require(vertex == "canvasSurfaceVertex" && fragment == "canvasSurfaceFragment" && compute is null,
                "canvas surfaces require their exact vertex and fragment entry points");
        if (materialVariant is { } debugEntries &&
            debugEntries.Semantic.Semantic is (EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle))
            Require(vertex is not null && fragment is not null && compute is null,
                "debug primitive variants require vertex and fragment entry points");
        Require(vertex is null || vertex != fragment, "stage entry points must be distinct");
        Require(Property(descriptor, "requiredFeatures", JsonValueKind.Array).GetArrayLength() == 0, "optional device features have not been admitted");
        Require(Property(descriptor, "pipeline", JsonValueKind.Object).EnumerateObject().Count() == 0, "pipeline state belongs to the engine material/pass, not the cooked module");
        JsonElement layout = Property(descriptor, "layout", JsonValueKind.Object);
        ExactKeys(layout, "vertexBuffers", "bindings");
        ImmutableArray<ShaderVertexBufferLayout>.Builder buffers = ImmutableArray.CreateBuilder<ShaderVertexBufferLayout>();
        HashSet<int> locations = [], slots = [];
        foreach (JsonElement buffer in Property(layout, "vertexBuffers", JsonValueKind.Array).EnumerateArray())
        {
            ExactKeys(buffer, "slot", "stride", "stepMode", "attributes");
            int slot = Bounded(buffer, "slot", 0, 7), stride = Bounded(buffer, "stride", 4, 2048);
            string step = Text(buffer, "stepMode");
            Require(slots.Add(slot) && stride % 4 == 0 && step is "vertex" or "instance", "invalid or duplicate vertex buffer slot, stride, or step mode");
            ImmutableArray<ShaderVertexAttribute>.Builder attributes = ImmutableArray.CreateBuilder<ShaderVertexAttribute>();
            foreach (JsonElement attribute in Property(buffer, "attributes", JsonValueKind.Array).EnumerateArray())
            {
                ExactKeys(attribute, "location", "offset", "format", "semantic");
                int location = Bounded(attribute, "location", 0, 15), offset = Bounded(attribute, "offset", 0, 2047);
                string format = Text(attribute, "format"), semantic = Text(attribute, "semantic");
                int bytes = VertexFormatBytes(format);
                Require(locations.Add(location) && offset % 4 == 0 && offset + bytes <= stride, "overlapping vertex location or attribute outside its stride");
                Require(semantic is "position" or "normal" or "tangent" or "uv0" or "uv1" or "uv2" or "uv3" or "color0", "unsupported engine vertex semantic '" + semantic + "'");
                foreach (ShaderVertexAttribute previous in attributes)
                    Require(offset + bytes <= previous.Offset || offset >= previous.Offset + VertexFormatBytes(previous.Format), "vertex attributes overlap within a buffer");
                attributes.Add(new ShaderVertexAttribute(location, offset, format, semantic));
            }
            Require(attributes.Count > 0, "a vertex stream must have at least one attribute");
            buffers.Add(new ShaderVertexBufferLayout(slot, stride, step, attributes.ToImmutable()));
        }
        Require(compute is null || buffers.Count == 0, "compute layouts cannot declare vertex streams");
        for (int index = 0; index < buffers.Count; index++) Require(slots.Contains(index), "vertex buffer slots must be contiguous from zero");
        if (materialVariant is { } debugStream &&
            debugStream.Semantic.Semantic is (EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle))
            Require(buffers.Count == 1 && buffers[0].Slot == 0 && buffers[0].Stride == 12 &&
                buffers[0].StepMode == "vertex" && buffers[0].Attributes.Length == 1 &&
                buffers[0].Attributes[0] is { Location: 0, Offset: 0, Format: "float32x3", Semantic: "position" },
                "debug primitive variants require the fixed indexed position scaffold");
        if (materialVariant is { } uiStream &&
            uiStream.Semantic.Semantic is (EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UIQuadBatchedTexture or EngineMaterialSemantic.UITextBatchedBitmap))
            Require(buffers.Count == 1 && buffers[0].Slot == 0 && buffers[0].Stride == 12 &&
                buffers[0].StepMode == "vertex" && buffers[0].Attributes.Length == 1 &&
                buffers[0].Attributes[0] is { Location: 0, Offset: 0, Format: "float32x3", Semantic: "position" },
                "screen UI variants require the fixed indexed position quad");
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.UICanvasSurfaceV1)
            Require(buffers.Count == 1 && buffers[0].Slot == 0 && buffers[0].Stride == 20 &&
                buffers[0].StepMode == "vertex" && buffers[0].Attributes.Length == 2 &&
                buffers[0].Attributes[0] is { Location: 0, Offset: 0, Format: "float32x3", Semantic: "position" } &&
                buffers[0].Attributes[1] is { Location: 1, Offset: 12, Format: "float32x2", Semantic: "uv0" },
                "canvas surfaces require the exact position and UV vertex stream");
        ImmutableArray<ShaderStageResourceLayout>.Builder resources = ImmutableArray.CreateBuilder<ShaderStageResourceLayout>();
        HashSet<(int, int)> bindings = [];
        foreach (JsonElement resource in Property(layout, "bindings", JsonValueKind.Array).EnumerateArray())
        {
            string[] resourceKeys = ["name", "physicalName", "group", "binding", "kind", "visibility", "owner", "frequency", "bytes", "dynamic", "members"];
            bool runtimeArray = resource.TryGetProperty("runtimeArray", out JsonElement runtimeArrayValue);
            ExactKeys(resource, runtimeArray ? [.. resourceKeys, "runtimeArray"] : resourceKeys);
            int group = Bounded(resource, "group", 0, 3), binding = Bounded(resource, "binding", 0, 63);
            Require(bindings.Add((group, binding)), "duplicate resource binding");
            string resourceName = Text(resource, "name"), physicalName = Text(resource, "physicalName"), kind = Text(resource, "kind");
            Require(Identifier(resourceName) && Identifier(physicalName), "invalid resource name");
            ShaderStageVisibility visibility = ShaderStageVisibility.None;
            foreach (JsonElement visible in Property(resource, "visibility", JsonValueKind.Array).EnumerateArray())
            {
                ShaderStageVisibility flag = visible.GetString() switch
                {
                    "vertex" => ShaderStageVisibility.Vertex, "fragment" => ShaderStageVisibility.Fragment, "compute" => ShaderStageVisibility.Compute,
                    _ => throw Invalid("unknown resource stage visibility"),
                };
                Require((visibility & flag) == 0, "duplicate resource visibility");
                visibility |= flag;
            }
            Require(visibility != ShaderStageVisibility.None && (visibility & ~stages) == 0, "resource visibility references a missing stage");
            bool isTexture = ShaderTextureBindingType.TryParse(kind, out ShaderTextureBindingType textureShape);
            ShaderAbiResourceKind resourceKind = isTexture
                ? textureShape.IsStorage ? ShaderAbiResourceKind.StorageImage : ShaderAbiResourceKind.SampledImage
                : kind switch
            {
                "uniform" => ShaderAbiResourceKind.UniformBuffer,
                "read-only-storage" or "storage" => ShaderAbiResourceKind.StorageBuffer,
                "filtering-sampler" or "non-filtering-sampler" or "comparison-sampler" => ShaderAbiResourceKind.Sampler,
                _ => throw Invalid("unsupported resource binding kind '" + kind + "'"),
            };
            ShaderAbiResourceOwner owner = EnumValue<ShaderAbiResourceOwner>(resource, "owner");
            ShaderAbiFrequency frequency = EnumValue<ShaderAbiFrequency>(resource, "frequency");
            Require(frequency != ShaderAbiFrequency.Unknown, "resource update frequency must be explicit");
            int size = Bounded(resource, "bytes", 0, 65536);
            bool dynamic = Property(resource, "dynamic").GetBoolean();
            if (runtimeArray)
                Require(runtimeArrayValue.ValueKind == JsonValueKind.True &&
                    resourceKind == ShaderAbiResourceKind.StorageBuffer && !dynamic && size > 0 && size % 4 == 0,
                    "runtimeArray requires a non-dynamic storage element with a positive four-byte stride");
            Require((visibility & ShaderStageVisibility.Vertex) == 0 ||
                kind != "storage" && !(isTexture && textureShape.IsStorage && textureShape.StorageAccess != "read-only"),
                "vertex stages cannot write storage resources");
            bool isBuffer = resourceKind is ShaderAbiResourceKind.UniformBuffer or ShaderAbiResourceKind.StorageBuffer;
            bool rawDebugStorage = materialVariant is { } selected &&
                selected.Semantic.Semantic is (EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle) &&
                kind == "read-only-storage" && group == 2 && binding == 0 && !dynamic && size == 4 &&
                owner == ShaderAbiResourceOwner.Engine && frequency == ShaderAbiFrequency.Object &&
                visibility == ShaderStageVisibility.Vertex &&
                resourceName == (selected.Semantic.Semantic switch
                {
                    EngineMaterialSemantic.DebugPoint => "PointsBuffer",
                    EngineMaterialSemantic.DebugLine => "LinesBuffer",
                    _ => "TrianglesBuffer",
                });
            bool uiStorage = materialVariant is { } uiSelected &&
                uiSelected.Semantic.Semantic is (EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UIQuadBatchedTexture or EngineMaterialSemantic.UITextBatchedBitmap) &&
                kind == "read-only-storage" && group == 1 && !dynamic &&
                owner == ShaderAbiResourceOwner.Engine && frequency == ShaderAbiFrequency.Object &&
                visibility == ShaderStageVisibility.Vertex &&
                (uiSelected.Semantic.Semantic is EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UIQuadBatchedTexture
                    ? (binding, resourceName, size) is (0, "QuadTransformBuffer", 16) or
                        (1, "QuadColorBuffer", 16) or (2, "QuadBoundsBuffer", 16) ||
                        uiSelected.Semantic.Semantic == EngineMaterialSemantic.UIQuadBatchedTexture &&
                        (binding, resourceName, size) is (3, "QuadUvBuffer", 16)
                    : (binding, resourceName, size) is (0, "GlyphTransformsBuffer", 16) or
                        (1, "GlyphTexCoordsBuffer", 16) or (2, "TextInstanceBuffer", 16) or
                        (3, "GlyphTextIndexBuffer", 4));
            bool rawComputeStorage = runtimeArray && Property(resource, "members", JsonValueKind.Array).GetArrayLength() == 0;
            Require(isBuffer ? rawDebugStorage || uiStorage || runtimeArray || size > 0 && size % 16 == 0 : size == 0 && !dynamic,
                "invalid resource byte size or dynamic offset");
            ImmutableArray<ShaderAbiMemberContract>.Builder members = ImmutableArray.CreateBuilder<ShaderAbiMemberContract>();
            HashSet<string> names = new(StringComparer.Ordinal);
            uint end = 0;
            foreach (JsonElement member in Property(resource, "members", JsonValueKind.Array).EnumerateArray())
            {
                ExactKeys(member, "name", "provider", "offset", "bytes", "type");
                string memberName = Text(member, "name"), provider = Text(member, "provider"), type = Text(member, "type");
                int offset = Bounded(member, "offset", 0, 65535), memberSize = Bounded(member, "bytes", 4, 65536);
                Require(Identifier(memberName) && ProviderIdentifier(provider) && names.Add(memberName), "invalid or duplicate uniform member name");
                (int alignment, int expectedSize) = MemberShape(type);
                Require(offset % alignment == 0 && memberSize == expectedSize && offset >= end && offset + memberSize <= size, "member alignment, size, overlap, or buffer range mismatch");
                end = checked((uint)(offset + memberSize));
                bool matrix = type == "mat4x4<f32>";
                members.Add(new ShaderAbiMemberContract(memberName, provider, (uint)offset, (uint)memberSize, type,
                    MatrixOrder: matrix ? ShaderAbiMatrixOrder.ColumnMajor : ShaderAbiMatrixOrder.None, MatrixStride: matrix ? 16u : 0u));
            }
            Require(rawDebugStorage || uiStorage || rawComputeStorage ? members.Count == 0 : isBuffer ? members.Count > 0 : members.Count == 0,
                "only declared runtime storage bindings may omit fixed buffer members");
            ShaderAbiResourceContract contract = new(resourceName, physicalName, (uint)group, (uint)binding, resourceKind, owner, frequency, (uint)size, members.ToImmutable());
            resources.Add(new ShaderStageResourceLayout(contract, visibility, kind, dynamic) { RuntimeArray = runtimeArray });
        }
        Require(resources.Count <= 64, "resource count exceeds the bounded profile");
        Require(resources.GroupBy(resource => resource.Contract.Set).All(group => group.Count() <= 32),
            "one WebGPU bind group cannot exceed 32 installed resource entries");
        ImmutableDictionary<string, int>.Builder limits = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty limit in Property(descriptor, "requiredLimits", JsonValueKind.Object).EnumerateObject())
        {
            Require(LimitNames.Contains(limit.Name) && limit.Value.TryGetInt32(out int value) && value > 0, "invalid required limit '" + limit.Name + "'");
            limits.Add(limit.Name, limit.Value.GetInt32());
        }
        if (workgroupSize is { } groupSize)
        {
            CheckLimit(limits, "maxComputeWorkgroupSizeX", checked((int)groupSize.X));
            CheckLimit(limits, "maxComputeWorkgroupSizeY", checked((int)groupSize.Y));
            CheckLimit(limits, "maxComputeWorkgroupSizeZ", checked((int)groupSize.Z));
            CheckLimit(limits, "maxComputeInvocationsPerWorkgroup", checked((int)(groupSize.X * groupSize.Y * groupSize.Z)));
        }
        CheckLimit(limits, "maxVertexAttributes", locations.Count);
        CheckLimit(limits, "maxVertexBuffers", buffers.Count);
        CheckLimit(limits, "maxVertexBufferArrayStride", buffers.Count == 0 ? 0 : buffers.Max(buffer => buffer.Stride));
        CheckLimit(limits, "maxBindGroups", resources.Count == 0 ? 0 : checked((int)resources.Max(resource => resource.Contract.Set) + 1));
        CheckLimit(limits, "maxBindingsPerBindGroup", resources.Count == 0 ? 0 : resources.GroupBy(resource => resource.Contract.Set).Max(group => group.Count()));
        CheckLimit(limits, "maxUniformBufferBindingSize", resources.Where(resource => resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer).Select(resource => (int)resource.Contract.ByteSize).DefaultIfEmpty().Max());
        CheckLimit(limits, "maxDynamicUniformBuffersPerPipelineLayout", resources.Count(resource => resource.DynamicOffset && resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer));
        foreach (ShaderStageVisibility stage in new[] { ShaderStageVisibility.Vertex, ShaderStageVisibility.Fragment, ShaderStageVisibility.Compute })
        {
            CheckLimit(limits, "maxUniformBuffersPerShaderStage", resources.Count(resource => (resource.Visibility & stage) != 0 && resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer));
            CheckLimit(limits, "maxSampledTexturesPerShaderStage", resources.Count(resource => (resource.Visibility & stage) != 0 && resource.Contract.Kind == ShaderAbiResourceKind.SampledImage));
            CheckLimit(limits, "maxSamplersPerShaderStage", resources.Count(resource => (resource.Visibility & stage) != 0 && resource.Contract.Kind == ShaderAbiResourceKind.Sampler));
            CheckLimit(limits, "maxStorageBuffersPerShaderStage", resources.Count(resource => (resource.Visibility & stage) != 0 && resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer));
            CheckLimit(limits, "maxStorageTexturesPerShaderStage", resources.Count(resource => (resource.Visibility & stage) != 0 && resource.Contract.Kind == ShaderAbiResourceKind.StorageImage));
        }
        CheckLimit(limits, "maxStorageBufferBindingSize", resources.Where(resource => resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer).Select(resource => (int)resource.Contract.ByteSize).DefaultIfEmpty().Max());
        if (materialVariant is { } debugVariant &&
            debugVariant.Semantic.Semantic is (EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle))
        {
            int stride = debugVariant.Semantic.Semantic switch
            {
                EngineMaterialSemantic.DebugPoint => 16,
                EngineMaterialSemantic.DebugLine => 28,
                _ => 40,
            };
            CheckLimit(limits, "maxStorageBufferBindingSize", checked(65536 * stride));
            Require(resources.Count(resource => resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer) == 1,
                "debug primitive variants require exactly one raw storage binding");
        }
        if (materialVariant is { } uiVariant &&
            uiVariant.Semantic.Semantic is (EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UIQuadBatchedTexture or EngineMaterialSemantic.UITextBatchedBitmap))
        {
            bool text = uiVariant.Semantic.Semantic == EngineMaterialSemantic.UITextBatchedBitmap;
            bool texturedQuad = uiVariant.Semantic.Semantic == EngineMaterialSemantic.UIQuadBatchedTexture;
            Require(resources.Count == (text ? 7 : texturedQuad ? 7 : 4) &&
                resources.Count(resource => resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer) == (text || texturedQuad ? 4 : 3),
                "screen UI variants require their exact storage and atlas bindings");
            if (uiVariant.Semantic.Version == 2)
                Require(resources.Any(resource => resource is
                    {
                        BindingType: "uniform", DynamicOffset: true, RuntimeArray: false,
                        Contract: { Name: "View", PhysicalName: "view_0", Set: 0, Binding: 0,
                            Kind: ShaderAbiResourceKind.UniformBuffer, Owner: ShaderAbiResourceOwner.Engine,
                            Frequency: ShaderAbiFrequency.View, ByteSize: 80, Members.Length: 2 },
                    } && resource.Visibility == (ShaderStageVisibility.Vertex | ShaderStageVisibility.Fragment) &&
                    IsMatrixMember(resource.Contract.Members[0], "viewProjection_0", "ViewProjection") &&
                    resource.Contract.Members[1] is { PhysicalName: "outputMode_0", ProviderName: "UIOutputMode",
                        Offset: 64, Size: 16, PhysicalType: "vec4<f32>" }),
                    "canvas UI variants require the exact 80-byte camera projection and output-mode binding");
            else
                Require(resources.Any(resource => resource.Contract.Name == "View" &&
                    resource.Contract.Set == 0 && resource.Contract.Binding == 0 &&
                    resource.Contract.Kind == ShaderAbiResourceKind.UniformBuffer &&
                    resource.Contract.ByteSize == 64 && resource.Visibility == ShaderStageVisibility.Vertex),
                    "screen UI variants require the exact camera projection binding");
            if (text || texturedQuad)
                Require(resources.Any(resource => resource.Contract.Name == "Texture0" &&
                    resource.Contract.Set == 2 && resource.Contract.Binding == 0 &&
                    resource.BindingType == "texture-2d-float" && resource.Visibility == ShaderStageVisibility.Fragment) &&
                    resources.Any(resource => resource.Contract.Name == "Texture0" &&
                    resource.Contract.Set == 2 && resource.Contract.Binding == 1 &&
                    resource.BindingType == "filtering-sampler" && resource.Visibility == ShaderStageVisibility.Fragment),
                    "textured screen UI requires an exact image and sampler pair");
            CheckLimit(limits, "maxStorageBufferBindingSize", checked(65536 * (text ? 128 : 64)));
        }
        if (materialVariant?.Semantic == EngineMaterialSemanticIdentity.UICanvasSurfaceV1)
            ValidateCanvasSurfaceBindings(resources);
        CheckLimit(limits, "maxDynamicStorageBuffersPerPipelineLayout", resources.Count(resource => resource.DynamicOffset && resource.Contract.Kind == ShaderAbiResourceKind.StorageBuffer));
        string sourcePath = Property(descriptor, "source").ValueKind == JsonValueKind.String
            ? Text(descriptor, "source") : Text(Property(descriptor, "source"), "path");
        if (descriptor.TryGetProperty("sourceMap", out JsonElement map)) sourcePath = Text(map, "path");
        Require(RelativePath(sourcePath), "source path must be relative and normalized");
        return new ShaderProgramArtifact(identity, name, pass, sourcePath, artifact, schema, CoordinateConvention, vertex, fragment, compute,
            buffers.OrderBy(buffer => buffer.Slot).ToImmutableArray(), resources.ToImmutable(), limits.ToImmutable())
            { ComputeWorkgroupSize = workgroupSize, SourceLanguage = Text(descriptor, "sourceLanguage") };
    }

    private static void ValidateCanvasSurfaceBindings(ImmutableArray<ShaderStageResourceLayout>.Builder resources)
    {
        Require(resources.Count == 4 && resources.Any(resource => resource is
            {
                BindingType: "uniform", DynamicOffset: true, RuntimeArray: false, Visibility: ShaderStageVisibility.Vertex,
                Contract: { Name: "View", PhysicalName: "view_0", Set: 0, Binding: 0,
                    Kind: ShaderAbiResourceKind.UniformBuffer, Owner: ShaderAbiResourceOwner.Engine,
                    Frequency: ShaderAbiFrequency.View, ByteSize: 64, Members.Length: 1 },
            } && IsMatrixMember(resource.Contract.Members[0], "viewProjection_0", "ViewProjection")) &&
            resources.Any(resource => resource is
            {
                BindingType: "uniform", DynamicOffset: true, RuntimeArray: false, Visibility: ShaderStageVisibility.Vertex,
                Contract: { Name: "Object", PhysicalName: "object_0", Set: 0, Binding: 1,
                    Kind: ShaderAbiResourceKind.UniformBuffer, Owner: ShaderAbiResourceOwner.Engine,
                    Frequency: ShaderAbiFrequency.Object, ByteSize: 64, Members.Length: 1 },
            } && IsMatrixMember(resource.Contract.Members[0], "modelMatrix_0", "ModelMatrix")) &&
            resources.Any(resource => resource is
            {
                BindingType: "texture-2d-float", DynamicOffset: false, RuntimeArray: false, Visibility: ShaderStageVisibility.Fragment,
                Contract: { Name: "Texture0", PhysicalName: "materialTexture_0", Set: 1, Binding: 0,
                    Kind: ShaderAbiResourceKind.SampledImage, Owner: ShaderAbiResourceOwner.Material,
                    Frequency: ShaderAbiFrequency.Material, ByteSize: 0, Members.Length: 0 },
            }) && resources.Any(resource => resource is
            {
                BindingType: "filtering-sampler", DynamicOffset: false, RuntimeArray: false, Visibility: ShaderStageVisibility.Fragment,
                Contract: { Name: "Texture0", PhysicalName: "materialSampler_0", Set: 1, Binding: 1,
                    Kind: ShaderAbiResourceKind.Sampler, Owner: ShaderAbiResourceOwner.Material,
                    Frequency: ShaderAbiFrequency.Material, ByteSize: 0, Members.Length: 0 },
            }), "canvas surfaces require the exact view, object, texture, and sampler bindings");
    }

    private static bool IsMatrixMember(ShaderAbiMemberContract member, string physicalName, string providerName)
        => member.PhysicalName == physicalName && member.ProviderName == providerName &&
            member is { Offset: 0, Size: 64, PhysicalType: "mat4x4<f32>",
                MatrixOrder: ShaderAbiMatrixOrder.ColumnMajor, MatrixStride: 16 };

    internal static int VertexFormatBytes(string format) => format switch
    {
        "float32" => 4, "float32x2" => 8, "float32x3" => 12, "float32x4" => 16,
        _ => throw Invalid("unsupported vertex format '" + format + "'"),
    };

    internal static (int Alignment, int Size) MemberShape(string type) => type switch
    {
        "f32" or "u32" or "i32" => (4, 4),
        "vec2<f32>" or "vec2<u32>" or "vec2<i32>" => (8, 8),
        "vec3<f32>" or "vec3<u32>" or "vec3<i32>" => (16, 12),
        "vec4<f32>" or "vec4<u32>" or "vec4<i32>" => (16, 16),
        "mat4x4<f32>" => (16, 64),
        _ => throw Invalid("unsupported physical member type '" + type + "'"),
    };

    private static void CheckLimit(ImmutableDictionary<string, int>.Builder limits, string key, int minimum)
        => Require(minimum == 0 || limits.TryGetValue(key, out int value) && value >= minimum, "requiredLimits." + key + " must be at least " + minimum);
    private static bool RelativePath(string value) => value.Length is > 0 and <= 240 && !value.Contains('\\') && !value.StartsWith('/') && !value.Contains(':') && !value.Split('/').Any(part => part is "" or "." or "..");
    private static bool Identifier(string value, bool allowDash = false) => value.Length is > 0 and <= 64 && Regex.IsMatch(value, allowDash ? "^[a-zA-Z_][a-zA-Z0-9_-]*$" : "^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant);
    private static bool ProviderIdentifier(string value)
        => value.Length is > 0 and <= 256 && value.Split('.').All(segment => Identifier(segment));
    private static T EnumValue<T>(JsonElement value, string key) where T : struct, Enum
        => Enum.TryParse(Text(value, key), ignoreCase: false, out T result) && Enum.IsDefined(result) ? result : throw Invalid("unsupported " + key);
    private static int Int(JsonElement value, string key) => Property(value, key).TryGetInt32(out int result) ? result : throw Invalid(key + " must be an integer");
    private static int Bounded(JsonElement value, string key, int minimum, int maximum)
    {
        int result = Int(value, key);
        Require(result >= minimum && result <= maximum, key + " is outside the supported range");
        return result;
    }
    private static string Text(JsonElement value, string key) => Property(value, key, JsonValueKind.String).GetString()!;
    /// <summary>Reads an explicit semantic/profile declaration from a hash-owned descriptor.</summary>
    public static EngineMaterialVariantKey ReadMaterialVariantKey(JsonElement value, string pass, ShaderCompileTarget target)
    {
        ExactKeys(value, "semantic", "semanticVersion", "vertexProfile", "outputProfile");
        return BuildMaterialVariantKey(value, pass, target);
    }

    /// <summary>Reads a complete manifest reference without deriving selectors from names or paths.</summary>
    public static EngineMaterialVariantKey ReadMaterialVariantReferenceKey(JsonElement value)
    {
        ExactKeys(value, "semantic", "semanticVersion", "target", "pass", "vertexProfile", "outputProfile", "descriptorIdentity");
        Require(Text(value, "target") == nameof(ShaderCompileTarget.WebGPUWgsl), "unsupported material variant target");
        return BuildMaterialVariantKey(value, Text(value, "pass"), ShaderCompileTarget.WebGPUWgsl);
    }

    private static EngineMaterialVariantKey BuildMaterialVariantKey(JsonElement value, string pass, ShaderCompileTarget target)
    {
        string semanticName = Text(value, "semantic");
        Require(Enum.TryParse(semanticName, ignoreCase: false, out EngineMaterialSemantic semantic) &&
            semantic is EngineMaterialSemantic.StandardLitColor or EngineMaterialSemantic.StandardLitTexture or EngineMaterialSemantic.OpaqueShadowDepth or EngineMaterialSemantic.OpaquePointShadowDepth or EngineMaterialSemantic.OpaqueSpotShadowDepth or
                EngineMaterialSemantic.DebugPoint or EngineMaterialSemantic.DebugLine or EngineMaterialSemantic.DebugTriangle or
                EngineMaterialSemantic.UIQuadBatched or EngineMaterialSemantic.UIQuadBatchedTexture or EngineMaterialSemantic.UITextBatchedBitmap or
                EngineMaterialSemantic.UICanvasSurface or EngineMaterialSemantic.UberOutline or
                EngineMaterialSemantic.SkyboxGradient or EngineMaterialSemantic.SkyboxEquirectangular or
                EngineMaterialSemantic.SkyboxOctahedral or EngineMaterialSemantic.SkyboxCubemap or
                EngineMaterialSemantic.SkyboxDynamicProcedural,
            "unsupported material semantic");
        EngineMaterialVariantKey key = new(new EngineMaterialSemanticIdentity(semantic,
            Property(value, "semanticVersion", JsonValueKind.Number).GetInt32()), target, pass,
            Text(value, "vertexProfile"), Text(value, "outputProfile"));
        key.Validate();
        return key;
    }
    private static JsonElement Property(JsonElement value, string key, JsonValueKind? kind = null)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out JsonElement result))
            throw Invalid("missing property '" + key + "'");
        Require(kind is null || result.ValueKind == kind, key + " has an incorrect value kind");
        return result;
    }
    private static void ExactKeys(JsonElement value, params string[] keys)
        => Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == keys.Length && value.EnumerateObject().All(property => keys.Contains(property.Name, StringComparer.Ordinal)), "unexpected layout properties");
    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Require(keys.Add(property.Name), "duplicate property '" + property.Name + "'");
                CheckUnique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement element in value.EnumerateArray()) CheckUnique(element);
    }
    private static void Require(bool condition, string message) { if (!condition) throw Invalid(message); }
    private static InvalidDataException Invalid(string message) => new("Engine WGSL artifact: " + message + ".");
}
