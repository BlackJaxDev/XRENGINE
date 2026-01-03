using System;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// YamlDotNet's generic dictionary adapter can throw for complex dictionary-like types.
    /// XRMesh.BufferCollection is an eventful wrapper around EventDictionary, so we
    /// deserialize/serialize it by delegating to the underlying EventDictionary.
    /// </summary>
    [YamlTypeConverter]
    public sealed class XRMeshBufferCollectionYamlTypeConverter : IYamlTypeConverter
    {
        [ThreadStatic]
        private static bool _skip;

        public bool Accepts(Type type)
        {
            if (_skip)
                return false;

            return type == typeof(XRMesh.BufferCollection);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            // Allow explicit nulls.
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                    return null;

                throw new YamlException($"Unexpected scalar while deserializing {nameof(XRMesh)}.{nameof(XRMesh.BufferCollection)}: '{scalar.Value}'.");
            }

            bool prev = _skip;
            _skip = true;
            try
            {
                var buffers = (EventDictionary<string, XRDataBuffer>)rootDeserializer(typeof(EventDictionary<string, XRDataBuffer>));
                var collection = new XRMesh.BufferCollection
                {
                    Buffers = buffers ?? []
                };
                return collection;
            }
            finally
            {
                _skip = prev;
            }
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            bool prev = _skip;
            _skip = true;
            try
            {
                if (value is null)
                {
                    emitter.Emit(new Scalar("~"));
                    return;
                }

                if (value is not XRMesh.BufferCollection collection)
                    throw new YamlException($"Expected {nameof(XRMesh)}.{nameof(XRMesh.BufferCollection)} but got '{value.GetType()}'.");

                serializer(collection.Buffers);
            }
            finally
            {
                _skip = prev;
            }
        }
    }
}
