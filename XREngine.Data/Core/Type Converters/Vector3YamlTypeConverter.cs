using System.Numerics;
using System.Globalization;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class Vector3YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Vector3) || type == typeof(Vector3?);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                var parts = scalar.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 3)
                    throw new YamlException("Expected Vector3 format 'X Y Z'.");

                return new Vector3(ParseSingle(parts[0]), ParseSingle(parts[1]), ParseSingle(parts[2]));
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                float x = ParseSingle(RequireScalar(parser, "Vector3 sequence X component"));
                float y = ParseSingle(RequireScalar(parser, "Vector3 sequence Y component"));
                float z = ParseSingle(RequireScalar(parser, "Vector3 sequence Z component"));

                if (!parser.TryConsume<SequenceEnd>(out _))
                    throw new YamlException("Expected the Vector3 sequence to contain exactly 3 elements.");

                return new Vector3(x, y, z);
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                float? x = null;
                float? y = null;
                float? z = null;

                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = RequireScalar(parser, "Vector3 mapping key");
                    string value = RequireScalar(parser, $"Vector3 mapping value for '{key}'");

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
                    }
                }

                if (!x.HasValue || !y.HasValue || !z.HasValue)
                    throw new YamlException("Expected Vector3 mapping with 'X', 'Y', and 'Z' keys.");

                return new Vector3(x.Value, y.Value, z.Value);
            }

            throw new YamlException("Expected a scalar, sequence, or mapping value to deserialize a Vector3.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v3 = (Vector3)value!;
            emitter.Emit(new Scalar($"{v3.X} {v3.Y} {v3.Z}"));
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
