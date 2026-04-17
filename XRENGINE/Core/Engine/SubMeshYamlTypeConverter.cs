using System;
using XREngine.Rendering.Models;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine;

[YamlTypeConverter]
public sealed class SubMeshYamlTypeConverter : IWriteOnlyYamlTypeConverter
{
    [ThreadStatic]
    private static bool _skip;

    public bool Accepts(Type type)
    {
        if (_skip)
            return false;

        return type == typeof(SubMesh);
    }

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        => throw new NotSupportedException($"{nameof(SubMeshYamlTypeConverter)} is write-only; reading is handled by {nameof(XRAssetDeserializer)}.");

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is null)
        {
            emitter.Emit(new Scalar("~"));
            return;
        }

        if (value is not SubMesh subMesh)
            throw new YamlException($"Expected {nameof(SubMesh)} but got '{value.GetType()}'.");

        if (TryWriteAsReference.TryEmitReference(emitter, subMesh))
            return;

        bool previous = _skip;
        _skip = true;
        try
        {
            serializer(value, value.GetType());
        }
        finally
        {
            _skip = previous;
        }
    }
}
