using System;
using XREngine.Rendering;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// YamlDotNet cannot instantiate abstract types like <see cref="XRTexture"/>.
    /// For asset YAML, material textures are serialized as 2D textures (mip chain),
    /// so we deserialize <see cref="XRTexture"/> as <see cref="XRTexture2D"/>.
    /// </summary>
    [YamlTypeConverter]
    public sealed class XRTextureYamlTypeConverter : IYamlTypeConverter
    {
        [ThreadStatic]
        private static bool _skip;

        public bool Accepts(Type type)
        {
            if (_skip)
                return false;

            return type == typeof(XRTexture);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            // Allow explicit nulls in texture slots.
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                if (scalar.Value is null || scalar.Value == "~" || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Not a null scalar; put it back isn't possible, so treat as invalid.
                throw new YamlException($"Unexpected scalar while deserializing {nameof(XRTexture)}: '{scalar.Value}'.");
            }

            bool prev = _skip;
            _skip = true;
            try
            {
                // Default to XRTexture2D for YAML assets.
                return rootDeserializer(typeof(XRTexture2D));
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
                serializer(value);
            }
            finally
            {
                _skip = prev;
            }
        }
    }
}
