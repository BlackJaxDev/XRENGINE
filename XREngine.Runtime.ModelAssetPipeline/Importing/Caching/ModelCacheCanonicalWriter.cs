using System.Buffers.Binary;
using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Writes explicit typed fields in a deterministic little-endian form for identity hashing.
/// </summary>
internal sealed class ModelCacheCanonicalWriter : IDisposable
{
    private enum FieldType : byte
    {
        Boolean = 1,
        Int32 = 2,
        UInt32 = 3,
        UInt64 = 4,
        Single = 5,
        String = 6,
        Bytes = 7,
    }

    private readonly MemoryStream _stream = new();

    public void WriteBoolean(uint fieldId, bool value)
    {
        WriteFieldHeader(fieldId, FieldType.Boolean, 1);
        _stream.WriteByte(value ? (byte)1 : (byte)0);
    }

    public void WriteInt32(uint fieldId, int value)
    {
        WriteFieldHeader(fieldId, FieldType.Int32, sizeof(int));
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteUInt32(uint fieldId, uint value)
    {
        WriteFieldHeader(fieldId, FieldType.UInt32, sizeof(uint));
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteUInt64(uint fieldId, ulong value)
    {
        WriteFieldHeader(fieldId, FieldType.UInt64, sizeof(ulong));
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void WriteSingle(uint fieldId, float value)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Canonical model settings cannot contain non-finite floats.");

        if (value == 0.0f)
            value = 0.0f;

        WriteFieldHeader(fieldId, FieldType.Single, sizeof(float));
        Span<byte> buffer = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, BitConverter.SingleToUInt32Bits(value));
        _stream.Write(buffer);
    }

    public void WriteString(uint fieldId, string? value)
    {
        if (value is null)
        {
            WriteFieldHeader(fieldId, FieldType.String, -1);
            return;
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormC));
        WriteFieldHeader(fieldId, FieldType.String, utf8.Length);
        _stream.Write(utf8);
    }

    public void WriteBytes(uint fieldId, ReadOnlySpan<byte> value)
    {
        WriteFieldHeader(fieldId, FieldType.Bytes, value.Length);
        _stream.Write(value);
    }

    public byte[] ToArray() => _stream.ToArray();

    public void Dispose() => _stream.Dispose();

    private void WriteFieldHeader(uint fieldId, FieldType fieldType, int payloadLength)
    {
        Span<byte> header = stackalloc byte[sizeof(uint) + sizeof(byte) + sizeof(int)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, fieldId);
        header[sizeof(uint)] = (byte)fieldType;
        BinaryPrimitives.WriteInt32LittleEndian(header[(sizeof(uint) + sizeof(byte))..], payloadLength);
        _stream.Write(header);
    }
}
