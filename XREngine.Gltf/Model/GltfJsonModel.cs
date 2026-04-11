using System.Text.Json;
using System.Text.Json.Serialization;

namespace XREngine.Gltf;

public sealed class GltfRoot
{
    [JsonPropertyName("asset")]
    public GltfAssetInfo Asset { get; set; } = new();

    [JsonPropertyName("scene")]
    public int? DefaultScene { get; set; }

    [JsonPropertyName("scenes")]
    public List<GltfScene> Scenes { get; set; } = [];

    [JsonPropertyName("nodes")]
    public List<GltfNode> Nodes { get; set; } = [];

    [JsonPropertyName("meshes")]
    public List<GltfMesh> Meshes { get; set; } = [];

    [JsonPropertyName("accessors")]
    public List<GltfAccessor> Accessors { get; set; } = [];

    [JsonPropertyName("bufferViews")]
    public List<GltfBufferView> BufferViews { get; set; } = [];

    [JsonPropertyName("buffers")]
    public List<GltfBuffer> Buffers { get; set; } = [];

    [JsonPropertyName("images")]
    public List<GltfImage> Images { get; set; } = [];

    [JsonPropertyName("textures")]
    public List<GltfTexture> Textures { get; set; } = [];

    [JsonPropertyName("samplers")]
    public List<GltfSampler> Samplers { get; set; } = [];

    [JsonPropertyName("materials")]
    public List<GltfMaterial> Materials { get; set; } = [];

    [JsonPropertyName("skins")]
    public List<GltfSkin> Skins { get; set; } = [];

    [JsonPropertyName("animations")]
    public List<GltfAnimation> Animations { get; set; } = [];

    [JsonPropertyName("extensionsUsed")]
    public List<string> ExtensionsUsed { get; set; } = [];

    [JsonPropertyName("extensionsRequired")]
    public List<string> ExtensionsRequired { get; set; } = [];

    public int ResolveDefaultSceneIndex()
        => DefaultScene is int sceneIndex && sceneIndex >= 0 && sceneIndex < Scenes.Count
            ? sceneIndex
            : 0;
}

public sealed class GltfAssetInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("generator")]
    public string? Generator { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }
}

public sealed class GltfScene
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nodes")]
    public List<int> Nodes { get; set; } = [];

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfNode
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("mesh")]
    public int? Mesh { get; set; }

    [JsonPropertyName("skin")]
    public int? Skin { get; set; }

    [JsonPropertyName("camera")]
    public int? Camera { get; set; }

    [JsonPropertyName("children")]
    public List<int> Children { get; set; } = [];

    [JsonPropertyName("translation")]
    public float[]? Translation { get; set; }

    [JsonPropertyName("rotation")]
    public float[]? Rotation { get; set; }

    [JsonPropertyName("scale")]
    public float[]? Scale { get; set; }

    [JsonPropertyName("matrix")]
    public float[]? Matrix { get; set; }

    [JsonPropertyName("weights")]
    public float[]? Weights { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfMesh
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("primitives")]
    public List<GltfPrimitive> Primitives { get; set; } = [];

    [JsonPropertyName("weights")]
    public float[]? Weights { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfPrimitive
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, int> Attributes { get; set; } = [];

    [JsonPropertyName("indices")]
    public int? Indices { get; set; }

    [JsonPropertyName("material")]
    public int? Material { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; } = 4;

    [JsonPropertyName("targets")]
    public List<Dictionary<string, int>> Targets { get; set; } = [];

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    public bool TryGetAttributeAccessor(string attributeName, out int accessorIndex)
        => Attributes.TryGetValue(attributeName, out accessorIndex);
}

public sealed class GltfAccessor
{
    [JsonPropertyName("bufferView")]
    public int? BufferView { get; set; }

    [JsonPropertyName("byteOffset")]
    public int ByteOffset { get; set; }

    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }

    [JsonPropertyName("normalized")]
    public bool Normalized { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "SCALAR";

    [JsonPropertyName("sparse")]
    public GltfSparseAccessor? Sparse { get; set; }

    [JsonPropertyName("min")]
    public JsonElement? Min { get; set; }

    [JsonPropertyName("max")]
    public JsonElement? Max { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfSparseAccessor
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("indices")]
    public GltfSparseAccessorIndices Indices { get; set; } = new();

    [JsonPropertyName("values")]
    public GltfSparseAccessorValues Values { get; set; } = new();
}

public sealed class GltfSparseAccessorIndices
{
    [JsonPropertyName("bufferView")]
    public int BufferView { get; set; }

    [JsonPropertyName("byteOffset")]
    public int ByteOffset { get; set; }

    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }
}

public sealed class GltfSparseAccessorValues
{
    [JsonPropertyName("bufferView")]
    public int BufferView { get; set; }

    [JsonPropertyName("byteOffset")]
    public int ByteOffset { get; set; }
}

public sealed class GltfBufferView
{
    [JsonPropertyName("buffer")]
    public int Buffer { get; set; }

    [JsonPropertyName("byteOffset")]
    public int ByteOffset { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("byteStride")]
    public int? ByteStride { get; set; }

    [JsonPropertyName("target")]
    public int? Target { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfBuffer
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfImage
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("bufferView")]
    public int? BufferView { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfTexture
{
    [JsonPropertyName("sampler")]
    public int? Sampler { get; set; }

    [JsonPropertyName("source")]
    public int? Source { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfSampler
{
    [JsonPropertyName("magFilter")]
    public int? MagFilter { get; set; }

    [JsonPropertyName("minFilter")]
    public int? MinFilter { get; set; }

    [JsonPropertyName("wrapS")]
    public int? WrapS { get; set; }

    [JsonPropertyName("wrapT")]
    public int? WrapT { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class GltfMaterial
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("pbrMetallicRoughness")]
    public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }

    [JsonPropertyName("normalTexture")]
    public GltfNormalTextureInfo? NormalTexture { get; set; }

    [JsonPropertyName("occlusionTexture")]
    public GltfOcclusionTextureInfo? OcclusionTexture { get; set; }

    [JsonPropertyName("emissiveTexture")]
    public GltfTextureInfo? EmissiveTexture { get; set; }

    [JsonPropertyName("emissiveFactor")]
    public float[]? EmissiveFactor { get; set; }

    [JsonPropertyName("alphaMode")]
    public string? AlphaMode { get; set; }

    [JsonPropertyName("alphaCutoff")]
    public float? AlphaCutoff { get; set; }

    [JsonPropertyName("doubleSided")]
    public bool? DoubleSided { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfPbrMetallicRoughness
{
    [JsonPropertyName("baseColorFactor")]
    public float[]? BaseColorFactor { get; set; }

    [JsonPropertyName("baseColorTexture")]
    public GltfTextureInfo? BaseColorTexture { get; set; }

    [JsonPropertyName("metallicFactor")]
    public float? MetallicFactor { get; set; }

    [JsonPropertyName("roughnessFactor")]
    public float? RoughnessFactor { get; set; }

    [JsonPropertyName("metallicRoughnessTexture")]
    public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
}

public class GltfTextureInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("texCoord")]
    public int TexCoord { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfNormalTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("scale")]
    public float? Scale { get; set; }
}

public sealed class GltfOcclusionTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("strength")]
    public float? Strength { get; set; }
}

public sealed class GltfSkin
{
    [JsonPropertyName("inverseBindMatrices")]
    public int? InverseBindMatrices { get; set; }

    [JsonPropertyName("skeleton")]
    public int? Skeleton { get; set; }

    [JsonPropertyName("joints")]
    public List<int> Joints { get; set; } = [];

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfAnimation
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("channels")]
    public List<GltfAnimationChannel> Channels { get; set; } = [];

    [JsonPropertyName("samplers")]
    public List<GltfAnimationSampler> Samplers { get; set; } = [];

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfAnimationChannel
{
    [JsonPropertyName("sampler")]
    public int Sampler { get; set; }

    [JsonPropertyName("target")]
    public GltfAnimationTarget Target { get; set; } = new();

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class GltfAnimationTarget
{
    [JsonPropertyName("node")]
    public int? Node { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

public sealed class GltfAnimationSampler
{
    [JsonPropertyName("input")]
    public int Input { get; set; }

    [JsonPropertyName("output")]
    public int Output { get; set; }

    [JsonPropertyName("interpolation")]
    public string? Interpolation { get; set; }

    [JsonPropertyName("extras")]
    public JsonElement? Extras { get; set; }

    [JsonPropertyName("extensions")]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}