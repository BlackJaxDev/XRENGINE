using System;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Utilities;

namespace XREngine
{
    /// <summary>
    /// Wraps the root YAML value deserializer and records the last value whose type does not match the
    /// type that the reader asked for. YamlDotNet then tries to convert such a value with
    /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>, and that fails with
    /// "Object must implement IConvertible" without the names of the two types.
    /// <see cref="DepthTrackingNodeDeserializer"/> adds the recorded types and position to its error message,
    /// so a failed read names the node deserializer or converter output that has the wrong type.
    /// </summary>
    /// <remarks>
    /// This class only records. It does not change the value or the read result. Every nested value
    /// comes through this wrapper, because YamlDotNet passes the root value deserializer to each nested read.
    /// </remarks>
    internal sealed class TypeMismatchRecordingValueDeserializer(IValueDeserializer inner) : IValueDeserializer
    {
        [ThreadStatic]
        private static string? _lastMismatch;

        /// <summary>
        /// Gets the description of the last mismatched value on this thread, or null when there is none.
        /// </summary>
        public static string? LastMismatch => _lastMismatch;

        /// <summary>
        /// Clears the recorded mismatch at the start of a new document read.
        /// </summary>
        public static void Reset()
            => _lastMismatch = null;

        public object? DeserializeValue(IParser parser, Type expectedType, SerializerState state, IValueDeserializer nestedObjectDeserializer)
        {
            Mark start = parser.Current?.Start ?? Mark.Empty;
            object? value = inner.DeserializeValue(parser, expectedType, state, nestedObjectDeserializer);
            if (value is null || value is IValuePromise || value is IConvertible || expectedType == typeof(object) || expectedType.IsInstanceOfType(value))
                return value;

            _lastMismatch = $"read '{value.GetType().FullName}' where '{expectedType.FullName}' was expected, at {start.Line}:{start.Column}";
            return value;
        }
    }
}
