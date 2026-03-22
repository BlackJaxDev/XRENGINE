using System.Numerics;
using System.Globalization;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class Vector2YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Vector2);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                var parts = scalar.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2)
                    throw new YamlException("Expected Vector2 format 'X Y'.");

                return new Vector2(ParseSingle(parts[0]), ParseSingle(parts[1]));
            }

            if (parser.TryConsume<SequenceStart>(out _))
            {
                float x = ParseSingle(RequireScalar(parser, "Vector2 sequence X component"));
                float y = ParseSingle(RequireScalar(parser, "Vector2 sequence Y component"));

                if (!parser.TryConsume<SequenceEnd>(out _))
                    throw new YamlException("Expected the Vector2 sequence to contain exactly 2 elements.");

                return new Vector2(x, y);
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                float? x = null;
                float? y = null;

                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = RequireScalar(parser, "Vector2 mapping key");
                    string value = RequireScalar(parser, $"Vector2 mapping value for '{key}'");

                    switch (key.ToLowerInvariant())
                    {
                        case "x":
                            x = ParseSingle(value);
                            break;
                        case "y":
                            y = ParseSingle(value);
                            break;
                    }
                }

                if (!x.HasValue || !y.HasValue)
                    throw new YamlException("Expected Vector2 mapping with 'X' and 'Y' keys.");

                return new Vector2(x.Value, y.Value);
            }

            throw new YamlException("Expected a scalar, sequence, or mapping value to deserialize a Vector2.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v2 = (Vector2)value!;
            emitter.Emit(new Scalar($"{v2.X} {v2.Y}"));
        }

        private static string RequireScalar(IParser parser, string context) 
            => !parser.TryConsume<Scalar>(out var scalar)
                ? throw new YamlException($"Expected a scalar value for {context}.")
                : scalar.Value;

        private static float ParseSingle(string value)
            => float.Parse(value, CultureInfo.InvariantCulture);
    }
}
