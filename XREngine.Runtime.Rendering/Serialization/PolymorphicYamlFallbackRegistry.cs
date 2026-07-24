using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Core.Events;

namespace XREngine;

/// <summary>
/// Central registry for legacy YAML polymorphic fallbacks used when a mapping omits
/// the standard __type discriminator.
/// </summary>
internal static class PolymorphicYamlFallbackRegistry
{
    private delegate bool FallbackResolver(IReadOnlyList<ParsingEvent> events, out Type? concreteType);

    private static readonly ConcurrentDictionary<Type, FallbackResolver> Resolvers = new();

    static PolymorphicYamlFallbackRegistry()
    {
        Register(typeof(XRTexture), static (IReadOnlyList<ParsingEvent> _, out Type? concreteType) =>
        {
            concreteType = typeof(XRTexture2D);
            return true;
        });

        Register(typeof(ShaderVar), ResolveLegacyShaderVarFallback);
    }

    private static void Register(Type expectedType, FallbackResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(resolver);

        Resolvers[expectedType] = resolver;
    }

    public static void Register(Type expectedType, Type concreteType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(concreteType);

        Resolvers[expectedType] = (IReadOnlyList<ParsingEvent> _, out Type? resolvedType) =>
        {
            resolvedType = concreteType;
            return true;
        };
    }

    public static void Register(Type expectedType, Func<IReadOnlyList<ParsingEvent>, Type?> resolver)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(resolver);

        Resolvers[expectedType] = (IReadOnlyList<ParsingEvent> events, out Type? concreteType) =>
        {
            concreteType = resolver(events);
            return concreteType is not null;
        };
    }

    public static bool TryResolve(Type expectedType, IReadOnlyList<ParsingEvent> events, out Type? concreteType)
    {
        ArgumentNullException.ThrowIfNull(expectedType);
        ArgumentNullException.ThrowIfNull(events);

        if (Resolvers.TryGetValue(expectedType, out FallbackResolver? resolver))
            return resolver(events, out concreteType);

        concreteType = null;
        return false;
    }

    private static bool ResolveLegacyShaderVarFallback(IReadOnlyList<ParsingEvent> events, out Type? concreteType)
    {
        concreteType = InferLegacyShaderVarType(events) ?? typeof(ShaderFloat);
        return true;
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

            if (key.Value != "Value")
                continue;

            if (i + 1 < events.Count - 1 && events[i + 1] is Scalar valScalar)
                valueToken = valScalar.Value;
        }

        if (!string.IsNullOrWhiteSpace(valueToken) && TryInferShaderVarFromValueToken(valueToken, out var inferred))
        {
            if (ShaderVar.ShaderTypeAssociations.TryGetValue(inferred, out Type? clrType))
                return clrType;
        }

        if (hasColorKey)
            return typeof(ShaderVector3);

        return null;
    }

    private static bool TryInferShaderVarFromValueToken(string token, out EShaderVarType type)
    {
        token = token.Trim();

        if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "false", StringComparison.OrdinalIgnoreCase))
        {
            type = EShaderVarType._bool;
            return true;
        }

        string[] parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            if (int.TryParse(parts[0], out _))
            {
                type = EShaderVarType._int;
                return true;
            }

            if (uint.TryParse(parts[0], out _))
            {
                type = EShaderVarType._uint;
                return true;
            }

            if (float.TryParse(parts[0], out _))
            {
                type = EShaderVarType._float;
                return true;
            }

            if (double.TryParse(parts[0], out _))
            {
                type = EShaderVarType._double;
                return true;
            }

            type = EShaderVarType._float;
            return true;
        }

        type = parts.Length switch
        {
            2 => EShaderVarType._vec2,
            3 => EShaderVarType._vec3,
            4 => EShaderVarType._vec4,
            16 => EShaderVarType._mat4,
            _ => EShaderVarType._float
        };

        return true;
    }
}