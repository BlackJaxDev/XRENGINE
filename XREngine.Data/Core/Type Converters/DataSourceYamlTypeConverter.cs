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

            try
            {
                return new DataSource(Compression.DecompressFromString(length, byteStr));
            }
            catch (Exception ex) when (ex is FormatException or YamlException)
            {
                // The most common cause is a truncated/corrupted hex payload in YAML.
                // In the editor we prefer to keep the rest of the asset loadable/inspectable,
                // so fall back to a zero-filled buffer of the declared length.
                uint fallbackLength = length ?? 0u;
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to deserialize DataSource bytes (declared Length={fallbackLength}). {ex.GetType().Name}: {ex.Message}");
                return new DataSource(fallbackLength, zeroMemory: true);
            }
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
