using System.Text.Json;

namespace XREngine.ShaderTranslation.Compilation;

/// <summary>
/// Holds the parts of Slang reflection JSON that GLSL post-processing needs.
/// </summary>
public sealed class SlangStageReflection
{
    /// <summary>Gets the vertex inputs. It is empty for other stages.</summary>
    public List<VertexInputReflection> VertexInputs { get; } = [];

    /// <summary>Gets the names of separate texture objects.</summary>
    public List<string> Textures { get; } = [];

    /// <summary>Gets the names of separate sampler objects.</summary>
    public List<string> Samplers { get; } = [];

    /// <summary>Reads Slang <c>-reflection-json</c> output.</summary>
    public static SlangStageReflection Parse(string json)
    {
        SlangStageReflection reflection = new();
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("parameters", out JsonElement parameters))
        {
            foreach (JsonElement parameter in parameters.EnumerateArray())
            {
                if (!parameter.TryGetProperty("name", out JsonElement name) ||
                    !parameter.TryGetProperty("type", out JsonElement type) ||
                    !type.TryGetProperty("kind", out JsonElement kind))
                    continue;

                string kindText = kind.GetString() ?? string.Empty;
                if (kindText == "samplerState")
                    reflection.Samplers.Add(name.GetString()!);
                else if (kindText == "resource" && IsSeparateTexture(type))
                    reflection.Textures.Add(name.GetString()!);
            }
        }

        if (root.TryGetProperty("entryPoints", out JsonElement entryPoints))
        {
            foreach (JsonElement entryPoint in entryPoints.EnumerateArray())
            {
                if (!entryPoint.TryGetProperty("stage", out JsonElement stage) || stage.GetString() != "vertex")
                    continue;
                if (!entryPoint.TryGetProperty("parameters", out JsonElement entryParameters))
                    continue;

                foreach (JsonElement parameter in entryParameters.EnumerateArray())
                    CollectVaryingInputs(parameter, 0, reflection.VertexInputs);
            }
        }

        return reflection;
    }

    private static bool IsSeparateTexture(JsonElement type)
    {
        // A combined texture-sampler (Sampler2D) has "combined": true and needs no pairing.
        if (type.TryGetProperty("combined", out JsonElement combined) && combined.ValueKind == JsonValueKind.True)
            return false;
        return type.TryGetProperty("baseShape", out JsonElement shape) && (shape.GetString() ?? string.Empty).StartsWith("texture", StringComparison.Ordinal);
    }

    private static void CollectVaryingInputs(JsonElement element, int baseLocation, List<VertexInputReflection> inputs)
    {
        int location = baseLocation;
        if (element.TryGetProperty("binding", out JsonElement binding))
        {
            if (!binding.TryGetProperty("kind", out JsonElement bindingKind) || bindingKind.GetString() != "varyingInput")
                return;
            if (binding.TryGetProperty("index", out JsonElement index))
                location = index.GetInt32();
        }

        if (element.TryGetProperty("semanticName", out JsonElement semanticName))
        {
            int semanticIndex = element.TryGetProperty("semanticIndex", out JsonElement indexElement) ? indexElement.GetInt32() : 0;
            inputs.Add(new VertexInputReflection(
                (semanticName.GetString() ?? string.Empty).ToUpperInvariant(),
                semanticIndex,
                location,
                GetComponentCount(element)));
            return;
        }

        if (element.TryGetProperty("type", out JsonElement type) &&
            type.TryGetProperty("fields", out JsonElement fields))
        {
            foreach (JsonElement field in fields.EnumerateArray())
                CollectVaryingInputs(field, location, inputs);
        }
    }

    private static int GetComponentCount(JsonElement element)
    {
        if (!element.TryGetProperty("type", out JsonElement type))
            return 1;
        return type.TryGetProperty("elementCount", out JsonElement count) ? count.GetInt32() : 1;
    }
}
