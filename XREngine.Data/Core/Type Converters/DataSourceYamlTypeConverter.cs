using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class DataSourceYamlTypeConverter : IYamlTypeConverter
    {
        private enum DataSourceEncoding
        {
            LzmaHex,
            RawHex,
        }

        public bool Accepts(Type type)
            => type == typeof(DataSource);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            uint? length = null;
            string byteStr = string.Empty;
            DataSourceEncoding encoding = DataSourceEncoding.LzmaHex;

            parser.Consume<MappingStart>();
            {
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = ConsumeScalar(parser, "Expected a scalar mapping key while deserializing a DataSource.");

                    if (key == "Length")
                    {
                        string? token = TryConsumeScalar(parser);
                        if (token is null)
                        {
                            SkipNode(parser);
                            continue;
                        }

                        if (uint.TryParse(token, out uint parsedLength))
                            length = parsedLength;
                        continue;
                    }

                    if (key == "Bytes")
                    {
                        byteStr = TryConsumeScalar(parser) ?? string.Empty;
                        if (byteStr.Length == 0)
                            SkipNode(parser);
                        continue;
                    }

                    if (key is "Encoding" or "Compression")
                    {
                        string? token = TryConsumeScalar(parser);
                        if (token is null)
                        {
                            SkipNode(parser);
                            continue;
                        }

                        encoding = ParseEncodingToken(token);
                        continue;
                    }

                    if (key is "Compressed" or "IsCompressed")
                    {
                        string? token = TryConsumeScalar(parser);
                        if (token is null)
                        {
                            SkipNode(parser);
                            continue;
                        }

                        if (bool.TryParse(token, out bool compressedFlag) && !compressedFlag)
                            encoding = DataSourceEncoding.RawHex;
                        continue;
                    }

                    // Unknown field: consume and discard
                    SkipNode(parser);
                }
            }

            try
            {
                DataSource result;

                if (encoding == DataSourceEncoding.RawHex)
                {
                    if (string.IsNullOrWhiteSpace(byteStr))
                    {
                        result = new DataSource(length ?? 0u, zeroMemory: true) { PreferCompressedYaml = false };
                    }
                    else
                    {
                        byte[] rawBytes = Convert.FromHexString(NormalizeHexScalar(byteStr));
                        result = new DataSource(rawBytes) { PreferCompressedYaml = false };
                    }
                }
                else
                {
                    result = new DataSource(Compression.DecompressFromString(length, byteStr)) { PreferCompressedYaml = true };
                }

                return result;
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

                if (!source.PreferCompressedYaml)
                {
                    emitter.Emit(new Scalar("Encoding"));
                    emitter.Emit(new Scalar("RawHex"));

                    emitter.Emit(new Scalar("Bytes"));
                    emitter.Emit(new Scalar(Convert.ToHexString(source.GetBytes())));
                }
                else
                {
                    emitter.Emit(new Scalar("Bytes"));
                    emitter.Emit(new Scalar(Compression.CompressToString(source)));
                }
            }
            emitter.Emit(new MappingEnd());
        }

        private static string ConsumeScalar(IParser parser, string errorMessage)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException(errorMessage);
            return scalar.Value ?? string.Empty;
        }

        private static string? TryConsumeScalar(IParser parser)
            => parser.TryConsume<Scalar>(out var scalar) ? scalar.Value : null;

        private static void SkipNode(IParser parser)
        {
            if (parser.TryConsume<Scalar>(out _))
                return;

            if (parser.TryConsume<AnchorAlias>(out _))
                return;

            if (parser.TryConsume<SequenceStart>(out _))
            {
                while (!parser.TryConsume<SequenceEnd>(out _))
                    SkipNode(parser);
                return;
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    SkipNode(parser); // key
                    SkipNode(parser); // value
                }
                return;
            }

            throw new YamlException("Unsupported YAML node encountered while skipping a value.");
        }

        private static DataSourceEncoding ParseEncodingToken(string token)
        {
            token = token.Trim();
            if (token.Equals("Raw", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("RawHex", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Uncompressed", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("NoCompression", StringComparison.OrdinalIgnoreCase))
            {
                return DataSourceEncoding.RawHex;
            }

            return DataSourceEncoding.LzmaHex;
        }

        private static string NormalizeHexScalar(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return string.Empty;

            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            bool hasWhitespace = false;
            for (int i = 0; i < hex.Length; i++)
            {
                if (char.IsWhiteSpace(hex[i]))
                {
                    hasWhitespace = true;
                    break;
                }
            }

            if (!hasWhitespace)
                return hex;

            var sb = new System.Text.StringBuilder(hex.Length);
            foreach (char c in hex)
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
