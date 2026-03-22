using System.Numerics;
using System.Globalization;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class Vector4YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Vector4);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                var parts = scalar.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 4)
                    throw new YamlException("Expected Vector4 format 'X Y Z W'.");

                return new Vector4(ParseSingle(parts[0]), ParseSingle(parts[1]), ParseSingle(parts[2]), ParseSingle(parts[3]));
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                float x = ParseSingle(RequireScalar(parser, "Vector4 sequence X component"));
                float y = ParseSingle(RequireScalar(parser, "Vector4 sequence Y component"));
                float z = ParseSingle(RequireScalar(parser, "Vector4 sequence Z component"));
                float w = ParseSingle(RequireScalar(parser, "Vector4 sequence W component"));

                if (!parser.TryConsume<SequenceEnd>(out _))
                    throw new YamlException("Expected the Vector4 sequence to contain exactly 4 elements.");

                return new Vector4(x, y, z, w);
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                float? x = null;
                float? y = null;
                float? z = null;
                float? w = null;

                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = RequireScalar(parser, "Vector4 mapping key");
                    string value = RequireScalar(parser, $"Vector4 mapping value for '{key}'");

                    switch (key.ToLowerInvariant())
                    {
                        case "x":
                            x = ParseSingle(value);
                            break;
                        case "y":
                            y = ParseSingle(value);
                            break;
                        case "z":
                            z = ParseSingle(value);
                            break;
                        case "w":
                            w = ParseSingle(value);
                            break;
                    }
                }

                if (!x.HasValue || !y.HasValue || !z.HasValue || !w.HasValue)
                    throw new YamlException("Expected Vector4 mapping with 'X', 'Y', 'Z', and 'W' keys.");

                return new Vector4(x.Value, y.Value, z.Value, w.Value);
            }

            throw new YamlException("Expected a scalar, sequence, or mapping value to deserialize a Vector4.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v4 = (Vector4)value!;
            emitter.Emit(new Scalar($"{v4.X} {v4.Y} {v4.Z} {v4.W}"));
        }

        private static string RequireScalar(IParser parser, string context)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException($"Expected a scalar value for {context}.");

            return scalar.Value;
        }

        private static float ParseSingle(string value)
            => float.Parse(value, CultureInfo.InvariantCulture);
    }
}
