using YamlDotNet.Core.Events;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine;

/// <summary>Installs renderer-owned compatibility mappings into the lower YAML registry.</summary>
public static class RenderingPolymorphicYamlFallbacks
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            leases.Add(PolymorphicYamlFallbackRegistry.Install(typeof(XRTexture), typeof(XRTexture2D)));
            leases.Add(PolymorphicYamlFallbackRegistry.Install(typeof(ShaderVar), ResolveLegacyShaderVarFallback));
        });

    private static Type? ResolveLegacyShaderVarFallback(IReadOnlyList<ParsingEvent> events)
    {
        Type? inferredType = InferLegacyShaderVarType(events);
        return inferredType ?? typeof(ShaderFloat);
    }

    private static Type? InferLegacyShaderVarType(IReadOnlyList<ParsingEvent> events)
    {
        if (events.Count < 2 || events[0] is not MappingStart || events[^1] is not MappingEnd)
            return null;

        string? valueToken = null;
        bool hasColorKey = false;

        for (int i = 1; i < events.Count - 1; i++)
        {
            if (events[i] is not Scalar key)
                continue;

            if (key.Value == "Color")
                hasColorKey = true;

            if (key.Value == "Value" && i + 1 < events.Count - 1 && events[i + 1] is Scalar value)
                valueToken = value.Value;
        }

        if (!string.IsNullOrWhiteSpace(valueToken)
            && TryInferShaderVarFromValueToken(valueToken, out EShaderVarType inferred)
            && ShaderVar.ShaderTypeAssociations.TryGetValue(inferred, out Type? clrType))
        {
            return clrType;
        }

        return hasColorKey ? typeof(ShaderVector3) : null;
    }

    private static bool TryInferShaderVarFromValueToken(string token, out EShaderVarType type)
    {
        token = token.Trim();
        if (bool.TryParse(token, out _))
        {
            type = EShaderVarType._bool;
            return true;
        }

        string[] parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            type = int.TryParse(parts[0], out _)
                ? EShaderVarType._int
                : uint.TryParse(parts[0], out _)
                    ? EShaderVarType._uint
                    : double.TryParse(parts[0], out _)
                        ? EShaderVarType._double
                        : EShaderVarType._float;
            return true;
        }

        type = parts.Length switch
        {
            2 => EShaderVarType._vec2,
            3 => EShaderVarType._vec3,
            4 => EShaderVarType._vec4,
            16 => EShaderVarType._mat4,
            _ => EShaderVarType._float,
        };
        return true;
    }

}
