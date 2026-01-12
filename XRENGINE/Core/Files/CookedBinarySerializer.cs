using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using MemoryPack;
using XREngine.Data;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using XREngine.Core;

namespace XREngine.Core.Files;

public interface ICookedBinarySerializable
{
    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    void WriteCookedBinary(CookedBinaryWriter writer);

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    void ReadCookedBinary(CookedBinaryReader reader);

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    long CalculateCookedBinarySize();
}

public sealed class CookedBinarySerializationCallbacks
{
    public Func<object?, object?>? OnSerializingValue { get; init; }

    public Func<object?, object?>? OnDeserializedValue { get; init; }
}

public sealed unsafe class CookedBinaryWriter : IDisposable
{
    private readonly FileMap? _map;
    private readonly byte* _start;
    private byte* _cursor;
    private readonly byte* _end;

    internal CookedBinaryWriter(byte* buffer, int length, FileMap? map = null)
    {
        _map = map;
        _start = buffer;
        _cursor = buffer;
        _end = buffer + length;
    }

    public long BytesWritten => _cursor - _start;
    public long Position
    {
        get => _cursor - _start;
        set
        {
            byte* target = _start + value;
            if (target < _start || target > _end)
                throw new ArgumentOutOfRangeException(nameof(value));
            _cursor = target;
        }
    }

    public long Capacity => _end - _start;

    public void Dispose()
        => _map?.Dispose();

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteValue(object? value)
        => CookedBinarySerializer.WriteValue(this, value, allowCustom: true, callbacks: null);

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteBaseObject<T>(T instance) where T : class
        => CookedBinarySerializer.WriteObjectContent(this, instance!, typeof(T), allowCustom: true, callbacks: null);

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(new Span<byte>(_cursor, data.Length));
        _cursor += data.Length;
    }

    public void Write(byte value)
    {
        EnsureCapacity(1);
        *_cursor++ = value;
    }

    public void Write(sbyte value) => WriteUnmanaged(value);
    public void Write(bool value) => WriteUnmanaged(value);
    public void Write(short value) => WriteUnmanaged(value);
    public void Write(ushort value) => WriteUnmanaged(value);
    public void Write(int value) => WriteUnmanaged(value);
    public void Write(uint value) => WriteUnmanaged(value);
    public void Write(long value) => WriteUnmanaged(value);
    public void Write(ulong value) => WriteUnmanaged(value);
    public void Write(float value) => WriteUnmanaged(value);
    public void Write(double value) => WriteUnmanaged(value);
    public void Write(decimal value) => WriteUnmanaged(value);
    public void Write(char value) => WriteUnmanaged(value);

    public void Write(byte[] data)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        WriteBytes(data);
    }

    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Write7BitEncodedInt(byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, new Span<byte>(_cursor, byteCount));
        _cursor += byteCount;
    }

    public void Write(ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Write7BitEncodedInt(byteCount);
        EnsureCapacity(byteCount);
        Encoding.UTF8.GetBytes(value, new Span<byte>(_cursor, byteCount));
        _cursor += byteCount;
    }

    internal void Write7BitEncodedInt(int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            Write((byte)(v | 0x80));
            v >>= 7;
        }
        Write((byte)v);
    }

    private void EnsureCapacity(int count)
    {
        if (_cursor + count > _end)
            throw new InvalidOperationException("Cooked binary writer exceeded allocated buffer.");
    }

    private void WriteUnmanaged<T>(T value) where T : unmanaged
    {
        int size = sizeof(T);
        EnsureCapacity(size);
        Unsafe.WriteUnaligned(_cursor, value);
        _cursor += size;
    }
}

public sealed unsafe class CookedBinaryReader : IDisposable
{
    private readonly FileMap? _map;
    private readonly byte* _start;
    private byte* _cursor;
    private readonly byte* _end;

    internal CookedBinaryReader(byte* buffer, int length, FileMap? map = null)
    {
        _map = map;
        _start = buffer;
        _cursor = buffer;
        _end = buffer + length;
    }

    public int Remaining => (int)(_end - _cursor);

    public void Dispose()
        => _map?.Dispose();

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public T? ReadValue<T>()
        => (T?)CookedBinarySerializer.ReadValue(this, typeof(T), callbacks: null);

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void ReadBaseObject<T>(T instance) where T : class
        => CookedBinarySerializer.ReadObjectContent(this, instance!, typeof(T), callbacks: null);

    public byte ReadByte()
    {
        EnsureAvailable(1);
        return *_cursor++;
    }

    public sbyte ReadSByte() => ReadUnmanaged<sbyte>();
    public bool ReadBoolean() => ReadUnmanaged<bool>();
    public short ReadInt16() => ReadUnmanaged<short>();
    public ushort ReadUInt16() => ReadUnmanaged<ushort>();
    public int ReadInt32() => ReadUnmanaged<int>();
    public uint ReadUInt32() => ReadUnmanaged<uint>();
    public long ReadInt64() => ReadUnmanaged<long>();
    public ulong ReadUInt64() => ReadUnmanaged<ulong>();
    public float ReadSingle() => ReadUnmanaged<float>();
    public double ReadDouble() => ReadUnmanaged<double>();
    public decimal ReadDecimal() => ReadUnmanaged<decimal>();
    public char ReadChar() => ReadUnmanaged<char>();

    public string ReadString()
    {
        int length = Read7BitEncodedInt();
        if (length == 0)
            return string.Empty;

        EnsureAvailable(length);
        string value = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(_cursor, length));
        _cursor += length;
        return value;
    }

    public long Position
    {
        get => _cursor - _start;
        set
        {
            byte* target = _start + value;
            if (target < _start || target > _end)
                throw new ArgumentOutOfRangeException(nameof(value));
            _cursor = target;
        }
    }

    public long Length => _end - _start;

    public byte[] ReadBytes(int length)
    {
        EnsureAvailable(length);
        byte[] result = new byte[length];
        new ReadOnlySpan<byte>(_cursor, length).CopyTo(result);
        _cursor += length;
        return result;
    }

    public void ReadBytes(void* destination, int length)
    {
        if (length <= 0)
            return;
        EnsureAvailable(length);
        Unsafe.CopyBlockUnaligned(destination, _cursor, (uint)length);
        _cursor += length;
    }

    public void SkipBytes(int length)
    {
        if (length <= 0)
            return;
        EnsureAvailable(length);
        _cursor += length;
    }

    internal int Read7BitEncodedInt()
    {
        int count = 0;
        int shift = 0;
        byte b;
        do
        {
            if (shift >= 35)
                throw new FormatException("7-bit encoded int is too large.");

            b = ReadByte();
            count |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return count;
    }

    private void EnsureAvailable(int count)
    {
        if (_cursor + count > _end)
            throw new EndOfStreamException("Attempted to read beyond the end of the cooked buffer.");
    }

    private T ReadUnmanaged<T>() where T : unmanaged
    {
        int size = sizeof(T);
        EnsureAvailable(size);
        T value = Unsafe.ReadUnaligned<T>(_cursor);
        _cursor += size;
        return value;
    }
}

public static class CookedBinarySerializer
{
    internal const string ReflectionWarningMessage = "Cooked binary serialization relies on reflection and cannot be statically analyzed for trimming or AOT";

    // Prevent MemoryPack from re-entering itself (XRAsset envelope calls back into cooked serialization).
    private static readonly AsyncLocal<int> MemoryPackRecursionDepth = new();
    private static bool IsMemoryPackSuppressed => MemoryPackRecursionDepth.Value > 0;

    private readonly struct MemoryPackRecursionScope : IDisposable
    {
        public MemoryPackRecursionScope() => MemoryPackRecursionDepth.Value++;
        public void Dispose() => MemoryPackRecursionDepth.Value--;
    }

    internal static T ExecuteWithMemoryPackSuppressed<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        using var _ = new MemoryPackRecursionScope();
        return func();
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static byte[] Serialize(object? value, CookedBinarySerializationCallbacks? callbacks = null)
    {
        long length = CalculateSize(value, callbacks);
        if (length > int.MaxValue)
            throw new InvalidOperationException($"Cooked payload exceeds maximum supported size ({length} bytes).");

        byte[] buffer = new byte[(int)length];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                using var writer = new CookedBinaryWriter(ptr, buffer.Length);
                WriteValue(writer, value, allowCustom: true, callbacks);
            }
        }

        return buffer;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static object? Deserialize(Type expectedType, ReadOnlySpan<byte> data, CookedBinarySerializationCallbacks? callbacks = null)
    {
        unsafe
        {
            fixed (byte* ptr = data)
            {
                using var reader = new CookedBinaryReader(ptr, data.Length);
                object? value = ReadValue(reader, expectedType, callbacks);
                return ConvertValue(value, expectedType);
            }
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static long CalculateSize(object? value, CookedBinarySerializationCallbacks? callbacks = null)
    {
        CookedBinarySizeCalculator calculator = new(callbacks);
        calculator.AddValue(value, allowCustom: true);
        return calculator.Length;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static long CalculateBaseObjectSize(object instance, Type metadataType)
    {
        CookedBinarySizeCalculator calculator = new(callbacks: null);
        calculator.AddObjectContent(instance, metadataType, allowCustom: true);
        return calculator.Length;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteValue(CookedBinaryWriter writer, object? value, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        value = callbacks?.OnSerializingValue?.Invoke(value) ?? value;

        if (value is null)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Null);
            return;
        }

        Type runtimeType = value.GetType();

        if (runtimeType == typeof(bool))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Boolean);
            writer.Write((bool)value);
            return;
        }

        if (runtimeType == typeof(byte))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Byte);
            writer.Write((byte)value);
            return;
        }

        if (runtimeType == typeof(sbyte))
        {
            writer.Write((byte)CookedBinaryTypeMarker.SByte);
            writer.Write((sbyte)value);
            return;
        }

        if (runtimeType == typeof(short))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Int16);
            writer.Write((short)value);
            return;
        }

        if (runtimeType == typeof(ushort))
        {
            writer.Write((byte)CookedBinaryTypeMarker.UInt16);
            writer.Write((ushort)value);
            return;
        }

        if (runtimeType == typeof(int))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Int32);
            writer.Write((int)value);
            return;
        }

        if (runtimeType == typeof(uint))
        {
            writer.Write((byte)CookedBinaryTypeMarker.UInt32);
            writer.Write((uint)value);
            return;
        }

        if (runtimeType == typeof(long))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Int64);
            writer.Write((long)value);
            return;
        }

        if (runtimeType == typeof(ulong))
        {
            writer.Write((byte)CookedBinaryTypeMarker.UInt64);
            writer.Write((ulong)value);
            return;
        }

        if (runtimeType == typeof(float))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Single);
            writer.Write((float)value);
            return;
        }

        if (runtimeType == typeof(double))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Double);
            writer.Write((double)value);
            return;
        }

        if (runtimeType == typeof(decimal))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Decimal);
            writer.Write((decimal)value);
            return;
        }

        if (runtimeType == typeof(char))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Char);
            writer.Write((char)value);
            return;
        }

        if (value is string s)
        {
            writer.Write((byte)CookedBinaryTypeMarker.String);
            writer.Write(s);
            return;
        }

        if (value is Guid guid)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Guid);
            writer.Write(guid.ToByteArray());
            return;
        }

        if (value is DateTime dateTime)
        {
            writer.Write((byte)CookedBinaryTypeMarker.DateTime);
            writer.Write(dateTime.ToBinary());
            return;
        }

        if (value is TimeSpan timeSpan)
        {
            writer.Write((byte)CookedBinaryTypeMarker.TimeSpan);
            writer.Write(timeSpan.Ticks);
            return;
        }

        if (value is byte[] bytes)
        {
            writer.Write((byte)CookedBinaryTypeMarker.ByteArray);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            return;
        }

        if (value is DataSource dataSource)
        {
            writer.Write((byte)CookedBinaryTypeMarker.DataSource);
            byte[] blob = dataSource.GetBytes();
            writer.Write(blob.Length);
            writer.Write(blob);
            return;
        }

        if (runtimeType.IsEnum)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Enum);
            WriteTypeName(writer, runtimeType);
            writer.Write(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return;
        }

        if (runtimeType.IsArray && value is Array array)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Array);
            Type elementType = runtimeType.GetElementType() ?? typeof(object);
            WriteTypeName(writer, elementType);
            writer.Write(array.Length);
            foreach (object? item in array)
            {
                WriteValue(writer, item, allowCustom, callbacks);
            }

            return;
        }

        if (value is IDictionary dictionary)
        {
            writer.Write((byte)CookedBinaryTypeMarker.Dictionary);
            WriteTypeName(writer, runtimeType);
            writer.Write(dictionary.Count);
            foreach (DictionaryEntry entry in dictionary)
            {
                WriteValue(writer, entry.Key, allowCustom, callbacks);
                WriteValue(writer, entry.Value, allowCustom, callbacks);
            }

            return;
        }

        if (value is IList list)
        {
            writer.Write((byte)CookedBinaryTypeMarker.List);
            WriteTypeName(writer, runtimeType);
            writer.Write(list.Count);
            foreach (object? item in list)
                WriteValue(writer, item, allowCustom, callbacks);
            return;
        }

        if (allowCustom && value is ICookedBinarySerializable custom)
        {
            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            custom.WriteCookedBinary(writer);
            return;
        }

        writer.Write((byte)CookedBinaryTypeMarker.Object);
        WriteTypeName(writer, runtimeType);
        if (TrySerializeWithMemoryPack(value, runtimeType, out byte[]? memPackBytes))
        {
            writer.Write((byte)CookedBinaryObjectEncoding.MemoryPack);
            writer.Write(memPackBytes!.Length);
            writer.Write(memPackBytes);
        }
        else
        {
            writer.Write((byte)CookedBinaryObjectEncoding.Reflection);
            using var scope = EnterReflectionScope(value, out bool isCycle);
            if (!isCycle)
                WriteObjectContent(writer, value, runtimeType, allowCustom, callbacks);
            else
                writer.Write(0); // zero members to break the cycle
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static object? ReadValue(CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks)
    {
        var marker = (CookedBinaryTypeMarker)reader.ReadByte();
        var value = marker switch
        {
            CookedBinaryTypeMarker.Null => null,
            CookedBinaryTypeMarker.Boolean => reader.ReadBoolean(),
            CookedBinaryTypeMarker.Byte => reader.ReadByte(),
            CookedBinaryTypeMarker.SByte => reader.ReadSByte(),
            CookedBinaryTypeMarker.Int16 => reader.ReadInt16(),
            CookedBinaryTypeMarker.UInt16 => reader.ReadUInt16(),
            CookedBinaryTypeMarker.Int32 => reader.ReadInt32(),
            CookedBinaryTypeMarker.UInt32 => reader.ReadUInt32(),
            CookedBinaryTypeMarker.Int64 => reader.ReadInt64(),
            CookedBinaryTypeMarker.UInt64 => reader.ReadUInt64(),
            CookedBinaryTypeMarker.Single => reader.ReadSingle(),
            CookedBinaryTypeMarker.Double => reader.ReadDouble(),
            CookedBinaryTypeMarker.Decimal => reader.ReadDecimal(),
            CookedBinaryTypeMarker.Char => reader.ReadChar(),
            CookedBinaryTypeMarker.String => reader.ReadString(),
            CookedBinaryTypeMarker.Guid => new Guid(reader.ReadBytes(16)),
            CookedBinaryTypeMarker.DateTime => DateTime.FromBinary(reader.ReadInt64()),
            CookedBinaryTypeMarker.TimeSpan => new TimeSpan(reader.ReadInt64()),
            CookedBinaryTypeMarker.ByteArray => ReadByteArray(reader),
            CookedBinaryTypeMarker.Enum => ReadEnum(reader),
            CookedBinaryTypeMarker.Array => ReadArray(reader, callbacks),
            CookedBinaryTypeMarker.List => ReadList(reader, callbacks),
            CookedBinaryTypeMarker.Dictionary => ReadDictionary(reader, callbacks),
            CookedBinaryTypeMarker.CustomObject => ReadCustomObject(reader, expectedType, callbacks),
            CookedBinaryTypeMarker.DataSource => ReadDataSource(reader),
            CookedBinaryTypeMarker.Object => ReadObject(reader, callbacks),
            _ => throw new NotSupportedException($"Unknown cooked binary marker '{marker}'.")
        };

        return callbacks?.OnDeserializedValue?.Invoke(value) ?? value;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteObjectContent(CookedBinaryWriter writer, object instance, Type metadataType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        var metadata = TypeMetadataCache.Get(metadataType);
        writer.Write(metadata.Members.Length);
        foreach (var member in metadata.Members)
        {
            writer.Write(member.Name);
            object? value = member.GetValue(instance);
            WriteValue(writer, value, allowCustom, callbacks);
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void ReadObjectContent(CookedBinaryReader reader, object instance, Type metadataType, CookedBinarySerializationCallbacks? callbacks)
    {
        int count = reader.ReadInt32();
        var metadata = TypeMetadataCache.Get(metadataType);
        for (int i = 0; i < count; i++)
        {
            string propertyName = reader.ReadString();
            if (metadata.TryGetMember(propertyName, out var member))
            {
                object? value = ReadValue(reader, null, callbacks);
                object? converted = ConvertValue(value, member.MemberType);
                member.SetValue(instance, converted);
            }
            else
            {
                SkipValue(reader);
            }
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static void SkipValue(CookedBinaryReader reader)
    {
        var marker = (CookedBinaryTypeMarker)reader.ReadByte();
        switch (marker)
        {
            case CookedBinaryTypeMarker.Null:
                return;
            case CookedBinaryTypeMarker.Boolean:
                reader.ReadBoolean();
                return;
            case CookedBinaryTypeMarker.Byte:
                reader.ReadByte();
                return;
            case CookedBinaryTypeMarker.SByte:
                reader.ReadSByte();
                return;
            case CookedBinaryTypeMarker.Int16:
                reader.ReadInt16();
                return;
            case CookedBinaryTypeMarker.UInt16:
                reader.ReadUInt16();
                return;
            case CookedBinaryTypeMarker.Int32:
                reader.ReadInt32();
                return;
            case CookedBinaryTypeMarker.UInt32:
                reader.ReadUInt32();
                return;
            case CookedBinaryTypeMarker.Int64:
                reader.ReadInt64();
                return;
            case CookedBinaryTypeMarker.UInt64:
                reader.ReadUInt64();
                return;
            case CookedBinaryTypeMarker.Single:
                reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.Double:
                reader.ReadDouble();
                return;
            case CookedBinaryTypeMarker.Decimal:
                reader.ReadDecimal();
                return;
            case CookedBinaryTypeMarker.Char:
                reader.ReadChar();
                return;
            case CookedBinaryTypeMarker.String:
                reader.ReadString();
                return;
            case CookedBinaryTypeMarker.Guid:
                reader.ReadBytes(16);
                return;
            case CookedBinaryTypeMarker.DateTime:
                reader.ReadInt64();
                return;
            case CookedBinaryTypeMarker.TimeSpan:
                reader.ReadInt64();
                return;
            case CookedBinaryTypeMarker.ByteArray:
            {
                int length = reader.ReadInt32();
                reader.ReadBytes(length);
                return;
            }
            case CookedBinaryTypeMarker.Enum:
                reader.ReadString();
                reader.ReadInt64();
                return;
            case CookedBinaryTypeMarker.Array:
            {
                reader.ReadString(); // element type name
                int length = reader.ReadInt32();
                for (int i = 0; i < length; i++)
                    SkipValue(reader);
                return;
            }
            case CookedBinaryTypeMarker.List:
            {
                reader.ReadString(); // list runtime type name
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                    SkipValue(reader);
                return;
            }
            case CookedBinaryTypeMarker.Dictionary:
            {
                reader.ReadString(); // dict runtime type name
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    SkipValue(reader);
                    SkipValue(reader);
                }
                return;
            }
            case CookedBinaryTypeMarker.CustomObject:
            {
                // No length prefix; fall back to fully reading to advance the stream.
                _ = ReadCustomObject(reader, expectedType: null, callbacks: null);
                return;
            }
            case CookedBinaryTypeMarker.DataSource:
            {
                int length = reader.ReadInt32();
                reader.ReadBytes(length);
                return;
            }
            case CookedBinaryTypeMarker.Object:
            {
                reader.ReadString(); // runtime type name
                var encoding = (CookedBinaryObjectEncoding)reader.ReadByte();
                switch (encoding)
                {
                    case CookedBinaryObjectEncoding.MemoryPack:
                    {
                        int length = reader.ReadInt32();
                        reader.ReadBytes(length);
                        return;
                    }
                    case CookedBinaryObjectEncoding.Reflection:
                    {
                        int members = reader.ReadInt32();
                        for (int i = 0; i < members; i++)
                        {
                            reader.ReadString(); // member name
                            SkipValue(reader);
                        }
                        return;
                    }
                    default:
                        throw new InvalidOperationException($"Unsupported cooked object encoding '{encoding}'.");
                }
            }
            default:
                throw new NotSupportedException($"Unknown cooked binary marker '{marker}'.");
        }
    }

    private static byte[] ReadByteArray(CookedBinaryReader reader)
    {
        int length = reader.ReadInt32();
        return reader.ReadBytes(length);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadEnum(CookedBinaryReader reader)
    {
        string typeName = reader.ReadString();
        Type enumType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve enum type '{typeName}'.");
        long raw = reader.ReadInt64();
        return Enum.ToObject(enumType, raw);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadArray(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string elementTypeName = reader.ReadString();
        Type elementType = ResolveType(elementTypeName) ?? typeof(object);
        int length = reader.ReadInt32();
        Array array = Array.CreateInstance(elementType, length);
        for (int i = 0; i < length; i++)
        {
            object? value = ReadValue(reader, elementType, callbacks);
            array.SetValue(ConvertValue(value, elementType), i);
        }

        return array;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadList(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string listTypeName = reader.ReadString();
        Type listType = ResolveType(listTypeName) ?? typeof(List<object?>);
        IList list = (IList)(CreateInstance(listType) ?? new List<object?>());
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            object? value = ReadValue(reader, null, callbacks);
            list.Add(value);
        }

        return list;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadDictionary(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string dictTypeName = reader.ReadString();
        Type dictType = ResolveType(dictTypeName) ?? typeof(Dictionary<object, object?>);
        IDictionary dictionary = (IDictionary)(CreateInstance(dictType) ?? new Dictionary<object, object?>());
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            object? key = ReadValue(reader, null, callbacks);
            object? value = ReadValue(reader, null, callbacks);
            dictionary[key!] = value;
        }

        return dictionary;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadCustomObject(CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName) ?? expectedType ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
        if (CreateInstance(targetType) is not ICookedBinarySerializable instance)
            throw new InvalidOperationException($"Type '{targetType}' does not implement {nameof(ICookedBinarySerializable)}.");

        instance.ReadCookedBinary(reader);
        return instance;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadObject(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
        CookedBinaryObjectEncoding encoding = (CookedBinaryObjectEncoding)reader.ReadByte();
        return encoding switch
        {
            CookedBinaryObjectEncoding.MemoryPack => ReadMemoryPackObject(reader, targetType),
            CookedBinaryObjectEncoding.Reflection => ReadReflectionObject(reader, targetType, callbacks),
            _ => throw new InvalidOperationException($"Unsupported cooked object encoding '{encoding}'.")
        };
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadReflectionObject(CookedBinaryReader reader, Type targetType, CookedBinarySerializationCallbacks? callbacks)
    {
        object instance = CreateInstance(targetType) ?? throw new InvalidOperationException($"Unable to create instance of '{targetType}'.");
        ReadObjectContent(reader, instance, targetType, callbacks);
        return instance;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadMemoryPackObject(CookedBinaryReader reader, Type targetType)
    {
        int length = reader.ReadInt32();
        byte[] payload = reader.ReadBytes(length);
        if (typeof(XRAsset).IsAssignableFrom(targetType))
        {
            XRAsset? asset = XRAssetMemoryPackAdapter.Deserialize(payload, targetType);
            if (asset is null)
                throw new InvalidOperationException($"MemoryPack deserialization returned null for asset type '{targetType}'.");
            return asset;
        }

        object? instance = MemoryPackSerializer.Deserialize(targetType, payload);
        if (instance is null)
            throw new InvalidOperationException($"MemoryPack deserialization returned null for type '{targetType}'.");
        return instance;
    }

    private static object ReadDataSource(CookedBinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] data = reader.ReadBytes(length);
        return new DataSource(data);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
            return targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null
                ? Activator.CreateInstance(targetType)
                : null;

        Type valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType))
            return value;

        Type? nullable = Nullable.GetUnderlyingType(targetType);
        if (nullable is not null)
            return ConvertValue(value, nullable);

        if (targetType.IsEnum)
            return Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));

        if (targetType == typeof(Guid))
        {
            if (value is byte[] bytes && bytes.Length == 16)
                return new Guid(bytes);
            if (value is string s && Guid.TryParse(s, out Guid guid))
                return guid;
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static void WriteTypeName(CookedBinaryWriter writer, Type type)
        => writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? CreateInstance(Type type)
    {
        try
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);

            ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor is not null)
                return ctor.Invoke(null);

            return null;
        }
        catch
        {
            return null;
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static Type ResolveType(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Cooked binary payload is missing a type hint.");

        // Back-compat: allow types to redirect legacy names via [XRTypeRedirect].
        name = XRTypeRedirectRegistry.RewriteTypeName(name);

        return TypeCache.GetOrAdd(name, static key =>
        {
            Type? resolved = Type.GetType(key, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
                return resolved;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resolved = assembly.GetType(key, throwOnError: false, ignoreCase: false);
                if (resolved is not null)
                    return resolved;
            }

            throw new InvalidOperationException($"Unable to resolve type '{key}'.");
        });
    }

    private static readonly ConcurrentDictionary<string, Type> TypeCache = new();

    private static readonly AsyncLocal<ReferenceLoopGuard?> ReflectionLoopGuard = new();

    private static ReferenceLoopScope EnterReflectionScope(object instance, out bool isCycle)
    {
        var guard = ReflectionLoopGuard.Value ??= new ReferenceLoopGuard();
        return guard.Enter(instance, out isCycle);
    }

    private sealed class ReferenceLoopGuard
    {
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public ReferenceLoopScope Enter(object instance, out bool isCycle)
        {
            if (!_seen.Add(instance))
            {
                isCycle = true;
                return new ReferenceLoopScope(this, null);
            }

            isCycle = false;
            return new ReferenceLoopScope(this, instance);
        }

        public void Exit(object instance)
            => _seen.Remove(instance);
    }

    private readonly struct ReferenceLoopScope : IDisposable
    {
        private readonly ReferenceLoopGuard _guard;
        private readonly object? _instance;

        public ReferenceLoopScope(ReferenceLoopGuard guard, object? instance)
        {
            _guard = guard;
            _instance = instance;
        }

        public void Dispose()
        {
            if (_instance is not null)
                _guard.Exit(_instance);
        }
    }

    private static bool TrySerializeWithMemoryPack(object value, Type runtimeType, out byte[]? data)
    {
        // Re-entrancy guard prevents XRAsset envelope from recursing back into MemoryPack.
        if (IsMemoryPackSuppressed)
        {
            data = null;
            return false;
        }

        // Scene graph objects form parent/child cycles; avoid MemoryPack to prevent stack overflows.
        if (runtimeType.IsAssignableTo(typeof(SceneNode)) || runtimeType.IsAssignableTo(typeof(TransformBase)))
        {
            data = null;
            return false;
        }

        using var _ = new MemoryPackRecursionScope();

        try
        {
            if (value is XRAsset asset)
            {
                data = XRAssetMemoryPackAdapter.Serialize(asset);
                return true;
            }

            data = MemoryPackSerializer.Serialize(runtimeType, value);
            return data is not null;
        }
        catch (Exception ex) when (IsMemoryPackUnsupported(ex))
        {
            data = null;
            return false;
        }
    }

    private static bool TryGetMemoryPackLength(object value, Type runtimeType, out int length)
    {
        if (!TrySerializeWithMemoryPack(value, runtimeType, out byte[]? data))
        {
            length = 0;
            return false;
        }

        length = data!.Length;
        return true;
    }

    private static bool IsMemoryPackUnsupported(Exception ex)
        => ex is MemoryPackSerializationException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException
            || ex.InnerException is not null && IsMemoryPackUnsupported(ex.InnerException);

    private enum CookedBinaryObjectEncoding : byte
    {
        MemoryPack = 0,
        Reflection = 1
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private sealed class CookedBinarySizeCalculator
    {
        private readonly CookedBinarySerializationCallbacks? _callbacks;
        private long _length;

        public long Length => _length;

        public CookedBinarySizeCalculator(CookedBinarySerializationCallbacks? callbacks)
        {
            _callbacks = callbacks;
        }

        public void AddValue(object? value, bool allowCustom)
        {
            AddBytes(1); // type marker
            value = _callbacks?.OnSerializingValue?.Invoke(value) ?? value;
            if (value is null)
                return;

            switch (value)
            {
                case bool:
                    AddBytes(sizeof(bool));
                    return;
                case byte or sbyte:
                    AddBytes(1);
                    return;
                case short or ushort:
                    AddBytes(2);
                    return;
                case int or uint:
                    AddBytes(4);
                    return;
                case long or ulong:
                    AddBytes(8);
                    return;
                case float:
                    AddBytes(4);
                    return;
                case double:
                    AddBytes(8);
                    return;
                case decimal:
                    AddBytes(16);
                    return;
                case char:
                    AddBytes(sizeof(char));
                    return;
            }

            if (value is string s)
            {
                AddBytes(SizeOfString(s));
                return;
            }

            if (value is Guid)
            {
                AddBytes(16);
                return;
            }

            if (value is DateTime or TimeSpan)
            {
                AddBytes(8);
                return;
            }

            if (value is byte[] bytes)
            {
                AddBytes(sizeof(int) + bytes.Length);
                return;
            }

            if (value is DataSource dataSource)
            {
                AddBytes(sizeof(int) + checked((int)dataSource.Length));
                return;
            }

            Type runtimeType = value.GetType();

            if (runtimeType.IsEnum)
            {
                AddBytes(SizeOfTypeName(runtimeType) + sizeof(long));
                return;
            }

            if (runtimeType.IsArray && value is Array array)
            {
                Type elementType = runtimeType.GetElementType() ?? typeof(object);
                AddBytes(SizeOfTypeName(elementType));
                AddBytes(sizeof(int));
                foreach (object? item in array)
                    AddValue(item, allowCustom);
                return;
            }

            if (value is IDictionary dictionary)
            {
                AddBytes(SizeOfTypeName(runtimeType));
                AddBytes(sizeof(int));
                foreach (DictionaryEntry entry in dictionary)
                {
                    AddValue(entry.Key, allowCustom);
                    AddValue(entry.Value, allowCustom);
                }
                return;
            }

            if (value is IList list)
            {
                AddBytes(SizeOfTypeName(runtimeType));
                AddBytes(sizeof(int));
                foreach (object? item in list)
                    AddValue(item, allowCustom);
                return;
            }

            if (allowCustom && value is ICookedBinarySerializable custom)
            {
                AddBytes(SizeOfTypeName(runtimeType) + custom.CalculateCookedBinarySize());
                return;
            }

            AddBytes(SizeOfTypeName(runtimeType));

            if (TryGetMemoryPackLength(value, runtimeType, out int memoryPackLength))
            {
                AddBytes(1 + sizeof(int) + memoryPackLength);
                return;
            }

            AddBytes(1); // encoding flag
            using var scope = EnterReflectionScope(value, out bool isCycle);
            if (isCycle)
            {
                AddBytes(sizeof(int)); // zero members marker to break the cycle
                return;
            }

            AddObjectContent(value, runtimeType, allowCustom);
        }

        public void AddObjectContent(object instance, Type metadataType, bool allowCustom)
        {
            AddBytes(sizeof(int));
            var metadata = TypeMetadataCache.Get(metadataType);
            foreach (var member in metadata.Members)
            {
                AddBytes(SizeOfString(member.Name));
                object? value = member.GetValue(instance);
                AddValue(value, allowCustom);
            }
        }

        private void AddBytes(long count)
            => _length = checked(_length + count);
    }

    private static long SizeOfTypeName(Type type)
    {
        string name = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        return SizeOfString(name);
    }

    private static long SizeOfString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return SizeOf7BitEncodedInt(byteCount) + byteCount;
    }

    private static int SizeOf7BitEncodedInt(int value)
    {
        uint num = (uint)value;
        int count = 0;
        do
        {
            num >>= 7;
            count++;
        } while (num != 0);
        return count;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private sealed class TypeMetadata
    {
        public TypeMetadata(Type type)
        {
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  .Where(p => p.CanRead && p.CanWrite)
                                  .Where(p => p.GetIndexParameters().Length == 0)
                                  .Where(p => p.GetCustomAttribute<YamlIgnoreAttribute>() is null)
                                  .Select(p => new MemberMetadata(p))
                                  .Where(m => m.IsValid)
                                  .ToArray();

            Members = properties;
            _membersByName = properties.ToDictionary(m => m.Name, StringComparer.Ordinal);
        }

        private readonly Dictionary<string, MemberMetadata> _membersByName;

        public MemberMetadata[] Members { get; }

        public bool TryGetMember(string name, out MemberMetadata metadata)
            => _membersByName.TryGetValue(name, out metadata!);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private sealed class MemberMetadata
    {
        public MemberMetadata(PropertyInfo property)
        {
            Name = property.Name;
            MemberType = property.PropertyType;
            Getter = property.GetGetMethod(true);
            Setter = property.GetSetMethod(true);
            IsValid = Getter is not null && Setter is not null;
        }

        public string Name { get; }
        public Type MemberType { get; }
        public MethodInfo? Getter { get; }
        public MethodInfo? Setter { get; }
        public bool IsValid { get; }

        public object? GetValue(object instance)
            => Getter?.Invoke(instance, null);

        public void SetValue(object instance, object? value)
        {
            if (Setter is null)
                return;

            Setter.Invoke(instance, new[] { value });
        }
    }

    private static class TypeMetadataCache
    {
        private static readonly ConcurrentDictionary<Type, TypeMetadata> Cache = new();

        [RequiresUnreferencedCode(ReflectionWarningMessage)]
        [RequiresDynamicCode(ReflectionWarningMessage)]
        public static TypeMetadata Get(Type type)
            => Cache.GetOrAdd(type, static t => new TypeMetadata(t));
    }

}
