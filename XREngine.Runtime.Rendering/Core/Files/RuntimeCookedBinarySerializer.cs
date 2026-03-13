using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using XREngine.Data.Core;

namespace XREngine.Core.Files;

public interface IRuntimeCookedBinarySerializable
{
    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    void WriteCookedBinary(RuntimeCookedBinaryWriter writer);

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    void ReadCookedBinary(RuntimeCookedBinaryReader reader);

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    long CalculateCookedBinarySize();
}

internal enum RuntimeCookedBinaryTypeMarker : byte
{
    Null = 0,
    Boolean = 1,
    Byte = 2,
    SByte = 3,
    Int16 = 4,
    UInt16 = 5,
    Int32 = 6,
    UInt32 = 7,
    Int64 = 8,
    UInt64 = 9,
    Single = 10,
    Double = 11,
    Decimal = 12,
    Char = 13,
    String = 14,
    Guid = 15,
    DateTime = 16,
    Vector2 = 17,
    Vector3 = 18,
    Vector4 = 19,
    Quaternion = 20,
    Matrix4x4 = 21,
    ByteArray = 22,
    Enum = 23,
    Array = 24,
    CustomObject = 25,
}

public sealed unsafe class RuntimeCookedBinaryWriter(byte* buffer, long length) : IDisposable
{
    private readonly byte* _start = buffer;
    private byte* _cursor = buffer;
    private readonly byte* _end = buffer + length;

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

    public void Dispose()
    {
    }

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteValue(object? value)
        => RuntimeCookedBinarySerializer.WriteValue(this, value);

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteBaseObject<T>(T instance) where T : class
        => RuntimeCookedBinarySerializer.WriteBaseObject(this, instance!, typeof(T));

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
        ArgumentNullException.ThrowIfNull(data);
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
            throw new InvalidOperationException("Runtime cooked binary writer exceeded allocated buffer.");
    }

    private void WriteUnmanaged<T>(T value) where T : unmanaged
    {
        int size = sizeof(T);
        EnsureCapacity(size);
        Unsafe.WriteUnaligned(_cursor, value);
        _cursor += size;
    }
}

public sealed unsafe class RuntimeCookedBinaryReader(byte* buffer, long length) : IDisposable
{
    private readonly byte* _start = buffer;
    private byte* _cursor = buffer;
    private readonly byte* _end = buffer + length;

    public long Remaining => _end - _cursor;
    public long Length => _end - _start;

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

    public void Dispose()
    {
    }

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    public T? ReadValue<T>()
        => (T?)RuntimeCookedBinarySerializer.ReadValue(this, typeof(T));

    [RequiresUnreferencedCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(RuntimeCookedBinarySerializer.ReflectionWarningMessage)]
    public void ReadBaseObject<T>(T instance) where T : class
        => RuntimeCookedBinarySerializer.ReadBaseObject(this, instance!, typeof(T));

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
            throw new EndOfStreamException("Attempted to read beyond the end of the runtime cooked buffer.");
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

public static class RuntimeCookedBinarySerializer
{
    internal const string ReflectionWarningMessage = "Runtime cooked binary serialization relies on reflection and cannot be statically analyzed for trimming or AOT";

    private static readonly AsyncLocal<int> RecursionDepth = new();
    private static readonly PropertyInfo? XRObjectIdProperty = typeof(XRObjectBase)
        .GetProperty(nameof(XRObjectBase.ID), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static unsafe byte[] Serialize(object? value)
    {
        long length = CalculateSize(value);
        if (length > int.MaxValue)
            throw new InvalidOperationException($"Runtime cooked payload exceeds maximum supported size ({length} bytes).");

        byte[] buffer = new byte[(int)length];
        fixed (byte* ptr = buffer)
        {
            using RuntimeCookedBinaryWriter writer = new(ptr, buffer.Length);
            WriteValue(writer, value);
        }

        return buffer;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static unsafe object? Deserialize(Type expectedType, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(expectedType);

        fixed (byte* ptr = data)
        {
            using RuntimeCookedBinaryReader reader = new(ptr, data.Length);
            return ReadValue(reader, expectedType);
        }
    }

    public static T ExecuteWithMemoryPackSuppressed<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        RecursionDepth.Value++;
        try
        {
            return func();
        }
        finally
        {
            RecursionDepth.Value--;
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static long CalculateSize(object? value)
        => CalculateValueSize(value);

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static long CalculateBaseObjectSize(object instance, Type metadataType)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(metadataType);

        if (instance is not XRAsset asset || metadataType != typeof(XRAsset))
            throw new NotSupportedException($"Runtime cooked base-object serialization only supports '{typeof(XRAsset)}'.");

        return CalculateSize(asset.ID)
            + CalculateSize(asset.Name)
            + CalculateSize(asset.FilePath)
            + CalculateSize(asset.OriginalPath)
            + CalculateSize(asset.OriginalLastWriteTimeUtc);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteBaseObject(RuntimeCookedBinaryWriter writer, object instance, Type metadataType)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(metadataType);

        if (instance is not XRAsset asset || metadataType != typeof(XRAsset))
            throw new NotSupportedException($"Runtime cooked base-object serialization only supports '{typeof(XRAsset)}'.");

        writer.WriteValue(asset.ID);
        writer.WriteValue(asset.Name);
        writer.WriteValue(asset.FilePath);
        writer.WriteValue(asset.OriginalPath);
        writer.WriteValue(asset.OriginalLastWriteTimeUtc);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void ReadBaseObject(RuntimeCookedBinaryReader reader, object instance, Type metadataType)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(metadataType);

        if (instance is not XRAsset asset || metadataType != typeof(XRAsset))
            throw new NotSupportedException($"Runtime cooked base-object serialization only supports '{typeof(XRAsset)}'.");

        using IDisposable suppression = XRBase.SuppressPropertyNotifications();
        Guid? id = reader.ReadValue<Guid>();
        if (id.HasValue && XRObjectIdProperty?.SetMethod is not null)
            XRObjectIdProperty.SetValue(asset, id.Value);

        asset.Name = reader.ReadValue<string>();
        asset.FilePath = reader.ReadValue<string>();
        asset.OriginalPath = reader.ReadValue<string>();
        asset.OriginalLastWriteTimeUtc = reader.ReadValue<DateTime?>();
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteValue(RuntimeCookedBinaryWriter writer, object? value)
    {
        if (value is null)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Null);
            return;
        }

        if (value is IRuntimeCookedBinarySerializable custom)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, value.GetType());
            custom.WriteCookedBinary(writer);
            return;
        }

        Type runtimeType = value.GetType();
        if (runtimeType.IsEnum)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Enum);
            WriteTypeName(writer, runtimeType);
            writer.Write(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return;
        }

        switch (Type.GetTypeCode(runtimeType))
        {
            case TypeCode.Boolean:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Boolean);
                writer.Write((bool)value);
                return;
            case TypeCode.Byte:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Byte);
                writer.Write((byte)value);
                return;
            case TypeCode.SByte:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.SByte);
                writer.Write((sbyte)value);
                return;
            case TypeCode.Int16:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Int16);
                writer.Write((short)value);
                return;
            case TypeCode.UInt16:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.UInt16);
                writer.Write((ushort)value);
                return;
            case TypeCode.Int32:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Int32);
                writer.Write((int)value);
                return;
            case TypeCode.UInt32:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.UInt32);
                writer.Write((uint)value);
                return;
            case TypeCode.Int64:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Int64);
                writer.Write((long)value);
                return;
            case TypeCode.UInt64:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.UInt64);
                writer.Write((ulong)value);
                return;
            case TypeCode.Single:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Single);
                writer.Write((float)value);
                return;
            case TypeCode.Double:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Double);
                writer.Write((double)value);
                return;
            case TypeCode.Decimal:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Decimal);
                writer.Write((decimal)value);
                return;
            case TypeCode.Char:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.Char);
                writer.Write((char)value);
                return;
            case TypeCode.String:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.String);
                writer.Write((string)value);
                return;
            case TypeCode.DateTime:
                writer.Write((byte)RuntimeCookedBinaryTypeMarker.DateTime);
                writer.Write(((DateTime)value).ToBinary());
                return;
        }

        if (value is Guid guid)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Guid);
            writer.Write(guid.ToByteArray());
            return;
        }

        if (value is Vector2 vector2)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Vector2);
            writer.Write(vector2.X);
            writer.Write(vector2.Y);
            return;
        }

        if (value is Vector3 vector3)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Vector3);
            writer.Write(vector3.X);
            writer.Write(vector3.Y);
            writer.Write(vector3.Z);
            return;
        }

        if (value is Vector4 vector4)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Vector4);
            writer.Write(vector4.X);
            writer.Write(vector4.Y);
            writer.Write(vector4.Z);
            writer.Write(vector4.W);
            return;
        }

        if (value is Quaternion quaternion)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Quaternion);
            writer.Write(quaternion.X);
            writer.Write(quaternion.Y);
            writer.Write(quaternion.Z);
            writer.Write(quaternion.W);
            return;
        }

        if (value is Matrix4x4 matrix)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Matrix4x4);
            writer.Write(matrix.M11);
            writer.Write(matrix.M12);
            writer.Write(matrix.M13);
            writer.Write(matrix.M14);
            writer.Write(matrix.M21);
            writer.Write(matrix.M22);
            writer.Write(matrix.M23);
            writer.Write(matrix.M24);
            writer.Write(matrix.M31);
            writer.Write(matrix.M32);
            writer.Write(matrix.M33);
            writer.Write(matrix.M34);
            writer.Write(matrix.M41);
            writer.Write(matrix.M42);
            writer.Write(matrix.M43);
            writer.Write(matrix.M44);
            return;
        }

        if (value is byte[] bytes)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.ByteArray);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            return;
        }

        if (value is Array array)
        {
            writer.Write((byte)RuntimeCookedBinaryTypeMarker.Array);
            WriteTypeName(writer, runtimeType);
            writer.Write(array.Length);
            foreach (object? item in array)
                WriteValue(writer, item);
            return;
        }

        throw new NotSupportedException($"Runtime cooked binary serialization does not support '{runtimeType.FullName ?? runtimeType.Name}'.");
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static object? ReadValue(RuntimeCookedBinaryReader reader, Type? expectedType)
    {
        RuntimeCookedBinaryTypeMarker marker = (RuntimeCookedBinaryTypeMarker)reader.ReadByte();
        object? value = marker switch
        {
            RuntimeCookedBinaryTypeMarker.Null => null,
            RuntimeCookedBinaryTypeMarker.Boolean => reader.ReadBoolean(),
            RuntimeCookedBinaryTypeMarker.Byte => reader.ReadByte(),
            RuntimeCookedBinaryTypeMarker.SByte => reader.ReadSByte(),
            RuntimeCookedBinaryTypeMarker.Int16 => reader.ReadInt16(),
            RuntimeCookedBinaryTypeMarker.UInt16 => reader.ReadUInt16(),
            RuntimeCookedBinaryTypeMarker.Int32 => reader.ReadInt32(),
            RuntimeCookedBinaryTypeMarker.UInt32 => reader.ReadUInt32(),
            RuntimeCookedBinaryTypeMarker.Int64 => reader.ReadInt64(),
            RuntimeCookedBinaryTypeMarker.UInt64 => reader.ReadUInt64(),
            RuntimeCookedBinaryTypeMarker.Single => reader.ReadSingle(),
            RuntimeCookedBinaryTypeMarker.Double => reader.ReadDouble(),
            RuntimeCookedBinaryTypeMarker.Decimal => reader.ReadDecimal(),
            RuntimeCookedBinaryTypeMarker.Char => reader.ReadChar(),
            RuntimeCookedBinaryTypeMarker.String => reader.ReadString(),
            RuntimeCookedBinaryTypeMarker.Guid => new Guid(reader.ReadBytes(16)),
            RuntimeCookedBinaryTypeMarker.DateTime => DateTime.FromBinary(reader.ReadInt64()),
            RuntimeCookedBinaryTypeMarker.Vector2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            RuntimeCookedBinaryTypeMarker.Vector3 => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            RuntimeCookedBinaryTypeMarker.Vector4 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            RuntimeCookedBinaryTypeMarker.Quaternion => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            RuntimeCookedBinaryTypeMarker.Matrix4x4 => ReadMatrix(reader),
            RuntimeCookedBinaryTypeMarker.ByteArray => reader.ReadBytes(reader.ReadInt32()),
            RuntimeCookedBinaryTypeMarker.Enum => ReadEnum(reader, expectedType),
            RuntimeCookedBinaryTypeMarker.Array => ReadArray(reader, expectedType),
            RuntimeCookedBinaryTypeMarker.CustomObject => ReadCustomObject(reader, expectedType),
            _ => throw new NotSupportedException($"Unknown runtime cooked binary marker '{marker}'."),
        };

        return expectedType is null ? value : ConvertValue(value, expectedType);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadEnum(RuntimeCookedBinaryReader reader, Type? expectedType)
    {
        string typeName = reader.ReadString();
        Type enumType = ResolveType(typeName) ?? UnwrapNullable(expectedType) ?? throw new InvalidOperationException($"Unable to resolve runtime cooked enum '{typeName}'.");
        return Enum.ToObject(enumType, reader.ReadInt64());
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadArray(RuntimeCookedBinaryReader reader, Type? expectedType)
    {
        string typeName = reader.ReadString();
        Type arrayType = ResolveType(typeName) ?? expectedType ?? throw new InvalidOperationException($"Unable to resolve runtime cooked array '{typeName}'.");
        Type? elementType = arrayType.GetElementType() ?? expectedType?.GetElementType();
        if (elementType is null)
            throw new InvalidOperationException($"Runtime cooked payload type '{arrayType}' is not an array.");

        int length = reader.ReadInt32();
        Array array = Array.CreateInstance(elementType, length);
        for (int i = 0; i < length; i++)
        {
            object? item = ReadValue(reader, elementType);
            array.SetValue(ConvertValue(item, elementType), i);
        }

        return array;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadCustomObject(RuntimeCookedBinaryReader reader, Type? expectedType)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName) ?? UnwrapNullable(expectedType) ?? throw new InvalidOperationException($"Unable to resolve runtime cooked custom type '{typeName}'.");
        object? instance = CreateInstance(targetType);
        if (instance is not IRuntimeCookedBinarySerializable custom)
            throw new InvalidOperationException($"Type '{targetType}' does not implement {nameof(IRuntimeCookedBinarySerializable)}.");

        custom.ReadCookedBinary(reader);
        return instance;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static long CalculateValueSize(object? value)
    {
        if (value is null)
            return 1;

        if (value is IRuntimeCookedBinarySerializable custom)
            return 1 + SizeOfTypeName(value.GetType()) + custom.CalculateCookedBinarySize();

        Type runtimeType = value.GetType();
        if (runtimeType.IsEnum)
            return 1 + SizeOfTypeName(runtimeType) + sizeof(long);

        switch (Type.GetTypeCode(runtimeType))
        {
            case TypeCode.Boolean:
            case TypeCode.Byte:
            case TypeCode.SByte:
                return 2;
            case TypeCode.Int16:
            case TypeCode.UInt16:
                return 1 + sizeof(short);
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Single:
                return 1 + sizeof(int);
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Double:
            case TypeCode.DateTime:
                return 1 + sizeof(long);
            case TypeCode.Decimal:
                return 1 + sizeof(decimal);
            case TypeCode.Char:
                return 1 + sizeof(char);
            case TypeCode.String:
                return 1 + SizeOfString((string)value);
        }

        if (value is Guid)
            return 1 + 16;
        if (value is Vector2)
            return 1 + sizeof(float) * 2;
        if (value is Vector3)
            return 1 + sizeof(float) * 3;
        if (value is Vector4 or Quaternion)
            return 1 + sizeof(float) * 4;
        if (value is Matrix4x4)
            return 1 + sizeof(float) * 16;
        if (value is byte[] bytes)
            return 1 + sizeof(int) + bytes.Length;
        if (value is Array array)
        {
            long size = 1 + SizeOfTypeName(runtimeType) + sizeof(int);
            foreach (object? item in array)
                size += CalculateValueSize(item);
            return size;
        }

        throw new NotSupportedException($"Runtime cooked binary size calculation does not support '{runtimeType.FullName ?? runtimeType.Name}'.");
    }

    private static Matrix4x4 ReadMatrix(RuntimeCookedBinaryReader reader)
        => new(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteTypeName(RuntimeCookedBinaryWriter writer, Type type)
        => writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

    private static long SizeOfTypeName(Type type)
        => SizeOfString(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);

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
        }
        while (num != 0);
        return count;
    }

    private static Type? ResolveType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        Type? resolved = Type.GetType(name, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
            return resolved;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(name, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static Type? UnwrapNullable(Type? type)
        => type is null ? null : Nullable.GetUnderlyingType(type) ?? type;

    private static object? CreateInstance(Type type)
    {
        try
        {
            if (type.IsValueType)
                return Activator.CreateInstance(type);

            ConstructorInfo? ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null);
            return ctor?.Invoke(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null)
        {
            Type? nullableType = Nullable.GetUnderlyingType(targetType);
            return targetType.IsValueType && nullableType is null
                ? Activator.CreateInstance(targetType)
                : null;
        }

        Type valueType = value.GetType();
        if (targetType.IsAssignableFrom(valueType))
            return value;

        Type? nullable = Nullable.GetUnderlyingType(targetType);
        if (nullable is not null)
            return ConvertValue(value, nullable);

        if (targetType.IsEnum)
            return Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));

        if (targetType == typeof(Guid) && value is byte[] bytes && bytes.Length == 16)
            return new Guid(bytes);

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }
}
