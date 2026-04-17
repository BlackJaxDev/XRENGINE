using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Animation;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Core.Engine;

/// <summary>
/// Handles TransformBase YAML using an explicit wrapper so transform graphs remain stable,
/// while allowing property-level default types to omit the transform type marker when it matches.
/// </summary>
[YamlTypeConverter]
internal sealed class TransformBaseYamlTypeConverter : IYamlTypeConverter
{
    private const string TypeKey = "$type";
    private const string ValueKey = "$value";
    private const string ChildrenKey = "$children";

    [ThreadStatic]
    private static bool _skip;

    public bool Accepts(Type type)
        => !_skip && typeof(TransformBase).IsAssignableFrom(type);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        bool isReferenceMode = YamlTransformReferenceContext.ConsumeRead();

        if (parser.TryConsume<Scalar>(out var scalar) && IsNullScalar(scalar))
            return null;

        if (isReferenceMode)
            return ReadReferenceTransform(parser);

        bool isInlineMapping = parser.TryConsume<MappingStart>(out _);
        if (!isInlineMapping && !parser.Accept<Scalar>(out _))
            parser.Consume<MappingStart>();

        Type? runtimeType = null;
        TransformBase? result = null;
        TransformBase[]? serializedChildren = null;
        bool hasSerializedChildren = false;
        Guid referenceId = Guid.Empty;
        string? referenceName = null;
        bool hasReferenceFields = false;
        bool hasExplicitValue = false;

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
                    hasExplicitValue = true;
                    if (runtimeType is null && !YamlDefaultTypeContext.TryConsumeRead(type, out runtimeType))
                        runtimeType = typeof(Transform);
                    if (runtimeType == typeof(Transform))
                    {
                        result = ReadConcreteTransform(parser, rootDeserializer);
                        break;
                    }

                    bool previous = _skip;
                    _skip = true;
                    object? deserialized;
                    try
                    {
                        Type concreteType = runtimeType is null ? typeof(Transform) : runtimeType;
                        deserialized = rootDeserializer(concreteType);
                    }
                    finally
                    {
                        _skip = previous;
                    }

                    if (deserialized is TransformBase transform)
                        result = transform;
                    else if (deserialized is not null)
                        throw new InvalidOperationException($"Deserialized type '{deserialized.GetType().FullName}' is not a {nameof(TransformBase)}.");
                    break;

                case "ID":
                    if (parser.TryConsume<Scalar>(out var idScalar) && Guid.TryParse(idScalar.Value, out Guid parsedReferenceId))
                    {
                        referenceId = parsedReferenceId;
                        hasReferenceFields = true;
                    }
                    else
                    {
                        SkipNode(parser);
                    }
                    break;

                case "Name":
                    if (parser.TryConsume<Scalar>(out var nameScalar))
                    {
                        referenceName = nameScalar.Value;
                        hasReferenceFields = true;
                    }
                    else
                    {
                        SkipNode(parser);
                    }
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

        if (result is null && hasReferenceFields && !hasExplicitValue && !hasSerializedChildren)
            return CreateReferencePlaceholder(referenceId, referenceName);

        result ??= new Transform();

        if (hasSerializedChildren)
            ApplySerializedChildren(result, serializedChildren);

        return result;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        bool isReferenceMode = YamlTransformReferenceContext.ConsumeWrite();

        if (value is null)
        {
            emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
            return;
        }

        if (isReferenceMode && value is TransformBase referenceTransform)
        {
            EmitReferenceTransform(emitter, referenceTransform);
            return;
        }

        var runtimeType = value.GetType();

        Type? defaultType = YamlDefaultTypeContext.ConsumeWriteDefaultType();

        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        if (defaultType is null || runtimeType != defaultType)
        {
            emitter.Emit(new Scalar(TypeKey));
            emitter.Emit(new Scalar(runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name));
        }
        emitter.Emit(new Scalar(ValueKey));
        bool previous = _skip;
        _skip = true;
        try
        {
            serializer(value, runtimeType);
        }
        finally
        {
            _skip = previous;
        }

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

    private static Transform ReadConcreteTransform(IParser parser, ObjectDeserializer rootDeserializer)
    {
        bool isInlineMapping = parser.TryConsume<MappingStart>(out _);
        if (!isInlineMapping && !parser.Accept<Scalar>(out _))
            parser.Consume<MappingStart>();

        Transform transform = new();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            string? key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case "Scale":
                    if (rootDeserializer(typeof(Vector3?)) is Vector3 scale)
                        transform.Scale = scale;
                    break;

                case "Translation":
                    if (rootDeserializer(typeof(Vector3?)) is Vector3 translation)
                        transform.Translation = translation;
                    break;

                case "Rotation":
                    if (rootDeserializer(typeof(Quaternion?)) is Quaternion rotation)
                        transform.Rotation = rotation;
                    break;

                case "Order":
                    if (rootDeserializer(typeof(ETransformOrder)) is ETransformOrder order)
                        transform.Order = order;
                    break;

                case "Name":
                    if (parser.TryConsume<Scalar>(out Scalar? nameScalar))
                        transform.Name = nameScalar.Value;
                    else
                        SkipNode(parser);
                    break;

                case "ID":
                    if (parser.TryConsume<Scalar>(out Scalar? idScalar) && Guid.TryParse(idScalar.Value, out Guid serializedReferenceId))
                        transform.SerializedReferenceId = serializedReferenceId;
                    else
                        SkipNode(parser);
                    break;

                default:
                    SkipNode(parser);
                    break;
            }
        }

        return transform;
    }

    private static void EmitReferenceTransform(IEmitter emitter, TransformBase transform)
    {
        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar("ID"));
        emitter.Emit(new Scalar(transform.EffectiveSerializedReferenceId.ToString()));
        emitter.Emit(new MappingEnd());
    }

    private static TransformBase ReadReferenceTransform(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            if (Guid.TryParse(scalar.Value, out Guid scalarId))
                return CreateReferencePlaceholder(scalarId, name: null);

            throw new YamlException($"Expected transform reference mapping or GUID scalar, but found '{scalar.Value}'.");
        }

        bool isInlineMapping = parser.TryConsume<MappingStart>(out _);
        if (!isInlineMapping && !parser.Accept<Scalar>(out _))
            parser.Consume<MappingStart>();

        Guid referenceId = Guid.Empty;
        string? name = null;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            string? key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case "ID":
                    if (parser.TryConsume<Scalar>(out Scalar? idScalar) && Guid.TryParse(idScalar.Value, out Guid parsedId))
                        referenceId = parsedId;
                    else
                        SkipNode(parser);
                    break;

                case "Name":
                    if (parser.TryConsume<Scalar>(out Scalar? nameScalar))
                        name = nameScalar.Value;
                    else
                        SkipNode(parser);
                    break;

                case TypeKey:
                    SkipNode(parser);
                    break;

                case ValueKey:
                    ReadReferenceTransformValue(parser, ref referenceId, ref name);
                    break;

                default:
                    SkipNode(parser);
                    break;
            }
        }

        return CreateReferencePlaceholder(referenceId, name);
    }

    private static void ReadReferenceTransformValue(IParser parser, ref Guid referenceId, ref string? name)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            if (Guid.TryParse(scalar.Value, out Guid scalarId))
                referenceId = scalarId;
            return;
        }

        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            string? key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case "ID":
                    if (parser.TryConsume<Scalar>(out Scalar? idScalar) && Guid.TryParse(idScalar.Value, out Guid parsedId))
                        referenceId = parsedId;
                    else
                        SkipNode(parser);
                    break;

                case "Name":
                    if (parser.TryConsume<Scalar>(out Scalar? nameScalar))
                        name = nameScalar.Value;
                    else
                        SkipNode(parser);
                    break;

                default:
                    SkipNode(parser);
                    break;
            }
        }
    }

    private static TransformBase CreateReferencePlaceholder(Guid referenceId, string? name)
        => new Transform
        {
            Name = name,
            SerializedReferenceId = referenceId,
        };

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

        if (parser.TryConsume<DocumentStart>(out _))
        {
            if (!parser.Accept<DocumentEnd>(out _))
                SkipNode(parser);

            _ = parser.TryConsume<DocumentEnd>(out _);
            return;
        }

        if (parser.TryConsume<DocumentEnd>(out _))
            return;

        if (parser.TryConsume<StreamStart>(out _))
        {
            if (!parser.Accept<StreamEnd>(out _))
                SkipNode(parser);

            _ = parser.TryConsume<StreamEnd>(out _);
            return;
        }

        if (parser.TryConsume<StreamEnd>(out _))
            return;

        if (parser.TryConsume<SequenceStart>(out _))
        {
            while (!parser.Accept<SequenceEnd>(out _))
                SkipNode(parser);

            parser.Consume<SequenceEnd>();
            return;
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            while (!parser.Accept<MappingEnd>(out _))
            {
                SkipNode(parser);

                if (parser.Accept<MappingEnd>(out _))
                    break;

                SkipNode(parser);
            }

            parser.Consume<MappingEnd>();
            return;
        }

        throw new YamlException("Unsupported YAML node encountered while skipping transform metadata.");
    }
}
