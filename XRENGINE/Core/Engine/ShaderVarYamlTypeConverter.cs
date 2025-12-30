using System;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Vectors;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine
{
    /// <summary>
    /// Enables deserialization of <see cref="ShaderVar"/> polymorphic parameters from YAML.
    /// The YAML format historically omits type tags, so we infer a concrete ShaderVar subtype
    /// from the serialized Value (or an explicit discriminator if present).
    /// </summary>
    [YamlTypeConverter]
    public sealed class ShaderVarYamlTypeConverter : IYamlTypeConverter
    {
        [ThreadStatic]
        private static bool _skip;

        public bool Accepts(Type type)
        {
            if (_skip)
                return false;

            // Only intercept the abstract base. Concrete ShaderVar types can be handled normally.
            return type == typeof(ShaderVar);
        }

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            // NOTE: In this repo's YamlDotNet version, ObjectDeserializer only accepts a Type and
            // consumes from the current parser. That means we can't "peek" and then replay.
            // Instead, parse the mapping ourselves and build the concrete ShaderVar instance.
            if (!parser.TryConsume<MappingStart>(out _))
                throw new YamlException("Expected a mapping to deserialize a ShaderVar.");

            string? name = null;
            string? typeToken = null;
            string? valueToken = null;
            bool hasColorKey = false;

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                string key = ConsumeScalar(parser, "Expected a scalar mapping key while deserializing a ShaderVar.");

                if (key == "Color")
                    hasColorKey = true;

                if (key is "__shaderVarType" or "ShaderVarType" or "Type" or "TypeName")
                {
                    typeToken = TryConsumeScalar(parser);
                    if (typeToken is null)
                        SkipNode(parser);
                    continue;
                }

                if (key == "Name")
                {
                    name = TryConsumeScalar(parser);
                    if (name is null)
                        SkipNode(parser);
                    continue;
                }

                if (key == "Value")
                {
                    valueToken = TryConsumeScalar(parser);
                    if (valueToken is null)
                        SkipNode(parser);
                    continue;
                }

                // Unknown field: consume and discard
                SkipNode(parser);
            }

            Type concreteType = InferConcreteShaderVarType(typeToken, valueToken, hasColorKey) ?? typeof(ShaderFloat);

            if (Activator.CreateInstance(concreteType) is not ShaderVar shaderVar)
                throw new YamlException($"Failed to create a ShaderVar instance of type '{concreteType.FullName}'.");

            if (!string.IsNullOrWhiteSpace(name))
                shaderVar.Name = name!;

            if (!string.IsNullOrWhiteSpace(valueToken))
                TrySetValueFromScalar(shaderVar, valueToken!);

            return shaderVar;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            bool previous = _skip;
            _skip = true;
            try
            {
                serializer(value);
            }
            finally
            {
                _skip = previous;
            }
        }

        private static Type? InferConcreteShaderVarType(string? typeToken, string? valueToken, bool hasColorKey)
        {
            if (!string.IsNullOrWhiteSpace(typeToken))
            {
                if (TryParseShaderVarType(typeToken!, out var parsedType) && ShaderVar.ShaderTypeAssociations.TryGetValue(parsedType, out var clrType))
                    return clrType;
            }

            if (!string.IsNullOrWhiteSpace(valueToken))
            {
                if (TryInferFromValueToken(valueToken!, out var inferred) && ShaderVar.ShaderTypeAssociations.TryGetValue(inferred, out var clrType))
                    return clrType;
            }

            // Heuristic for older assets: BaseColor-like params often serialize a dummy Color field.
            if (hasColorKey)
                return typeof(ShaderVector3);

            return null;
        }

        private static bool TryParseShaderVarType(string token, out EShaderVarType type)
        {
            token = token.Trim();

            // Accept both "_vec3" and "vec3" forms.
            if (!token.StartsWith('_'))
                token = "_" + token;

            return Enum.TryParse(token, ignoreCase: true, out type);
        }

        private static bool TryInferFromValueToken(string token, out EShaderVarType type)
        {
            token = token.Trim();

            if (string.Equals(token, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "false", StringComparison.OrdinalIgnoreCase))
            {
                type = EShaderVarType._bool;
                return true;
            }

            // Split by whitespace; Vector converters serialize as "x y z".
            string[] parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // If it's a single token, decide between int/float/uint/double heuristically.
            if (parts.Length == 1)
            {
                // Try integer first.
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    type = EShaderVarType._int;
                    return true;
                }

                // Then unsigned.
                if (uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    type = EShaderVarType._uint;
                    return true;
                }

                // Fall back to float (most common material parameter).
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    type = EShaderVarType._float;
                    return true;
                }

                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    type = EShaderVarType._double;
                    return true;
                }

                type = EShaderVarType._float;
                return true;
            }

            type = parts.Length switch
            {
                2 => EShaderVarType._vec2,
                3 => EShaderVarType._vec3,
                4 => EShaderVarType._vec4,
                16 => EShaderVarType._mat4,
                _ => EShaderVarType._float
            };
            return true;
        }

        private static string ConsumeScalar(IParser parser, string errorMessage)
        {
            if (!parser.TryConsume<Scalar>(out var scalar))
                throw new YamlException(errorMessage);
            return scalar.Value ?? string.Empty;
        }

        private static string? TryConsumeScalar(IParser parser)
        {
            return parser.TryConsume<Scalar>(out var scalar) ? scalar.Value : null;
        }

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

        private static void TrySetValueFromScalar(ShaderVar shaderVar, string token)
        {
            // Some ShaderVar implementations override Value on the base type (non-generic),
            // but most use ShaderVar<T>.Value. Use reflection to handle both.
            PropertyInfo? valueProperty = shaderVar.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            if (valueProperty is null || !valueProperty.CanWrite)
                return;

            object parsed = ParseValue(valueProperty.PropertyType, token);
            valueProperty.SetValue(shaderVar, parsed);
        }

        private static object ParseValue(Type targetType, string token)
        {
            token = token.Trim();

            if (targetType == typeof(string))
                return token;
            if (targetType == typeof(bool))
                return bool.Parse(token);
            if (targetType == typeof(int))
                return int.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))
                return uint.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);

            string[] parts = token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (targetType == typeof(Vector2))
                return new Vector2(ParseFloat(parts, 0), ParseFloat(parts, 1));
            if (targetType == typeof(Vector3))
                return new Vector3(ParseFloat(parts, 0), ParseFloat(parts, 1), ParseFloat(parts, 2));
            if (targetType == typeof(Vector4))
                return new Vector4(ParseFloat(parts, 0), ParseFloat(parts, 1), ParseFloat(parts, 2), ParseFloat(parts, 3));
            if (targetType == typeof(Matrix4x4))
            {
                if (parts.Length != 16)
                    throw new YamlException("Expected Matrix4x4 format with 16 components.");

                return new Matrix4x4(
                    ParseFloat(parts, 0), ParseFloat(parts, 1), ParseFloat(parts, 2), ParseFloat(parts, 3),
                    ParseFloat(parts, 4), ParseFloat(parts, 5), ParseFloat(parts, 6), ParseFloat(parts, 7),
                    ParseFloat(parts, 8), ParseFloat(parts, 9), ParseFloat(parts, 10), ParseFloat(parts, 11),
                    ParseFloat(parts, 12), ParseFloat(parts, 13), ParseFloat(parts, 14), ParseFloat(parts, 15));
            }

            if (targetType == typeof(IVector2))
                return new IVector2(ParseInt(parts, 0), ParseInt(parts, 1));
            if (targetType == typeof(IVector3))
                return new IVector3(ParseInt(parts, 0), ParseInt(parts, 1), ParseInt(parts, 2));
            if (targetType == typeof(IVector4))
                return new IVector4(ParseInt(parts, 0), ParseInt(parts, 1), ParseInt(parts, 2), ParseInt(parts, 3));

            if (targetType == typeof(UVector2))
                return new UVector2(ParseUInt(parts, 0), ParseUInt(parts, 1));
            if (targetType == typeof(UVector3))
                return new UVector3(ParseUInt(parts, 0), ParseUInt(parts, 1), ParseUInt(parts, 2));
            if (targetType == typeof(UVector4))
                return new UVector4(ParseUInt(parts, 0), ParseUInt(parts, 1), ParseUInt(parts, 2), ParseUInt(parts, 3));

            if (targetType == typeof(DVector2))
                return new DVector2(ParseDouble(parts, 0), ParseDouble(parts, 1));
            if (targetType == typeof(DVector3))
                return new DVector3(ParseDouble(parts, 0), ParseDouble(parts, 1), ParseDouble(parts, 2));
            if (targetType == typeof(DVector4))
                return new DVector4(ParseDouble(parts, 0), ParseDouble(parts, 1), ParseDouble(parts, 2), ParseDouble(parts, 3));

            if (targetType == typeof(BoolVector2))
                return new BoolVector2(ParseBool(parts, 0), ParseBool(parts, 1));
            if (targetType == typeof(BoolVector3))
                return new BoolVector3(ParseBool(parts, 0), ParseBool(parts, 1), ParseBool(parts, 2));
            if (targetType == typeof(BoolVector4))
                return new BoolVector4(ParseBool(parts, 0), ParseBool(parts, 1), ParseBool(parts, 2), ParseBool(parts, 3));

            // Last resort: attempt to use a string ctor.
            ConstructorInfo? ctor = targetType.GetConstructor([typeof(string)]);
            if (ctor is not null)
                return ctor.Invoke([token]);

            throw new YamlException($"Unsupported ShaderVar Value type '{targetType.FullName}'.");
        }

        private static float ParseFloat(string[] parts, int index)
        {
            if (index >= parts.Length)
                throw new YamlException("Not enough components in scalar value.");
            return float.Parse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static double ParseDouble(string[] parts, int index)
        {
            if (index >= parts.Length)
                throw new YamlException("Not enough components in scalar value.");
            return double.Parse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string[] parts, int index)
        {
            if (index >= parts.Length)
                throw new YamlException("Not enough components in scalar value.");
            return int.Parse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static uint ParseUInt(string[] parts, int index)
        {
            if (index >= parts.Length)
                throw new YamlException("Not enough components in scalar value.");
            return uint.Parse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static bool ParseBool(string[] parts, int index)
        {
            if (index >= parts.Length)
                throw new YamlException("Not enough components in scalar value.");
            return bool.Parse(parts[index]);
        }
    }
}
