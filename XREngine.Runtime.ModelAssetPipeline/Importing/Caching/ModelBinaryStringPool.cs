using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Deterministic ordinal string table with strict UTF-8 decoding and offset references.
/// </summary>
internal sealed class ModelBinaryStringPool
{
    private readonly byte[] _bytes;
    private readonly ReadOnlyDictionary<uint, string> _stringsByOffset;
    private readonly Dictionary<string, uint> _offsetsByString;

    private ModelBinaryStringPool(
        byte[] bytes,
        Dictionary<uint, string> stringsByOffset,
        Dictionary<string, uint> offsetsByString)
    {
        _bytes = bytes;
        _stringsByOffset = new ReadOnlyDictionary<uint, string>(stringsByOffset);
        _offsetsByString = offsetsByString;
    }

    public ReadOnlyMemory<byte> Bytes => _bytes;
    public IReadOnlyDictionary<uint, string> StringsByOffset => _stringsByOffset;

    public static ModelBinaryStringPool Build(
        IEnumerable<string?> values,
        ModelCacheReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        limits ??= ModelCacheReadLimits.Default;

        string[] strings = values
            .Where(static value => !string.IsNullOrEmpty(value))
            .Select(static value => NormalizeAndValidate(value!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        if ((uint)strings.Length > limits.MaxStringCount)
            throw new ArgumentException("The string pool exceeds the configured string-count limit.", nameof(values));

        int byteCount = sizeof(uint);
        byte[][] encoded = new byte[strings.Length][];
        for (int i = 0; i < strings.Length; i++)
        {
            byte[] valueBytes = ModelBinaryCacheFormat.StrictUtf8.GetBytes(strings[i]);
            if (valueBytes.Length > limits.MaxStringBytes)
                throw new ArgumentException("A string exceeds the configured UTF-8 byte limit.", nameof(values));

            encoded[i] = valueBytes;
            byteCount = checked(byteCount + sizeof(uint) + valueBytes.Length);
        }

        if ((ulong)byteCount > limits.MaxStringPoolBytes)
            throw new ArgumentException("The string pool exceeds the configured byte limit.", nameof(values));

        byte[] poolBytes = new byte[byteCount];
        Dictionary<uint, string> stringsByOffset = new(strings.Length);
        Dictionary<string, uint> offsetsByString = new(strings.Length, StringComparer.Ordinal);
        int position = sizeof(uint);

        for (int i = 0; i < strings.Length; i++)
        {
            uint offset = checked((uint)position);
            byte[] valueBytes = encoded[i];
            BinaryPrimitives.WriteUInt32LittleEndian(poolBytes.AsSpan(position, sizeof(uint)), (uint)valueBytes.Length);
            valueBytes.CopyTo(poolBytes, position + sizeof(uint));
            stringsByOffset.Add(offset, strings[i]);
            offsetsByString.Add(strings[i], offset);
            position += sizeof(uint) + valueBytes.Length;
        }

        return new ModelBinaryStringPool(poolBytes, stringsByOffset, offsetsByString);
    }

    public static ModelBinaryStringPool Parse(byte[] bytes, ModelCacheReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(limits);
        if (bytes.Length < sizeof(uint) || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0)
            throw Invalid("The string pool does not begin with the reserved null entry.");

        Dictionary<uint, string> stringsByOffset = [];
        Dictionary<string, uint> offsetsByString = new(StringComparer.Ordinal);
        int position = sizeof(uint);
        string? previous = null;

        while (position < bytes.Length)
        {
            if (bytes.Length - position < sizeof(uint))
                throw Invalid("The string pool ends inside a string length prefix.");

            int entryOffset = position;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position, sizeof(uint)));
            position += sizeof(uint);
            if (length == 0)
                throw Invalid("Only the reserved null entry may have zero length.");
            if (length > limits.MaxStringBytes)
                throw Limit("A string exceeds the configured UTF-8 byte limit.");
            if (length > (uint)(bytes.Length - position))
                throw Invalid("A string extends beyond the string-pool region.");
            if ((uint)stringsByOffset.Count >= limits.MaxStringCount)
                throw Limit("The string pool exceeds the configured string-count limit.");

            string value;
            try
            {
                value = ModelBinaryCacheFormat.StrictUtf8.GetString(bytes, position, checked((int)length));
            }
            catch (DecoderFallbackException exception)
            {
                throw new ModelBinaryCacheFormatException(
                    CacheRejectReason.InvalidStringPool,
                    $"The string pool contains invalid UTF-8: {exception.Message}");
            }

            if (value.IndexOf('\0') >= 0)
                throw Invalid("The string pool contains an embedded NUL.");
            if (!value.IsNormalized(NormalizationForm.FormC))
                throw Invalid("The string pool contains a non-NFC string.");
            if (previous is not null && StringComparer.Ordinal.Compare(previous, value) >= 0)
                throw Invalid("The string pool is not strictly ordinally sorted and deduplicated.");

            uint offset = checked((uint)entryOffset);
            stringsByOffset.Add(offset, value);
            offsetsByString.Add(value, offset);
            previous = value;
            position += checked((int)length);
        }

        return new ModelBinaryStringPool(bytes, stringsByOffset, offsetsByString);
    }

    public uint GetOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        string normalized = NormalizeAndValidate(value);
        return _offsetsByString.TryGetValue(normalized, out uint offset)
            ? offset
            : throw new InvalidOperationException("The requested value was not included in the string pool.");
    }

    public string GetRequired(uint offset, string fieldName)
    {
        if (offset == 0)
            throw Invalid($"{fieldName} references the reserved null string.");

        return _stringsByOffset.TryGetValue(offset, out string? value)
            ? value
            : throw Invalid($"{fieldName} does not reference the start of a string-pool entry.");
    }

    public string? GetOptional(uint offset, string fieldName)
    {
        if (offset == 0)
            return null;

        return _stringsByOffset.TryGetValue(offset, out string? value)
            ? value
            : throw Invalid($"{fieldName} does not reference the start of a string-pool entry.");
    }

    private static string NormalizeAndValidate(string value)
    {
        if (value.IndexOf('\0') >= 0)
            throw new ArgumentException("Model-cache strings may not contain NUL characters.", nameof(value));

        return value.Normalize(NormalizationForm.FormC);
    }

    private static ModelBinaryCacheFormatException Invalid(string message)
        => new(CacheRejectReason.InvalidStringPool, message);

    private static ModelBinaryCacheFormatException Limit(string message)
        => new(CacheRejectReason.ResourceLimitExceeded, message);
}
