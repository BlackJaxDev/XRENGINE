using System;
using System.Collections.Generic;
using XREngine.Core;
using XREngine.Core.Files;
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
    /// If __type is absent, property-level defaults from <see cref="YamlDefaultTypeAttribute"/>
    /// are honored first, followed by any legacy fallbacks from <see cref="PolymorphicYamlFallbackRegistry"/>.
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
            AssetManager.EnsureYamlAssetRuntimeSupported();

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
            if (TryGetTypeDiscriminator(events, out string? typeName))
            {
                if (TryResolveType(typeName!, out Type? concreteType))
                {
                    if (!expectedType.IsAssignableFrom(concreteType))
                        throw new YamlException($"Polymorphic YAML type '{typeName}' is not assignable to '{expectedType.FullName}'.");

                    value = nestedObjectDeserializer(new ReplayParser(events), concreteType);
                    return true;
                }

                // __type was present but couldn't be resolved. Throw a clear error rather than
                // silently returning false (which would leave the parser in a corrupt state since
                // CaptureNode already consumed the mapping events).
                throw new YamlException(
                    $"Polymorphic YAML discriminator '__type: {typeName}' could not be resolved to a known CLR type " +
                    $"(expected base type: '{expectedType.FullName}'). Ensure the type is loaded and the name is correct.");
            }

            // 2) Property-level default concrete type when __type is omitted.
            if (YamlDefaultTypeContext.TryConsumeRead(expectedType, out Type? propertyDefaultType))
            {
                value = nestedObjectDeserializer(new ReplayParser(events), propertyDefaultType!);
                return true;
            }

            // 3) Back-compat fallback for legacy assets that predate __type.
            if (PolymorphicYamlFallbackRegistry.TryResolve(expectedType, events, out Type? fallbackType))
            {
                value = nestedObjectDeserializer(new ReplayParser(events), fallbackType!);
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
            typeName = XRTypeRedirectRegistry.RewriteTypeName(typeName);
            type = AotRuntimeMetadataStore.ResolveType(typeName);
            if (type is not null)
                return true;

            type = null;
            return false;
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
