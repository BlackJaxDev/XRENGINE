using System.Numerics;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class Vector3YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Vector3);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException("Expected a scalar value to deserialize a Vector3.");

            var parts = scalar.Value.Split(' ');
            if (parts.Length != 3)
                throw new YamlException("Expected Vector3 format 'X Y Z'.");

            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());
            float z = float.Parse(parts[2].Trim());
            return new Vector3(x, y, z);
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v3 = (Vector3)value!;
            emitter.Emit(new Scalar($"{v3.X} {v3.Y} {v3.Z}"));
        }
    }
}
