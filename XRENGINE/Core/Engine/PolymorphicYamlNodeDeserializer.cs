using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// Deserializes polymorphic objects by honoring the <see cref="PolymorphicTypeGraphVisitor.TypeKey"/> discriminator.
    ///
    /// This runs for abstract/interface (and object) declared types.
    /// If __type is present in the mapping, it instantiates that concrete type.
    /// For legacy assets without __type, it provides minimal backwards-compatible fallbacks
    /// for common engine abstractions (ShaderVar, XRTexture).
    /// </summary>
    public sealed class PolymorphicYamlNodeDeserializer : INodeDeserializer
    {
        public bool Deserialize(
            IParser reader,
            Type expectedType,
            Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value,
            ObjectDeserializer rootDeserializer)
        {
            // XRAsset has its own replay/reference mechanism; don't interfere.
            if (typeof(XRAsset).IsAssignableFrom(expectedType))
            {
                value = null;
                return false;
            }

            if (!IsPolymorphicDeclaredType(expectedType))
            {
                value = null;
                return false;
            }

            // Only handle mapping nodes. Sequences/scalars should be handled by normal pipeline.
            if (!reader.Accept<MappingStart>(out _))
            {
                value = null;
                return false;
            }

            var events = CaptureNode(reader);

            // 1) Prefer explicit discriminator.
            if (TryGetTypeDiscriminator(events, out string? typeName) && TryResolveType(typeName!, out Type? concreteType))
            {
                if (!expectedType.IsAssignableFrom(concreteType))
                    throw new YamlException($"Polymorphic YAML type '{typeName}' is not assignable to '{expectedType.FullName}'.");

                value = nestedObjectDeserializer(new ReplayParser(events), concreteType);
                return true;
            }

            // 2) Back-compat fallback for legacy assets that predate __type.
            if (expectedType == typeof(XRTexture))
            {
                value = nestedObjectDeserializer(new ReplayParser(events), typeof(XRTexture2D));
                return true;
            }

            if (expectedType == typeof(ShaderVar))
            {
                Type inferred = InferLegacyShaderVarType(events) ?? typeof(ShaderFloat);
                value = nestedObjectDeserializer(new ReplayParser(events), inferred);
                return true;
            }

            // No discriminator and no known fallback.
            value = null;
            return false;
        }

        private static bool IsPolymorphicDeclaredType(Type expectedType)
        {
            if (expectedType == typeof(object))
                return true;

            return expectedType.IsAbstract || expectedType.IsInterface;
        }

        private static bool TryGetTypeDiscriminator(IReadOnlyList<ParsingEvent> events, out string? typeName)
        {
            typeName = null;
            if (events.Count < 2 || events[0] is not MappingStart || events[^1] is not MappingEnd)
                return false;

            for (int i = 1; i < events.Count - 1; i++)
            {
                if (events[i] is not Scalar key)
                    continue;

                if (!string.Equals(key.Value, PolymorphicTypeGraphVisitor.TypeKey, StringComparison.Ordinal))
                    continue;

                if (i + 1 >= events.Count - 1)
                    return false;

                if (events[i + 1] is Scalar valueScalar && !string.IsNullOrWhiteSpace(valueScalar.Value))
                {
                    typeName = valueScalar.Value;
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool TryResolveType(string typeName, out Type? type)
        {
            type = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (type is not null)
                return true;

            // Fall back to searching loaded assemblies by full name.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type is not null)
                    return true;
            }

            type = null;
            return false;
        }

        private static Type? InferLegacyShaderVarType(IReadOnlyList<ParsingEvent> events)
        {
            // Legacy format: mapping with keys like Name/Value/Color.
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

            if (!string.IsNullOrWhiteSpace(valueToken) && TryInferShaderVarFromValueToken(valueToken!, out var inferred))
            {
                if (ShaderVar.ShaderTypeAssociations.TryGetValue(inferred, out var clrType))
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

        private static IReadOnlyList<ParsingEvent> CaptureNode(IParser parser)
        {
            var events = new List<ParsingEvent>();
            CaptureNodeRecursive(parser, events);
            return events;
        }

        private static void CaptureNodeRecursive(IParser parser, ICollection<ParsingEvent> events)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                events.Add(scalar);
                return;
            }

            if (parser.TryConsume<AnchorAlias>(out var anchorAlias))
            {
                events.Add(anchorAlias);
                return;
            }

            if (parser.TryConsume<SequenceStart>(out var sequenceStart))
            {
                events.Add(sequenceStart);
                while (true)
                {
                    if (parser.TryConsume<SequenceEnd>(out var sequenceEnd))
                    {
                        events.Add(sequenceEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events);
                }
                return;
            }

            if (parser.TryConsume<MappingStart>(out var mappingStart))
            {
                events.Add(mappingStart);
                while (true)
                {
                    if (parser.TryConsume<MappingEnd>(out var mappingEnd))
                    {
                        events.Add(mappingEnd);
                        break;
                    }

                    CaptureNodeRecursive(parser, events); // Key
                    CaptureNodeRecursive(parser, events); // Value
                }
                return;
            }

            throw new YamlException("Unsupported YAML node encountered while capturing polymorphic data.");
        }

        private sealed class ReplayParser : IParser
        {
            private readonly Queue<ParsingEvent> _events;
            private ParsingEvent? _current;

            public ReplayParser(IEnumerable<ParsingEvent> events)
            {
                _events = new Queue<ParsingEvent>(events);
                MoveNext();
            }

            public ParsingEvent Current => _current ?? throw new InvalidOperationException("The parser is not positioned on an event.");

            public bool MoveNext()
            {
                if (_events.Count == 0)
                {
                    _current = null;
                    return false;
                }

                _current = _events.Dequeue();
                return true;
            }
        }
    }
}
