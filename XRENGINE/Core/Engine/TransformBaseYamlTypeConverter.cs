using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Core.Engine;

/// <summary>
/// Handles TransformBase YAML while keeping transform graphs stable and allowing default
/// scene-node transforms to serialize inline when the declared property type already implies Transform.
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

        if (parser.Accept<Scalar>(out Scalar? scalar) && IsNullScalar(scalar))
        {
            parser.Consume<Scalar>();
            return null;
        }

        if (isReferenceMode)
            return ReadReferenceTransform(parser);

        bool hasNodeMappingStart = parser.TryConsume<MappingStart>(out _);
        if (!hasNodeMappingStart && !parser.Accept<Scalar>(out _))
            parser.Consume<MappingStart>();

        Type? runtimeType = null;
        TransformBase? result = null;
        TransformBase[]? serializedChildren = null;
        bool hasSerializedChildren = false;
        bool sawRecognizedKey = false;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!hasNodeMappingStart)
            {
                if (!parser.Accept<Scalar>(out Scalar? nextKey))
                    break;

                if (!IsRecognizedTransformTopLevelKey(nextKey.Value))
                    break;
            }

            var key = parser.Consume<Scalar>().Value;
            switch (key)
            {
                case TypeKey:
                    sawRecognizedKey = true;
                    if (parser.TryConsume<Scalar>(out var typeScalar))
                        runtimeType = ResolveTransformType(typeScalar.Value);
                    break;

                case ValueKey:
                    sawRecognizedKey = true;
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

                case ChildrenKey:
                    sawRecognizedKey = true;
                    serializedChildren = rootDeserializer(typeof(TransformBase[])) as TransformBase[];
                    hasSerializedChildren = true;
                    break;

                default:
                    if (TryReadInlineConcreteTransformProperty(key, type, ref runtimeType, ref result, rootDeserializer, parser))
                    {
                        sawRecognizedKey = true;
                        break;
                    }

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

        if (runtimeType == typeof(Transform) && defaultType == typeof(Transform) && value is Transform concreteTransform)
        {
            EmitFlatConcreteTransform(emitter, serializer, concreteTransform);
        }
        else
        {
            emitter.Emit(new Scalar(ValueKey));
            SerializeTransformValue(serializer, value, runtimeType);
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
        if (!isInlineMapping
            && parser.Accept<Scalar>(out Scalar? initialScalar)
            && Guid.TryParse(initialScalar.Value, out Guid scalarId))
        {
            parser.Consume<Scalar>();
            transform.SerializedReferenceId = scalarId;
            return transform;
        }

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (!isInlineMapping)
            {
                if (!parser.Accept<Scalar>(out Scalar? nextKey))
                    break;

                if (!IsConcreteTransformPropertyKey(nextKey.Value))
                    break;
            }

            string? key = parser.Consume<Scalar>().Value;
            if (!TryReadConcreteTransformProperty(transform, key, rootDeserializer, parser))
            {
                SkipNode(parser);
                continue;
            }

        }

        return transform;
    }

    private static bool TryReadInlineConcreteTransformProperty(string? key,
                                                               Type declaredType,
                                                               ref Type? runtimeType,
                                                               ref TransformBase? result,
                                                               ObjectDeserializer rootDeserializer,
                                                               IParser parser)
    {
        if (!IsConcreteTransformPropertyKey(key))
            return false;

        if (runtimeType is null && !YamlDefaultTypeContext.TryConsumeRead(declaredType, out runtimeType))
            runtimeType = typeof(Transform);

        if (runtimeType != typeof(Transform))
            return false;

        result ??= new Transform();
        return TryReadConcreteTransformProperty((Transform)result, key, rootDeserializer, parser);
    }

    private static bool IsConcreteTransformPropertyKey(string? key)
        => key is "Scale"
            or "Translation"
            or "Rotation"
            or "Order"
            or "Name"
            or "ID"
            or "TimeBetweenReplications"
            or "ImmediateLocalMatrixRecalculation"
            or "SmoothingSpeed";

    private static bool IsRecognizedTransformTopLevelKey(string? key)
        => key is TypeKey or ValueKey or ChildrenKey || IsConcreteTransformPropertyKey(key);

    private static bool TryReadConcreteTransformProperty(Transform transform,
                                                         string? key,
                                                         ObjectDeserializer rootDeserializer,
                                                         IParser parser)
    {
        switch (key)
        {
            case "Scale":
                if (rootDeserializer(typeof(Vector3?)) is Vector3 scale)
                    transform.Scale = scale;
                else
                    SkipNode(parser);
                return true;

            case "Translation":
                if (rootDeserializer(typeof(Vector3?)) is Vector3 translation)
                    transform.Translation = translation;
                else
                    SkipNode(parser);
                return true;

            case "Rotation":
                if (rootDeserializer(typeof(Quaternion?)) is Quaternion rotation)
                    transform.Rotation = rotation;
                else
                    SkipNode(parser);
                return true;

            case "Order":
                if (rootDeserializer(typeof(ETransformOrder?)) is ETransformOrder order)
                    transform.Order = order;
                else
                    SkipNode(parser);
                return true;

            case "Name":
                if (parser.TryConsume<Scalar>(out Scalar? nameScalar))
                    transform.Name = nameScalar.Value;
                else
                    SkipNode(parser);
                return true;

            case "ID":
                if (parser.TryConsume<Scalar>(out Scalar? idScalar) && Guid.TryParse(idScalar.Value, out Guid serializedReferenceId))
                    transform.SerializedReferenceId = serializedReferenceId;
                else
                    SkipNode(parser);
                return true;

            case "TimeBetweenReplications":
                if (rootDeserializer(typeof(float?)) is float replicationInterval)
                    transform.TimeBetweenReplicationsOverrideSerialized = replicationInterval;
                else
                    transform.TimeBetweenReplicationsOverrideSerialized = null;
                return true;

            case "ImmediateLocalMatrixRecalculation":
                if (rootDeserializer(typeof(bool?)) is bool immediateRecalculation)
                    transform.ImmediateLocalMatrixRecalculation = immediateRecalculation;
                else
                    SkipNode(parser);
                return true;

            case "SmoothingSpeed":
                if (rootDeserializer(typeof(float?)) is float smoothingSpeed)
                    transform.SmoothingSpeed = smoothingSpeed;
                else
                    SkipNode(parser);
                return true;

            default:
                return false;
        }
    }

    private static void SerializeTransformValue(ObjectSerializer serializer, object value, Type runtimeType)
    {
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
    }

    private static void EmitFlatConcreteTransform(IEmitter emitter, ObjectSerializer serializer, Transform transform)
    {
        EmitProperty(emitter, serializer, "ID", transform.EffectiveSerializedReferenceId, typeof(Guid));

        if (transform.Name is not null && !ShouldSuppressConcreteTransformName(transform))
            EmitProperty(emitter, serializer, "Name", transform.Name, typeof(string));

        if (transform.TimeBetweenReplicationsOverrideSerialized is float replicationInterval)
            EmitProperty(emitter, serializer, "TimeBetweenReplications", replicationInterval, typeof(float));

        if (!transform.ImmediateLocalMatrixRecalculation)
            EmitProperty(emitter, serializer, "ImmediateLocalMatrixRecalculation", transform.ImmediateLocalMatrixRecalculation, typeof(bool));

        if (transform.ScaleSerialized is Vector3 scale)
            EmitProperty(emitter, serializer, "Scale", scale, typeof(Vector3));

        if (transform.TranslationSerialized is Vector3 translation)
            EmitProperty(emitter, serializer, "Translation", translation, typeof(Vector3));

        if (transform.RotationSerialized is Quaternion rotation)
            EmitProperty(emitter, serializer, "Rotation", rotation, typeof(Quaternion));

        if (transform.Order != default)
            EmitProperty(emitter, serializer, "Order", transform.Order, typeof(ETransformOrder));

        if (MathF.Abs(transform.SmoothingSpeed - 0.4f) > float.Epsilon)
            EmitProperty(emitter, serializer, "SmoothingSpeed", transform.SmoothingSpeed, typeof(float));
    }

    private static bool ShouldSuppressConcreteTransformName(Transform transform)
        => transform.SceneNode is SceneNode sceneNode
            && string.Equals(transform.Name, sceneNode.Name, StringComparison.Ordinal);

    private static void EmitProperty(IEmitter emitter, ObjectSerializer serializer, string key, object value, Type type)
    {
        emitter.Emit(new Scalar(key));
        serializer(value, type);
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
