using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace XREngine.Data;

/// <summary>
/// Writes a byte array as one base64 scalar. Put it on a large binary property with
/// <see cref="YamlConverterAttribute"/>. By default YAML writes each byte as one sequence item, which is about eight
/// times larger.
/// </summary>
/// <remarks>
/// The serializer must have the converter registered for the attribute to find it. <see cref="Accepts"/> is false, so the
/// registration does not change the format of other byte arrays.
/// </remarks>
public sealed class Base64ByteArrayYamlTypeConverter : IYamlTypeConverter
{
    /// <inheritdoc/>
    public bool Accepts(Type type)
        => false;

    /// <inheritdoc/>
    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        Scalar scalar = parser.Consume<Scalar>();
        if (scalar.Value.Length == 0)
            return Array.Empty<byte>();

        try
        {
            return Convert.FromBase64String(scalar.Value);
        }
        catch (FormatException ex)
        {
            throw new YamlException(scalar.Start, scalar.End, "The value is not valid base64 data.", ex);
        }
    }

    /// <inheritdoc/>
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        byte[] bytes = value as byte[] ?? [];
        emitter.Emit(new Scalar(null, null, Convert.ToBase64String(bytes), ScalarStyle.Plain, true, false));
    }
}
