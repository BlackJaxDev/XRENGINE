using System;
using System.Collections.Generic;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Core.Engine;

/// <summary>
/// Provides polymorphic serialization support for <see cref="TransformBase"/> so YAML clones
/// can faithfully reproduce the original runtime transform subtype.
/// </summary>
[YamlTypeConverter]
internal sealed class TransformBaseYamlTypeConverter : IYamlTypeConverter
{
    private const string TypeKey = "$type";
    private const string ValueKey = "$value";
    private const string ChildrenKey = "$children";

    public bool Accepts(Type type)
        => type == typeof(TransformBase);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar) && IsNullScalar(scalar))
            return null;

        parser.Consume<MappingStart>();

        Type? runtimeType = null;
        TransformBase? result = null;
        TransformBase[]? serializedChildren = null;
        bool hasSerializedChildren = false;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case TypeKey:
                    if (parser.TryConsume<Scalar>(out var typeScalar))
                        runtimeType = ResolveTransformType(typeScalar.Value);
                    break;

                case ValueKey:
                    runtimeType ??= typeof(Transform);
                    var deserialized = rootDeserializer(runtimeType);
                    if (deserialized is TransformBase transform)
                        result = transform;
                    else if (deserialized is not null)
                        throw new InvalidOperationException($"Deserialized type '{deserialized.GetType().FullName}' is not a {nameof(TransformBase)}.");
                    break;

                case ChildrenKey:
                    serializedChildren = rootDeserializer(typeof(TransformBase[])) as TransformBase[];
                    hasSerializedChildren = true;
                    break;

                default:
                    SkipNode(parser);
                    break;
            }
        }

        result ??= new Transform();

        if (hasSerializedChildren)
            ApplySerializedChildren(result, serializedChildren);

        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
            return;
        }

        var runtimeType = value.GetType();

        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar(TypeKey));
        emitter.Emit(new Scalar(runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name));
        emitter.Emit(new Scalar(ValueKey));
        serializer(value, runtimeType);
        if (value is TransformBase transform)
            EmitSerializedChildren(emitter, serializer, transform);
        emitter.Emit(new MappingEnd());
    }

    private static void EmitSerializedChildren(IEmitter emitter, ObjectSerializer serializer, TransformBase transform)
    {
        var children = CollectSerializableChildren(transform);
        if (children is null || children.Length == 0)
            return;

        emitter.Emit(new Scalar(ChildrenKey));
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        foreach (var child in children)
            serializer(child, typeof(TransformBase));
        emitter.Emit(new SequenceEnd());
    }

    private static TransformBase[]? CollectSerializableChildren(TransformBase transform)
    {
        List<TransformBase>? results = null;
        var source = transform.Children;
        lock (source)
        {
            foreach (var child in source)
            {
                if (child is null || child.SceneNode is not null)
                    continue;

                results ??= new List<TransformBase>(source.Count);
                results.Add(child);
            }
        }

        return results?.ToArray();
    }

    private static void ApplySerializedChildren(TransformBase parent, TransformBase[]? children)
    {
        if (children is null || children.Length == 0)
        {
            parent.Children = [];
            return;
        }

        parent.Children = [.. children];
    }

    private static Type? ResolveTransformType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        foreach (var candidate in TransformBase.TransformTypes)
            if ((candidate.AssemblyQualifiedName is string aqn && string.Equals(aqn, typeName, StringComparison.Ordinal)) || 
                (candidate.FullName is string fullName && string.Equals(fullName, typeName, StringComparison.Ordinal)))
                return candidate;
        

        return null;
    }

    private static bool IsNullScalar(Scalar scalar)
    {
        if (scalar.Value is null)
            return true;

        return scalar.Value.Length == 0
            || string.Equals(scalar.Value, "~", StringComparison.Ordinal)
            || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static void SkipNode(IParser parser)
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
                parser.Consume<Scalar>();
                SkipNode(parser);
            }
            return;
        }

        throw new YamlException("Unsupported YAML node encountered while skipping transform metadata.");
    }
}
