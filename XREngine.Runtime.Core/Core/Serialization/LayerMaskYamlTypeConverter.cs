using System;
using System.Globalization;
using XREngine.Scene;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class LayerMaskYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        Type? underlyingType = Nullable.GetUnderlyingType(type);
        return type == typeof(LayerMask) || underlyingType == typeof(LayerMask);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
            return ParseScalar(scalar);

        parser.Consume<MappingStart>();

        int value = default;
        bool hasValue = false;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            string? key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case nameof(LayerMask.Value):
                    if (rootDeserializer(typeof(int?)) is int parsedValue)
                    {
                        value = parsedValue;
                        hasValue = true;
                    }
                    else
                    {
                        SkipNode(parser);
                    }
                    break;

                default:
                    SkipNode(parser);
                    break;
            }
        }

        return hasValue ? new LayerMask(value) : default(LayerMask);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        LayerMask mask = value is LayerMask layerMask ? layerMask : default;

        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar(nameof(LayerMask.Value)));
        serializer(mask.Value, typeof(int));
        emitter.Emit(new MappingEnd());
    }

    internal static LayerMask ParseScalar(Scalar scalar)
    {
        if (scalar.Value is null
            || scalar.Value.Length == 0
            || string.Equals(scalar.Value, "~", StringComparison.Ordinal)
            || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        if (int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedValue))
            return new LayerMask(parsedValue);

        throw new YamlException($"Expected LayerMask scalar to be an integer, but found '{scalar.Value}'.");
    }

    internal static void SkipNode(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out _))
            return;

        if (parser.TryConsume<AnchorAlias>(out _))
            return;

        if (parser.TryConsume<SequenceStart>(out _))
        {
            while (!parser.TryConsume<SequenceEnd>(out _))
                SkipNode(parser);

            return;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                SkipNode(parser);
                SkipNode(parser);
            }

            return;
        }

        throw new YamlException("Unsupported YAML node encountered while skipping LayerMask metadata.");
    }
}

internal sealed class LayerMaskYamlNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(IParser reader,
                            Type expectedType,
                            Func<IParser, Type, object?> nestedObjectDeserializer,
                            out object? value,
                            ObjectDeserializer rootDeserializer)
    {
        Type? underlyingType = Nullable.GetUnderlyingType(expectedType);
        bool accepts = expectedType == typeof(LayerMask) || underlyingType == typeof(LayerMask);
        if (!accepts)
        {
            value = null;
            return false;
        }

        if (reader.TryConsume<Scalar>(out Scalar? scalar))
        {
            value = LayerMaskYamlTypeConverter.ParseScalar(scalar);
            return true;
        }

        if (!reader.Accept<MappingStart>(out _))
        {
            value = null;
            return false;
        }

        reader.Consume<MappingStart>();

        int maskValue = default;
        bool hasValue = false;
        while (!reader.TryConsume<MappingEnd>(out _))
        {
            string? key = reader.Consume<Scalar>().Value;
            switch (key)
            {
                case nameof(LayerMask.Value):
                    if (rootDeserializer(typeof(int?)) is int parsedValue)
                    {
                        maskValue = parsedValue;
                        hasValue = true;
                    }
                    else
                    {
                        LayerMaskYamlTypeConverter.SkipNode(reader);
                    }
                    break;

                default:
                    LayerMaskYamlTypeConverter.SkipNode(reader);
                    break;
            }
        }

        value = hasValue ? new LayerMask(maskValue) : default(LayerMask);
        return true;
    }
}