using System;
using System.Collections.Generic;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

public sealed class XRShaderScalarYamlNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        AssetManager.EnsureYamlAssetRuntimeSupported();

        value = null;

        if (expectedType != typeof(XRShader) || !reader.Accept<Scalar>(out var scalar))
            return false;

        return XRAssetDeserializer.TryHandleScalarXRAsset(reader, expectedType, scalar, out value);
    }
}

public sealed class XRShaderCollectionYamlNodeDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value,
        ObjectDeserializer rootDeserializer)
    {
        AssetManager.EnsureYamlAssetRuntimeSupported();

        value = null;

        if (!XRShaderCollectionYamlTypeConverter.AcceptsShaderCollection(expectedType)
            || (!reader.Accept<Scalar>(out _) && !reader.Accept<SequenceStart>(out _)))
        {
            return false;
        }

        value = XRShaderCollectionYamlTypeConverter.ReadShaderCollection(reader, expectedType, rootDeserializer);
        return true;
    }
}

[YamlTypeConverter]
public sealed class XRShaderCollectionYamlTypeConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => AcceptsShaderCollection(type);

    internal static bool AcceptsShaderCollection(Type type)
    {
        if (type == typeof(XRShader[]))
            return true;

        if (!type.IsGenericType || type.GetGenericArguments()[0] != typeof(XRShader))
            return false;

        Type genericType = type.GetGenericTypeDefinition();
        return genericType == typeof(EventList<>)
            || genericType == typeof(List<>)
            || genericType == typeof(IList<>)
            || genericType == typeof(ICollection<>)
            || genericType == typeof(IEnumerable<>)
            || genericType == typeof(IReadOnlyList<>)
            || genericType == typeof(IReadOnlyCollection<>);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => ReadShaderCollection(parser, type, rootDeserializer);

    internal static object? ReadShaderCollection(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        AssetManager.EnsureYamlAssetRuntimeSupported();

        List<XRShader> shaders = [];

        if (parser.Accept<Scalar>(out var scalar))
        {
            if (XRAssetDeserializer.IsNullScalar(scalar))
            {
                parser.Consume<Scalar>();
                return type == typeof(EventList<XRShader>) ? new EventList<XRShader>() : null;
            }

            AddScalarShaderIfResolved(parser, scalar, shaders);
            return CreateCollection(type, shaders);
        }

        if (!parser.TryConsume<SequenceStart>(out _))
            throw new YamlException($"Expected a sequence while deserializing an {nameof(XRShader)} collection.");

        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            if (parser.Accept<Scalar>(out scalar))
            {
                AddScalarShaderIfResolved(parser, scalar, shaders);
                continue;
            }

            object? item = rootDeserializer(typeof(XRShader));
            if (item is XRShader shader)
                shaders.Add(shader);
        }

        return CreateCollection(type, shaders);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));

        if (value is IEnumerable<XRShader> shaders)
        {
            foreach (XRShader? shader in shaders)
            {
                if (shader is not null)
                    serializer(shader, typeof(XRShader));
            }
        }

        emitter.Emit(new SequenceEnd());
    }

    private static object CreateCollection(Type type, List<XRShader> shaders)
    {
        if (type == typeof(XRShader[]))
            return shaders.ToArray();

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(EventList<>))
            return new EventList<XRShader>(shaders);

        return shaders;
    }

    private static void AddScalarShaderIfResolved(IParser parser, Scalar scalar, List<XRShader> shaders)
    {
        if (XRAssetDeserializer.TryHandleScalarXRAsset(parser, typeof(XRShader), scalar, out object? value)
            && value is XRShader shader)
        {
            shaders.Add(shader);
        }
    }
}
