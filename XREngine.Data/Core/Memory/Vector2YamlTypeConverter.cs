using System.Numerics;
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
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException("Expected a scalar value to deserialize a Vector2.");
            var parts = scalar.Value.Split(' ');
            if (parts.Length != 2)
                throw new YamlException("Expected Vector2 format 'X Y'.");
            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());
            return new Vector2(x, y);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v2 = (Vector2)value!;
            emitter.Emit(new Scalar($"{v2.X} {v2.Y}"));
        }
    }
}
