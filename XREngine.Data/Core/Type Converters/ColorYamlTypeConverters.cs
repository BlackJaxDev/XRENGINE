using System.Globalization;
using XREngine.Data.Colors;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data
{
    [YamlTypeConverter]
    public sealed class ColorF4YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            Type? underlyingType = Nullable.GetUnderlyingType(type);
            return type == typeof(ColorF4) || underlyingType == typeof(ColorF4);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
                return ColorYamlParsing.ParseColorF4Scalar(scalar);

            if (parser.TryConsume<SequenceStart>(out _))
            {
                float r = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF4 sequence R component"));
                float g = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF4 sequence G component"));
                float b = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF4 sequence B component"));
                float a = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF4 sequence A component"));

                if (!parser.TryConsume<SequenceEnd>(out _))
                    throw new YamlException("Expected the ColorF4 sequence to contain exactly 4 elements.");

                return new ColorF4(r, g, b, a);
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                float r = 0.0f;
                float g = 0.0f;
                float b = 0.0f;
                float a = 0.0f;

                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = ColorYamlParsing.RequireScalar(parser, "ColorF4 mapping key");
                    string value = ColorYamlParsing.RequireScalar(parser, $"ColorF4 mapping value for '{key}'");

                    switch (key.ToLowerInvariant())
                    {
                        case "r":
                            r = ColorYamlParsing.ParseSingle(value);
                            break;
                        case "g":
                            g = ColorYamlParsing.ParseSingle(value);
                            break;
                        case "b":
                            b = ColorYamlParsing.ParseSingle(value);
                            break;
                        case "a":
                            a = ColorYamlParsing.ParseSingle(value);
                            break;
                    }
                }

                return new ColorF4(r, g, b, a);
            }

            throw new YamlException("Expected a scalar, sequence, or mapping value to deserialize a ColorF4.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            ColorF4 color = value is ColorF4 colorValue ? colorValue : default;
            emitter.Emit(new Scalar(
                $"{ColorYamlParsing.FormatSingle(color.R)} {ColorYamlParsing.FormatSingle(color.G)} {ColorYamlParsing.FormatSingle(color.B)} {ColorYamlParsing.FormatSingle(color.A)}"));
        }
    }

    [YamlTypeConverter]
    public sealed class ColorF3YamlTypeConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            Type? underlyingType = Nullable.GetUnderlyingType(type);
            return type == typeof(ColorF3) || underlyingType == typeof(ColorF3);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
                return ColorYamlParsing.ParseColorF3Scalar(scalar);

            if (parser.TryConsume<SequenceStart>(out _))
            {
                float r = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF3 sequence R component"));
                float g = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF3 sequence G component"));
                float b = ColorYamlParsing.ParseSingle(ColorYamlParsing.RequireScalar(parser, "ColorF3 sequence B component"));

                if (!parser.TryConsume<SequenceEnd>(out _))
                    throw new YamlException("Expected the ColorF3 sequence to contain exactly 3 elements.");

                return new ColorF3(r, g, b);
            }

            if (parser.TryConsume<MappingStart>(out _))
            {
                float r = 0.0f;
                float g = 0.0f;
                float b = 0.0f;

                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    string key = ColorYamlParsing.RequireScalar(parser, "ColorF3 mapping key");
                    string value = ColorYamlParsing.RequireScalar(parser, $"ColorF3 mapping value for '{key}'");

                    switch (key.ToLowerInvariant())
                    {
                        case "r":
                            r = ColorYamlParsing.ParseSingle(value);
                            break;
                        case "g":
                            g = ColorYamlParsing.ParseSingle(value);
                            break;
                        case "b":
                            b = ColorYamlParsing.ParseSingle(value);
                            break;
                    }
                }

                return new ColorF3(r, g, b);
            }

            throw new YamlException("Expected a scalar, sequence, or mapping value to deserialize a ColorF3.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            ColorF3 color = value is ColorF3 colorValue ? colorValue : default;
            emitter.Emit(new Scalar(
                $"{ColorYamlParsing.FormatSingle(color.R)} {ColorYamlParsing.FormatSingle(color.G)} {ColorYamlParsing.FormatSingle(color.B)}"));
        }
    }

    internal static class ColorYamlParsing
    {
        public static ColorF4 ParseColorF4Scalar(Scalar scalar)
        {
            string value = scalar.Value ?? string.Empty;
            if (IsNullScalar(value))
                return default;

            string[] parts = SplitScalar(value);
            if (parts.Length == 4)
                return new ColorF4(
                    ParseSingle(parts[0]),
                    ParseSingle(parts[1]),
                    ParseSingle(parts[2]),
                    ParseSingle(parts[3]));

            if (LooksLikeHexColor(value))
            {
                ColorF4 color = new(0.0f, 0.0f, 0.0f, 1.0f);
                color.HexCode = value;
                return color;
            }

            throw new YamlException("Expected ColorF4 format 'R G B A'.");
        }

        public static ColorF3 ParseColorF3Scalar(Scalar scalar)
        {
            string value = scalar.Value ?? string.Empty;
            if (IsNullScalar(value))
                return default;

            string[] parts = SplitScalar(value);
            if (parts.Length == 3)
                return new ColorF3(
                    ParseSingle(parts[0]),
                    ParseSingle(parts[1]),
                    ParseSingle(parts[2]));

            if (LooksLikeHexColor(value))
            {
                ColorF3 color = default;
                color.HexCode = value;
                return color;
            }

            throw new YamlException("Expected ColorF3 format 'R G B'.");
        }

        public static string RequireScalar(IParser parser, string context)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException($"Expected a scalar value for {context}.");

            return scalar.Value ?? string.Empty;
        }

        public static float ParseSingle(string value)
            => float.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);

        public static string FormatSingle(float value)
            => value.ToString("G9", CultureInfo.InvariantCulture);

        private static bool IsNullScalar(string value)
            => value.Length == 0
               || string.Equals(value, "~", StringComparison.Ordinal)
               || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase);

        private static string[] SplitScalar(string value)
            => value.Split(
                [' ', ',', 'R', 'r', 'G', 'g', 'B', 'b', 'A', 'a', ':', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool LooksLikeHexColor(string value)
        {
            if (value.StartsWith('#'))
                value = value[1..];
            else if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value[2..];
            else
                return false;

            return value.Length is 6 or 8;
        }
    }
}
