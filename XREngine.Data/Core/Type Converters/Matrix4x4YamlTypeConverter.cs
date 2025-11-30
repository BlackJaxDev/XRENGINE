using System.Numerics;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class Matrix4x4YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(Matrix4x4);
        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException("Expected a scalar value to deserialize a Matrix4x4.");
            var parts = scalar.Value.Split(' ');
            if (parts.Length != 16)
                throw new YamlException("Expected Matrix4x4 format 'M00 M01 M02 M03 M10 M11 M12 M13 M20 M21 M22 M23 M30 M31 M32 M33'.");
            float m00 = float.Parse(parts[0].Trim());
            float m01 = float.Parse(parts[1].Trim());
            float m02 = float.Parse(parts[2].Trim());
            float m03 = float.Parse(parts[3].Trim());
            float m10 = float.Parse(parts[4].Trim());
            float m11 = float.Parse(parts[5].Trim());
            float m12 = float.Parse(parts[6].Trim());
            float m13 = float.Parse(parts[7].Trim());
            float m20 = float.Parse(parts[8].Trim());
            float m21 = float.Parse(parts[9].Trim());
            float m22 = float.Parse(parts[10].Trim());
            float m23 = float.Parse(parts[11].Trim());
            float m30 = float.Parse(parts[12].Trim());
            float m31 = float.Parse(parts[13].Trim());
            float m32 = float.Parse(parts[14].Trim());
            float m33 = float.Parse(parts[15].Trim());
            return new Matrix4x4(
                m00, m01, m02, m03,
                m10, m11, m12, m13,
                m20, m21, m22, m23,
                m30, m31, m32, m33);
        }
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var m4x4 = (Matrix4x4)value!;
            emitter.Emit(new Scalar($"{m4x4.M11} {m4x4.M12} {m4x4.M13} {m4x4.M14} {m4x4.M21} {m4x4.M22} {m4x4.M23} {m4x4.M24} {m4x4.M31} {m4x4.M32} {m4x4.M33} {m4x4.M34} {m4x4.M41} {m4x4.M42} {m4x4.M43} {m4x4.M44}"));
        }
    }
}
