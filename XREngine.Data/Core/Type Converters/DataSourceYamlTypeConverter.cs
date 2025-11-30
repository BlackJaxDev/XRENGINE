using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class DataSourceYamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
            => type == typeof(DataSource);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            uint? length = null;
            string byteStr = string.Empty;

            parser.Consume<MappingStart>();
            {
                while (parser.TryConsume<Scalar>(out var scalar) && scalar != null)
                {
                    switch (scalar.Value)
                    {
                        case "Length":
                            length = uint.Parse(parser.Consume<Scalar>().Value);
                            break;
                        case "Bytes":
                            byteStr = parser.Consume<Scalar>().Value;
                            break;
                    }
                }
            }
            parser.Consume<MappingEnd>();

            return new DataSource(Compression.DecompressFromString(length, byteStr));
        }

        public unsafe void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not DataSource source)
                return;

            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            {
                emitter.Emit(new Scalar("Length"));
                emitter.Emit(new Scalar(source.Length.ToString()));

                emitter.Emit(new Scalar("Bytes"));
                emitter.Emit(new Scalar(Compression.CompressToString(source)));
            }
            emitter.Emit(new MappingEnd());
        }
    }
}
