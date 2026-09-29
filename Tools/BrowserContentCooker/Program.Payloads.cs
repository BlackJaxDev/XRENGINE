using System.Text.Json;

namespace XREngine.Tools.BrowserContentCooker;

internal static partial class Program
{
    private static (int Instances, float[]? Camera) ValidatePayload(string kind, byte[] bytes, string[] dependencies, Dictionary<string, JsonElement> assets)
    {
        using JsonDocument document = ReadJson(bytes);
        JsonElement payload = document.RootElement;
        HashSet<string> used = new(StringComparer.Ordinal);
        int instances = 0;
        float[]? camera = null;
        switch (kind)
        {
            case "mesh":
                Members(payload, "vertices", "indices");
                JsonElement[] vertices = Array(payload, "vertices", 65535 * 5, 15);
                Require(vertices.Length % 5 == 0, "Mesh vertices require position XYZ and UV pairs.");
                foreach (JsonElement value in vertices) Number(value);
                JsonElement[] indices = Array(payload, "indices", 196605, 3);
                Require(((long)vertices.Length + indices.Length) * sizeof(float) <= JsonLimit, "Decoded mesh exceeds 1 MiB.");
                Require(indices.Length % 3 == 0, "Mesh indices must describe triangles.");
                foreach (JsonElement value in indices) Integer(value, 0, vertices.Length / 5 - 1);
                break;
            case "material":
                Members(payload, "tint", "texture", "alphaMode", "shading", "cullMode", "alphaCutoff", "castShadow", "receiveShadow");
                Floats(payload, "tint", 4, 0, 1);
                if (payload.GetProperty("texture").ValueKind != JsonValueKind.Null)
                    Reference(payload.GetProperty("texture"), "texture", assets, used);
                string alpha = Choice(payload, "alphaMode", "opaque", "masked", "transparent");
                Choice(payload, "shading", "unlit", "lambert");
                Choice(payload, "cullMode", "none", "front", "back");
                Number(payload.GetProperty("alphaCutoff"), 0, 1);
                bool castsShadow = Boolean(payload, "castShadow");
                Boolean(payload, "receiveShadow");
                Require(alpha != "transparent" || !castsShadow, "Transparent materials must disable shadow casting.");
                break;
            case "scene":
                Members(payload, "cameraView", "cameraProjection", "instances");
                Floats(payload, "cameraView", 16);
                Floats(payload, "cameraProjection", 16);
                camera = Array(payload, "cameraView", 16, 16).Concat(Array(payload, "cameraProjection", 16, 16)).Select(value => Number(value)).ToArray();
                foreach (JsonElement instance in Array(payload, "instances", 64))
                {
                    instances++;
                    Members(instance, "mesh", "material", "modelMatrix");
                    Reference(instance.GetProperty("mesh"), "mesh", assets, used);
                    Reference(instance.GetProperty("material"), "material", assets, used);
                    Floats(instance, "modelMatrix", 16);
                }
                break;
            default: throw new InvalidDataException("Unsupported JSON asset kind.");
        }
        Require(used.SetEquals(dependencies), "Declared dependencies must exactly match payload references.");
        return (instances, camera);
    }

    private static void Reference(JsonElement value, string kind, Dictionary<string, JsonElement> assets, HashSet<string> used)
    {
        string id = Identifier(value);
        Require(assets.TryGetValue(id, out JsonElement target) && target.GetProperty("kind").GetString() == kind, $"'{id}' must reference a '{kind}' asset.");
        if (kind == "texture")
            foreach (JsonElement variant in Array(target, "variants", 8, 1))
                Require(variant.GetProperty("normalConvention").GetString() == "none", "Color materials cannot reference normal-map textures.");
        used.Add(id);
    }

    private static void ValidateVariant(JsonElement variant, string kind)
    {
        if (kind == "texture")
        {
            Members(variant, "source", "encoding", "requiredFeatures", "width", "height", "format", "mipByteLengths", "normalConvention", "alphaMode");
            Choice(variant, "encoding", "raw");
            string format = Choice(variant, "format", "rgba8unorm", "rgba8unorm-srgb", "astc-4x4-unorm", "astc-4x4-unorm-srgb", "etc2-rgba8unorm", "etc2-rgba8unorm-srgb");
            string normal = Choice(variant, "normalConvention", "none", "tangent-y-positive");
            Require(normal == "none" || !format.EndsWith("-srgb", StringComparison.Ordinal), "Normal textures must use a linear format.");
            Choice(variant, "alphaMode", "straight");
            string? feature = format.StartsWith("astc-", StringComparison.Ordinal) ? "texture-compression-astc" : format.StartsWith("etc2-", StringComparison.Ordinal) ? "texture-compression-etc2" : null;
            JsonElement[] features = Array(variant, "requiredFeatures", 1);
            Require(feature is null ? features.Length == 0 : features.Length == 1 && features[0].ValueKind == JsonValueKind.String && features[0].GetString() == feature, "Texture feature requirements do not match the format.");
        }
        else
        {
            Members(variant, "source", "encoding", "requiredFeatures");
            Choice(variant, "encoding", "json");
            Require(Array(variant, "requiredFeatures", 0).Length == 0, "JSON assets must be feature independent.");
        }
        Require(variant.GetProperty("source").ValueKind == JsonValueKind.String, "Variant source must be a relative filename.");
    }

    private static void ValidateTextureBytes(JsonElement variant, int byteLength)
    {
        int width = Integer(variant.GetProperty("width"), 1, 8192);
        int height = Integer(variant.GetProperty("height"), 1, 8192);
        string format = variant.GetProperty("format").GetString()!;
        bool compressed = !format.StartsWith("rgba8", StringComparison.Ordinal);
        Require(!compressed || width % 4 == 0 && height % 4 == 0, "Compressed base dimensions must be multiples of four.");
        int mipCount = 1;
        for (int dimension = Math.Max(width, height); dimension > 1; dimension >>= 1) mipCount++;
        JsonElement[] mipLengths = Array(variant, "mipByteLengths", mipCount, mipCount);
        long total = 0;
        foreach (JsonElement mip in mipLengths)
        {
            long expected = compressed ? (long)((width + 3) / 4) * ((height + 3) / 4) * 16 : (long)width * height * 4;
            Require(Integer(mip, 1, RawLimit) == expected, "Texture mip byte count does not match its dimensions and format.");
            total += expected;
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
        }
        Require(total == byteLength, "Texture payload must concatenate all mip levels exactly, without padding.");
    }

    private static void ValidateTextureFallback(JsonElement[] variants)
    {
        HashSet<string> formats = new(StringComparer.Ordinal);
        JsonElement reference = variants[0];
        bool srgb = reference.GetProperty("format").GetString()!.EndsWith("-srgb", StringComparison.Ordinal);
        bool fallback = false;
        foreach (JsonElement variant in variants)
        {
            string format = variant.GetProperty("format").GetString()!;
            Require(formats.Add(format), "Duplicate texture format variant.");
            fallback |= format == (srgb ? "rgba8unorm-srgb" : "rgba8unorm");
            Require(format.EndsWith("-srgb", StringComparison.Ordinal) == srgb, "Texture variants must use the same color space.");
            foreach (string property in new[] { "width", "height", "normalConvention", "alphaMode" })
                Require(variant.GetProperty(property).ToString() == reference.GetProperty(property).ToString(), "Texture variants must retain dimensions and semantics.");
            Require(variant.GetProperty("mipByteLengths").GetArrayLength() == reference.GetProperty("mipByteLengths").GetArrayLength(), "Texture variants must retain mip count.");
        }
        Require(fallback, "Every texture requires an uncompressed RGBA8 fallback.");
    }
}
