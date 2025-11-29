using System.Numerics;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class QuaternionYamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Quaternion);
        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException("Expected a scalar value to deserialize a Vector4.");
            var parts = scalar.Value.Split(' ');
            if (parts.Length != 4)
                throw new YamlException("Expected Vector4 format 'X Y Z W'.");
            float x = float.Parse(parts[0].Trim());
            float y = float.Parse(parts[1].Trim());
            float z = float.Parse(parts[2].Trim());
            float w = float.Parse(parts[3].Trim());
            return new Quaternion(x, y, z, w);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var v4 = (Quaternion)value!;
            emitter.Emit(new Scalar($"{v4.X} {v4.Y} {v4.Z} {v4.W}"));
        }
    }
}
