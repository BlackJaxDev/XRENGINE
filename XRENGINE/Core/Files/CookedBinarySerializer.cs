using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Text;
using MemoryPack;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Components;
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

    // Dictionary mapping known types to their serialization handlers for O(1) dispatch
    private static readonly Dictionary<Type, Action<CookedBinaryWriter, object>> PrimitiveWriters = new()
    {
        [typeof(bool)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Boolean); w.Write((bool)v); },
        [typeof(byte)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Byte); w.Write((byte)v); },
        [typeof(sbyte)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.SByte); w.Write((sbyte)v); },
        [typeof(short)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Int16); w.Write((short)v); },
        [typeof(ushort)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.UInt16); w.Write((ushort)v); },
        [typeof(int)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Int32); w.Write((int)v); },
        [typeof(uint)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.UInt32); w.Write((uint)v); },
        [typeof(long)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Int64); w.Write((long)v); },
        [typeof(ulong)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.UInt64); w.Write((ulong)v); },
        [typeof(float)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Single); w.Write((float)v); },
        [typeof(double)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Double); w.Write((double)v); },
        [typeof(decimal)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Decimal); w.Write((decimal)v); },
        [typeof(char)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Char); w.Write((char)v); },
        [typeof(string)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.String); w.Write((string)v); },
        [typeof(Guid)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Guid); w.Write(((Guid)v).ToByteArray()); },
        [typeof(DateTime)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.DateTime); w.Write(((DateTime)v).ToBinary()); },
        [typeof(TimeSpan)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.TimeSpan); w.Write(((TimeSpan)v).Ticks); },
        [typeof(Vector2)] = (w, v) => { var x = (Vector2)v; w.Write((byte)CookedBinaryTypeMarker.Vector2); w.Write(x.X); w.Write(x.Y); },
        [typeof(Vector3)] = (w, v) => { var x = (Vector3)v; w.Write((byte)CookedBinaryTypeMarker.Vector3); w.Write(x.X); w.Write(x.Y); w.Write(x.Z); },
        [typeof(Vector4)] = (w, v) => { var x = (Vector4)v; w.Write((byte)CookedBinaryTypeMarker.Vector4); w.Write(x.X); w.Write(x.Y); w.Write(x.Z); w.Write(x.W); },
        [typeof(Quaternion)] = (w, v) => { var x = (Quaternion)v; w.Write((byte)CookedBinaryTypeMarker.Quaternion); w.Write(x.X); w.Write(x.Y); w.Write(x.Z); w.Write(x.W); },
        [typeof(Matrix4x4)] = (w, v) =>
        {
            var m = (Matrix4x4)v;
            w.Write((byte)CookedBinaryTypeMarker.Matrix4x4);
            w.Write(m.M11); w.Write(m.M12); w.Write(m.M13); w.Write(m.M14);
            w.Write(m.M21); w.Write(m.M22); w.Write(m.M23); w.Write(m.M24);
            w.Write(m.M31); w.Write(m.M32); w.Write(m.M33); w.Write(m.M34);
            w.Write(m.M41); w.Write(m.M42); w.Write(m.M43); w.Write(m.M44);
        },
        [typeof(ColorF3)] = (w, v) => { var c = (ColorF3)v; w.Write((byte)CookedBinaryTypeMarker.ColorF3); w.Write(c.R); w.Write(c.G); w.Write(c.B); },
        [typeof(ColorF4)] = (w, v) => { var c = (ColorF4)v; w.Write((byte)CookedBinaryTypeMarker.ColorF4); w.Write(c.R); w.Write(c.G); w.Write(c.B); w.Write(c.A); },
        
        // High-priority additional types
        [typeof(Half)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Half); w.Write(BitConverter.HalfToUInt16Bits((Half)v)); },
        [typeof(DateTimeOffset)] = (w, v) => { var d = (DateTimeOffset)v; w.Write((byte)CookedBinaryTypeMarker.DateTimeOffset); w.Write(d.Ticks); w.Write((short)d.Offset.TotalMinutes); },
        [typeof(DateOnly)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.DateOnly); w.Write(((DateOnly)v).DayNumber); },
        [typeof(TimeOnly)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.TimeOnly); w.Write(((TimeOnly)v).Ticks); },
        [typeof(System.Numerics.Plane)] = (w, v) => { var p = (System.Numerics.Plane)v; w.Write((byte)CookedBinaryTypeMarker.Plane); w.Write(p.Normal.X); w.Write(p.Normal.Y); w.Write(p.Normal.Z); w.Write(p.D); },
        [typeof(Uri)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Uri); w.Write(((Uri)v).OriginalString); },
        
        // Geometry types
        [typeof(Segment)] = (w, v) => { var s = (Segment)v; w.Write((byte)CookedBinaryTypeMarker.Segment); w.Write(s.Start.X); w.Write(s.Start.Y); w.Write(s.Start.Z); w.Write(s.End.X); w.Write(s.End.Y); w.Write(s.End.Z); },
        [typeof(Ray)] = (w, v) => { var r = (Ray)v; w.Write((byte)CookedBinaryTypeMarker.Ray); w.Write(r.StartPoint.X); w.Write(r.StartPoint.Y); w.Write(r.StartPoint.Z); w.Write(r.Direction.X); w.Write(r.Direction.Y); w.Write(r.Direction.Z); },
        [typeof(AABB)] = (w, v) => { var a = (AABB)v; w.Write((byte)CookedBinaryTypeMarker.AABB); w.Write(a.Min.X); w.Write(a.Min.Y); w.Write(a.Min.Z); w.Write(a.Max.X); w.Write(a.Max.Y); w.Write(a.Max.Z); },
        [typeof(Sphere)] = (w, v) => { var s = (Sphere)v; w.Write((byte)CookedBinaryTypeMarker.Sphere); w.Write(s.Center.X); w.Write(s.Center.Y); w.Write(s.Center.Z); w.Write(s.Radius); },
        [typeof(Triangle)] = (w, v) =>
        {
            var t = (Triangle)v;
            w.Write((byte)CookedBinaryTypeMarker.Triangle);
            w.Write(t.A.X); w.Write(t.A.Y); w.Write(t.A.Z);
            w.Write(t.B.X); w.Write(t.B.Y); w.Write(t.B.Z);
            w.Write(t.C.X); w.Write(t.C.Y); w.Write(t.C.Z);
        },
        
        // Medium-priority types
        [typeof(Version)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.Version); w.Write(((Version)v).ToString()); },
        [typeof(Complex)] = (w, v) => { var c = (Complex)v; w.Write((byte)CookedBinaryTypeMarker.Complex); w.Write(c.Real); w.Write(c.Imaginary); },
        [typeof(IPAddress)] = (w, v) => { var ip = (IPAddress)v; var bytes = ip.GetAddressBytes(); w.Write((byte)CookedBinaryTypeMarker.IPAddress); w.Write((byte)bytes.Length); w.Write(bytes); },
        [typeof(Range)] = (w, v) => { var r = (Range)v; w.Write((byte)CookedBinaryTypeMarker.Range); w.Write(r.Start.Value); w.Write(r.Start.IsFromEnd); w.Write(r.End.Value); w.Write(r.End.IsFromEnd); },
        [typeof(Index)] = (w, v) => { var i = (Index)v; w.Write((byte)CookedBinaryTypeMarker.Index); w.Write(i.Value); w.Write(i.IsFromEnd); },
        
        // Low-priority types
        [typeof(CultureInfo)] = (w, v) => { w.Write((byte)CookedBinaryTypeMarker.CultureInfo); w.Write(((CultureInfo)v).Name); },
        [typeof(Regex)] = (w, v) => { var r = (Regex)v; w.Write((byte)CookedBinaryTypeMarker.Regex); w.Write(r.ToString()); w.Write((int)r.Options); },
        [typeof(BitArray)] = (w, v) => { var b = (BitArray)v; w.Write((byte)CookedBinaryTypeMarker.BitArray); w.Write(b.Length); int byteCount = (b.Length + 7) / 8; byte[] bytes = new byte[byteCount]; b.CopyTo(bytes, 0); w.Write(bytes); },
        [typeof(BigInteger)] = (w, v) => { var bi = (BigInteger)v; byte[] bytes = bi.ToByteArray(); w.Write((byte)CookedBinaryTypeMarker.BigInteger); w.Write(bytes.Length); w.Write(bytes); },
        [typeof(IPEndPoint)] = (w, v) => { var ep = (IPEndPoint)v; byte[] addr = ep.Address.GetAddressBytes(); w.Write((byte)CookedBinaryTypeMarker.IPEndPoint); w.Write((byte)addr.Length); w.Write(addr); w.Write(ep.Port); },
        [typeof(Frustum)] = (w, v) => { var f = (Frustum)v; w.Write((byte)CookedBinaryTypeMarker.Frustum); foreach (var c in f.Corners) { w.Write(c.X); w.Write(c.Y); w.Write(c.Z); } },
    };

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

        if (IsRuntimeOnlyType(runtimeType))
        {
            writer.Write((byte)CookedBinaryTypeMarker.Null);
            return;
        }

        // Fast path: O(1) lookup for known primitive/value types
        if (PrimitiveWriters.TryGetValue(runtimeType, out var primitiveWriter))
        {
            primitiveWriter(writer, value);
            return;
        }

        // Handle byte[] separately (can't use typeof(byte[]) reliably for array subtype matching)
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

        // XREvent and XREvent<T> - serialize only persistent calls (runtime delegates are not serializable)
        if (value is XREvent xrEvent)
        {
            writer.Write((byte)CookedBinaryTypeMarker.XREvent);
            WriteXREventPersistentCalls(writer, xrEvent.PersistentCalls, allowCustom, callbacks);
            return;
        }

        if (TryWriteGenericXREvent(writer, value, runtimeType, allowCustom, callbacks))
            return;

        // Nullable<T> - write the underlying value if present
        if (TryWriteNullable(writer, value, runtimeType, allowCustom, callbacks))
            return;

        // ValueTuple - serialize tuple elements
        if (TryWriteValueTuple(writer, value, runtimeType, allowCustom, callbacks))
            return;

        // Type reference
        if (value is Type typeRef)
        {
            writer.Write((byte)CookedBinaryTypeMarker.TypeRef);
            WriteTypeName(writer, typeRef);
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

        // HashSet<T> - must be before IList check since HashSet doesn't implement IList
        if (TryWriteHashSet(writer, value, runtimeType, allowCustom, callbacks))
            return;

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

        // Blittable struct fallback: if the type is an unmanaged struct with sequential/explicit layout,
        // serialize it directly via raw memory copy.
        if (runtimeType.IsValueType && !runtimeType.IsPrimitive && !runtimeType.IsEnum && IsBlittableStruct(runtimeType))
        {
            writer.Write((byte)CookedBinaryTypeMarker.BlittableStruct);
            WriteTypeName(writer, runtimeType);
            int size = Marshal.SizeOf(runtimeType);
            writer.Write(size);
            WriteBlittableValue(writer, value, size);
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
            CookedBinaryTypeMarker.Vector2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.Vector3 => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.Vector4 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.Quaternion => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.Matrix4x4 => ReadMatrix4x4(reader),
            CookedBinaryTypeMarker.ColorF3 => new ColorF3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.ColorF4 => new ColorF4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            
            // High-priority additional types
            CookedBinaryTypeMarker.Half => BitConverter.UInt16BitsToHalf(reader.ReadUInt16()),
            CookedBinaryTypeMarker.DateTimeOffset => new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16())),
            CookedBinaryTypeMarker.DateOnly => DateOnly.FromDayNumber(reader.ReadInt32()),
            CookedBinaryTypeMarker.TimeOnly => new TimeOnly(reader.ReadInt64()),
            CookedBinaryTypeMarker.Plane => new System.Numerics.Plane(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            CookedBinaryTypeMarker.Uri => new Uri(reader.ReadString()),
            CookedBinaryTypeMarker.TypeRef => ReadTypeRef(reader),
            CookedBinaryTypeMarker.HashSet => ReadHashSet(reader, callbacks),
            
            // Geometry types
            CookedBinaryTypeMarker.Segment => new Segment(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
            CookedBinaryTypeMarker.Ray => new Ray(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
            CookedBinaryTypeMarker.AABB => new AABB(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
            CookedBinaryTypeMarker.Sphere => new Sphere(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle()),
            CookedBinaryTypeMarker.Triangle => new Triangle(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())),
            CookedBinaryTypeMarker.Frustum => ReadFrustum(reader),
            
            // Medium-priority types
            CookedBinaryTypeMarker.Version => Version.Parse(reader.ReadString()),
            CookedBinaryTypeMarker.BigInteger => ReadBigInteger(reader),
            CookedBinaryTypeMarker.Complex => new Complex(reader.ReadDouble(), reader.ReadDouble()),
            CookedBinaryTypeMarker.IPAddress => ReadIPAddress(reader),
            CookedBinaryTypeMarker.IPEndPoint => ReadIPEndPoint(reader),
            CookedBinaryTypeMarker.Range => new Range(new Index(reader.ReadInt32(), reader.ReadBoolean()), new Index(reader.ReadInt32(), reader.ReadBoolean())),
            CookedBinaryTypeMarker.Index => new Index(reader.ReadInt32(), reader.ReadBoolean()),
            
            // Low-priority types
            CookedBinaryTypeMarker.BitArray => ReadBitArray(reader),
            CookedBinaryTypeMarker.CultureInfo => CultureInfo.GetCultureInfo(reader.ReadString()),
            CookedBinaryTypeMarker.Regex => new Regex(reader.ReadString(), (RegexOptions)reader.ReadInt32()),
            
            CookedBinaryTypeMarker.BlittableStruct => ReadBlittableStruct(reader),
            CookedBinaryTypeMarker.XREvent => ReadXREvent(reader),
            CookedBinaryTypeMarker.XREventGeneric => ReadXREventGeneric(reader),
            CookedBinaryTypeMarker.Nullable => ReadNullable(reader, callbacks),
            CookedBinaryTypeMarker.ValueTuple => ReadValueTuple(reader, callbacks),
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
            object? value = null;
            try
            {
                value = member.GetValue(instance);
            }
            catch (Exception ex)
            {
                LogMemberSerializationFailure(metadataType, member.Name, ex);
            }
            WriteValue(writer, value, allowCustom, callbacks);
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void ReadObjectContent(CookedBinaryReader reader, object instance, Type metadataType, CookedBinarySerializationCallbacks? callbacks)
    {
        int count = reader.ReadInt32();
        var metadata = TypeMetadataCache.Get(metadataType);

        using (XRBase.SuppressPropertyNotifications())
        {
            for (int i = 0; i < count; i++)
            {
                string propertyName = reader.ReadString();
                if (metadata.TryGetMember(propertyName, out var member))
                {
                    object? value;
                    if (instance is SceneNode sceneNode && propertyName == nameof(SceneNode.ComponentsSerialized))
                    {
                        using var _ = new OwningSceneNodeScope(sceneNode);
                        value = ReadValue(reader, null, callbacks);
                    }
                    else
                    {
                        value = ReadValue(reader, null, callbacks);
                    }

                    object? converted;
                    try
                    {
                        converted = ConvertValue(value, member.MemberType);
                    }
                    catch
                    {
                        converted = null;
                    }

                    try
                    {
                        member.SetValue(instance, converted);
                    }
                    catch
                    {
                        // Ignore bad setters during snapshot restore.
                    }
                }
                else
                {
                    SkipValue(reader);
                }
            }
        }

        InvokePostDeserializeHook(instance);
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
            case CookedBinaryTypeMarker.Vector2:
                reader.ReadSingle();
                reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.Vector3:
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.Vector4:
            case CookedBinaryTypeMarker.Quaternion:
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.Matrix4x4:
                for (int i = 0; i < 16; i++)
                    reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.ColorF3:
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                return;
            case CookedBinaryTypeMarker.ColorF4:
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                reader.ReadSingle();
                return;
            
            // High-priority additional types
            case CookedBinaryTypeMarker.Half:
                reader.ReadUInt16();
                return;
            case CookedBinaryTypeMarker.DateTimeOffset:
                reader.ReadInt64(); // ticks
                reader.ReadInt16(); // offset minutes
                return;
            case CookedBinaryTypeMarker.DateOnly:
                reader.ReadInt32();
                return;
            case CookedBinaryTypeMarker.TimeOnly:
                reader.ReadInt64();
                return;
            case CookedBinaryTypeMarker.Plane:
                reader.SkipBytes(sizeof(float) * 4);
                return;
            case CookedBinaryTypeMarker.Uri:
                reader.ReadString();
                return;
            case CookedBinaryTypeMarker.TypeRef:
                reader.ReadString();
                return;
            case CookedBinaryTypeMarker.HashSet:
            {
                reader.ReadString(); // element type name
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                    SkipValue(reader);
                return;
            }
            
            // Geometry types
            case CookedBinaryTypeMarker.Segment:
                reader.SkipBytes(sizeof(float) * 6);
                return;
            case CookedBinaryTypeMarker.Ray:
                reader.SkipBytes(sizeof(float) * 6);
                return;
            case CookedBinaryTypeMarker.AABB:
                reader.SkipBytes(sizeof(float) * 6);
                return;
            case CookedBinaryTypeMarker.Sphere:
                reader.SkipBytes(sizeof(float) * 4);
                return;
            case CookedBinaryTypeMarker.Triangle:
                reader.SkipBytes(sizeof(float) * 9);
                return;
            case CookedBinaryTypeMarker.Frustum:
            {
                // 8 corners (3 floats each) - planes reconstructed on deserialize
                reader.SkipBytes(sizeof(float) * 8 * 3);
                return;
            }
            
            // Medium-priority types
            case CookedBinaryTypeMarker.Version:
                reader.ReadString();
                return;
            case CookedBinaryTypeMarker.BigInteger:
            {
                int length = reader.ReadInt32();
                reader.SkipBytes(length);
                return;
            }
            case CookedBinaryTypeMarker.Complex:
                reader.SkipBytes(sizeof(double) * 2);
                return;
            case CookedBinaryTypeMarker.IPAddress:
            {
                int length = reader.ReadByte();
                reader.SkipBytes(length);
                return;
            }
            case CookedBinaryTypeMarker.IPEndPoint:
            {
                int length = reader.ReadByte();
                reader.SkipBytes(length + sizeof(int)); // address + port
                return;
            }
            case CookedBinaryTypeMarker.Range:
                reader.SkipBytes(sizeof(int) + sizeof(bool) + sizeof(int) + sizeof(bool));
                return;
            case CookedBinaryTypeMarker.Index:
                reader.SkipBytes(sizeof(int) + sizeof(bool));
                return;
            
            // Low-priority types
            case CookedBinaryTypeMarker.BitArray:
            {
                int bitLength = reader.ReadInt32();
                int byteCount = (bitLength + 7) / 8;
                reader.SkipBytes(byteCount);
                return;
            }
            case CookedBinaryTypeMarker.CultureInfo:
                reader.ReadString();
                return;
            case CookedBinaryTypeMarker.Regex:
                reader.ReadString();
                reader.ReadInt32();
                return;
            
            case CookedBinaryTypeMarker.BlittableStruct:
            {
                reader.ReadString(); // type name
                int size = reader.ReadInt32();
                reader.SkipBytes(size);
                return;
            }
            case CookedBinaryTypeMarker.XREvent:
            {
                SkipXRPersistentCallList(reader);
                return;
            }
            case CookedBinaryTypeMarker.XREventGeneric:
            {
                reader.ReadString(); // type argument
                SkipXRPersistentCallList(reader);
                return;
            }
            case CookedBinaryTypeMarker.Nullable:
            {
                reader.ReadString(); // underlying type name
                bool hasValue = reader.ReadBoolean();
                if (hasValue)
                    SkipValue(reader);
                return;
            }
            case CookedBinaryTypeMarker.ValueTuple:
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadString(); // element type name
                    SkipValue(reader);
                }
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
    private static Array ReadArray(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
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
    private static IList ReadList(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
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
    private static IDictionary ReadDictionary(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
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
    private static ICookedBinarySerializable? ReadCustomObject(CookedBinaryReader reader, Type? expectedType, CookedBinarySerializationCallbacks? callbacks)
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
        int count = reader.ReadInt32();
        var metadata = TypeMetadataCache.Get(targetType);

        object? instance = CreateInstance(targetType);
        if (instance is null)
        {
            // If the type can't be instantiated (no public parameterless ctor), we can still
            // safely skip its serialized content because reflection encoding is self-describing.
            for (int i = 0; i < count; i++)
            {
                reader.ReadString();
                SkipValue(reader);
            }

            LogTypeDeserializationFailure(targetType, new InvalidOperationException($"Unable to create instance of '{targetType}'."));
            return null;
        }

        // Ensure components have their owning SceneNode before other member setters run.
        TryAttachComponentToOwningSceneNode(instance);

        using (XRBase.SuppressPropertyNotifications())
        {
            for (int i = 0; i < count; i++)
            {
                string propertyName = reader.ReadString();
                if (metadata.TryGetMember(propertyName, out var member))
                {
                    object? value;
                    if (instance is SceneNode sceneNode && propertyName == nameof(SceneNode.ComponentsSerialized))
                    {
                        using var _ = new OwningSceneNodeScope(sceneNode);
                        value = ReadValue(reader, null, callbacks);
                    }
                    else
                    {
                        value = ReadValue(reader, null, callbacks);
                    }
                    object? converted;
                    try
                    {
                        converted = ConvertValue(value, member.MemberType);
                    }
                    catch
                    {
                        converted = null;
                    }

                    try
                    {
                        member.SetValue(instance, converted);
                    }
                    catch
                    {
                        // Ignore bad setters during snapshot restore.
                    }
                }
                else
                {
                    SkipValue(reader);
                }
            }
        }

        InvokePostDeserializeHook(instance);
        return instance;
    }

    private static readonly ConcurrentDictionary<Type, byte> LoggedTypeDeserializationFailures = new();
    private static readonly ConcurrentDictionary<Type, byte> LoggedMemoryPackSerializationFailures = new();

    private static void LogTypeDeserializationFailure(Type type, Exception ex)
    {
        if (!LoggedTypeDeserializationFailures.TryAdd(type, 0))
            return;

        Debug.LogWarning($"Cooked binary deserialization skipped type '{type}': {ex.Message}");
    }

    private static void LogMemoryPackSerializationFailure(Type type, Exception ex)
    {
        if (!LoggedMemoryPackSerializationFailures.TryAdd(type, 0))
            return;

        Debug.LogWarning($"[MEMORYPACK SERIALIZE FAIL] Type '{type.FullName}' is not supported by MemoryPack. " +
            $"Consider adding explicit support in CookedBinarySerializer. Exception: {ex.GetType().Name}: {ex.Message}");
    }

    private static void LogMemoryPackDeserializationFailure(Type type, Exception ex)
    {
        if (!LoggedTypeDeserializationFailures.TryAdd(type, 0))
            return;

        Debug.LogWarning($"[MEMORYPACK DESERIALIZE FAIL] Type '{type.FullName}' failed to deserialize via MemoryPack. " +
            $"This may cause data loss! Consider adding explicit support in CookedBinarySerializer. Exception: {ex.GetType().Name}: {ex.Message}");
    }

    private static void LogMemberSerializationFailure(Type ownerType, string memberName, Exception ex)
        => Debug.LogWarning($"Cooked binary serialization skipped member '{ownerType}.{memberName}': {ex.Message}");

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadMemoryPackObject(CookedBinaryReader reader, Type targetType)
    {
        int length = reader.ReadInt32();
        byte[] payload = reader.ReadBytes(length);
        try
        {
            object? instance;
            using (XRBase.SuppressPropertyNotifications())
            {
                instance = typeof(XRAsset).IsAssignableFrom(targetType)
                    ? (XRAsset?)(XRAssetMemoryPackAdapter.Deserialize(payload, targetType) ?? throw new InvalidOperationException($"MemoryPack deserialization returned null for asset type '{targetType}'."))
                    : (object?)(MemoryPackSerializer.Deserialize(targetType, payload) ?? throw new InvalidOperationException($"MemoryPack deserialization returned null for type '{targetType}'."));
            }

            InvokePostDeserializeHook(instance);
            return instance;
        }
        catch (Exception ex)
        {
            // Safe to skip because the payload is length-prefixed and already consumed.
            LogMemoryPackDeserializationFailure(targetType, ex);
            return null;
        }
    }

    private static readonly ConcurrentDictionary<Type, byte> LoggedPostDeserializeFailures = new();

    private static void InvokePostDeserializeHook(object? instance)
    {
        if (instance is not IPostCookedBinaryDeserialize hook)
            return;

        try
        {
            hook.OnPostCookedBinaryDeserialize();
        }
        catch (Exception ex)
        {
            var type = instance.GetType();
            if (LoggedPostDeserializeFailures.TryAdd(type, 0))
                Debug.LogWarning($"Cooked post-deserialize hook failed for '{type}': {ex.Message}");
        }
    }

    private static DataSource ReadDataSource(CookedBinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] data = reader.ReadBytes(length);
        return new DataSource(data);
    }

    private static Matrix4x4 ReadMatrix4x4(CookedBinaryReader reader)
    {
        return new Matrix4x4(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static readonly ConcurrentDictionary<Type, bool> BlittableStructCache = new();

    private static bool IsBlittableStruct(Type type)
    {
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
            return false;

        return BlittableStructCache.GetOrAdd(type, static t =>
        {
            // Check for StructLayout with Sequential or Explicit layout
            var layoutAttr = t.GetCustomAttribute<StructLayoutAttribute>();
            if (layoutAttr is null)
                return false;

            if (layoutAttr.Value != LayoutKind.Sequential && layoutAttr.Value != LayoutKind.Explicit)
                return false;

            // Verify all fields are blittable
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type fieldType = field.FieldType;
                if (!IsBlittableType(fieldType))
                    return false;
            }

            return true;
        });
    }

    private static bool IsBlittableType(Type type)
    {
        if (type.IsPrimitive)
            return true;
        if (type.IsEnum)
            return true;
        if (type == typeof(decimal))
            return true;
        if (type.IsPointer)
            return true;
        if (type.IsValueType && !type.IsGenericType)
            return IsBlittableStruct(type);
        return false;
    }

    private static unsafe void WriteBlittableValue(CookedBinaryWriter writer, object value, int size)
    {
        byte* buffer = stackalloc byte[size];
        Marshal.StructureToPtr(value, (IntPtr)buffer, fDeleteOld: false);
        writer.WriteBytes(new ReadOnlySpan<byte>(buffer, size));
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static unsafe object? ReadBlittableStruct(CookedBinaryReader reader)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName);
        int size = reader.ReadInt32();
        byte[] data = reader.ReadBytes(size);

        int expectedSize = Marshal.SizeOf(targetType);
        if (size != expectedSize)
            throw new InvalidOperationException($"Blittable struct size mismatch: expected {expectedSize}, got {size} for type '{targetType}'.");

        fixed (byte* ptr = data)
        {
            return Marshal.PtrToStructure((IntPtr)ptr, targetType);
        }
    }

    // XREvent serialization helpers
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static void WriteXREventPersistentCalls(CookedBinaryWriter writer, List<XRPersistentCall>? calls, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        if (calls is null || calls.Count == 0)
        {
            writer.Write(0);
            return;
        }

        writer.Write(calls.Count);
        foreach (var call in calls)
            WriteXRPersistentCall(writer, call);
    }

    private static void WriteXRPersistentCall(CookedBinaryWriter writer, XRPersistentCall call)
    {
        writer.Write(call.NodeId.ToByteArray());
        writer.Write(call.TargetObjectId.ToByteArray());
        writer.Write(call.MethodName ?? string.Empty);
        writer.Write(call.UseTupleExpansion);
        
        var paramTypes = call.ParameterTypeNames;
        if (paramTypes is null || paramTypes.Length == 0)
        {
            writer.Write(0);
        }
        else
        {
            writer.Write(paramTypes.Length);
            foreach (var typeName in paramTypes)
                writer.Write(typeName ?? string.Empty);
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static bool TryWriteGenericXREvent(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(XREvent<>))
            return false;

        writer.Write((byte)CookedBinaryTypeMarker.XREventGeneric);
        WriteTypeName(writer, runtimeType.GetGenericArguments()[0]); // Write the T type
        
        // Get PersistentCalls property via reflection
        var prop = runtimeType.GetProperty(nameof(XREvent.PersistentCalls));
        var calls = prop?.GetValue(value) as List<XRPersistentCall>;
        WriteXREventPersistentCalls(writer, calls, allowCustom, callbacks);
        return true;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static XREvent ReadXREvent(CookedBinaryReader reader)
        => new XREvent { PersistentCalls = ReadXRPersistentCallList(reader) };

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadXREventGeneric(CookedBinaryReader reader)
    {
        string typeArg = reader.ReadString();
        Type elementType = ResolveType(typeArg);
        Type eventType = typeof(XREvent<>).MakeGenericType(elementType);
        
        var evt = Activator.CreateInstance(eventType)!;
        var calls = ReadXRPersistentCallList(reader);
        
        var prop = eventType.GetProperty(nameof(XREvent.PersistentCalls));
        prop?.SetValue(evt, calls);
        
        return evt;
    }

    private static List<XRPersistentCall>? ReadXRPersistentCallList(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return null;

        var calls = new List<XRPersistentCall>(count);
        for (int i = 0; i < count; i++)
            calls.Add(ReadXRPersistentCall(reader));
        return calls;
    }

    private static XRPersistentCall ReadXRPersistentCall(CookedBinaryReader reader)
    {
        var call = new XRPersistentCall
        {
            NodeId = new Guid(reader.ReadBytes(16)),
            TargetObjectId = new Guid(reader.ReadBytes(16)),
            MethodName = reader.ReadString(),
            UseTupleExpansion = reader.ReadBoolean()
        };

        int paramCount = reader.ReadInt32();
        if (paramCount > 0)
        {
            call.ParameterTypeNames = new string[paramCount];
            for (int i = 0; i < paramCount; i++)
                call.ParameterTypeNames[i] = reader.ReadString();
        }

        return call;
    }

    private static void SkipXRPersistentCallList(CookedBinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            SkipXRPersistentCall(reader);
    }

    private static void SkipXRPersistentCall(CookedBinaryReader reader)
    {
        reader.SkipBytes(16); // NodeId
        reader.SkipBytes(16); // TargetObjectId
        reader.ReadString();  // MethodName
        reader.ReadBoolean(); // UseTupleExpansion
        
        int paramCount = reader.ReadInt32();
        for (int i = 0; i < paramCount; i++)
            reader.ReadString();
    }

    // Nullable<T> serialization helpers
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static bool TryWriteNullable(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        Type? underlyingType = Nullable.GetUnderlyingType(runtimeType);
        if (underlyingType is null)
            return false;

        writer.Write((byte)CookedBinaryTypeMarker.Nullable);
        WriteTypeName(writer, underlyingType);
        
        // For boxed nullable, if we got here the value is not null (null would have been handled earlier)
        // So hasValue is always true for non-null boxed nullable
        writer.Write(true); // hasValue
        WriteValue(writer, value, allowCustom, callbacks);
        return true;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadNullable(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string underlyingTypeName = reader.ReadString();
        Type underlyingType = ResolveType(underlyingTypeName);
        bool hasValue = reader.ReadBoolean();
        
        if (!hasValue)
            return null;
        
        object? value = ReadValue(reader, underlyingType, callbacks);
        return value;
    }

    // ValueTuple serialization helpers
    private static readonly HashSet<Type> ValueTupleTypes = new()
    {
        typeof(ValueTuple<>),
        typeof(ValueTuple<,>),
        typeof(ValueTuple<,,>),
        typeof(ValueTuple<,,,>),
        typeof(ValueTuple<,,,,>),
        typeof(ValueTuple<,,,,,>),
        typeof(ValueTuple<,,,,,,>),
        typeof(ValueTuple<,,,,,,,>)
    };

    private static bool IsValueTupleType(Type type)
        => type.IsValueType && type.IsGenericType && ValueTupleTypes.Contains(type.GetGenericTypeDefinition());

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static bool TryWriteValueTuple(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        if (!IsValueTupleType(runtimeType))
            return false;

        var tuple = (ITuple)value;
        int length = tuple.Length;
        Type[] typeArgs = runtimeType.GetGenericArguments();

        writer.Write((byte)CookedBinaryTypeMarker.ValueTuple);
        writer.Write(length);

        for (int i = 0; i < length; i++)
        {
            WriteTypeName(writer, typeArgs[i]);
            WriteValue(writer, tuple[i], allowCustom, callbacks);
        }

        return true;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadValueTuple(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        int count = reader.ReadInt32();
        if (count == 0)
            return default(ValueTuple);

        Type[] typeArgs = new Type[count];
        object?[] values = new object?[count];

        for (int i = 0; i < count; i++)
        {
            string typeName = reader.ReadString();
            typeArgs[i] = ResolveType(typeName);
            values[i] = ReadValue(reader, typeArgs[i], callbacks);
        }

        // Create the appropriate ValueTuple type and instantiate it
        Type tupleType = count switch
        {
            1 => typeof(ValueTuple<>).MakeGenericType(typeArgs),
            2 => typeof(ValueTuple<,>).MakeGenericType(typeArgs),
            3 => typeof(ValueTuple<,,>).MakeGenericType(typeArgs),
            4 => typeof(ValueTuple<,,,>).MakeGenericType(typeArgs),
            5 => typeof(ValueTuple<,,,,>).MakeGenericType(typeArgs),
            6 => typeof(ValueTuple<,,,,,>).MakeGenericType(typeArgs),
            7 => typeof(ValueTuple<,,,,,,>).MakeGenericType(typeArgs),
            8 => typeof(ValueTuple<,,,,,,,>).MakeGenericType(typeArgs),
            _ => throw new NotSupportedException($"ValueTuple with {count} elements is not supported.")
        };

        return Activator.CreateInstance(tupleType, values)!;
    }

    // Type reference helpers
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static Type ReadTypeRef(CookedBinaryReader reader)
    {
        string typeName = reader.ReadString();
        return ResolveType(typeName);
    }

    // HashSet helpers
    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static bool TryWriteHashSet(CookedBinaryWriter writer, object value, Type runtimeType, bool allowCustom, CookedBinarySerializationCallbacks? callbacks)
    {
        if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(HashSet<>))
            return false;

        Type elementType = runtimeType.GetGenericArguments()[0];
        writer.Write((byte)CookedBinaryTypeMarker.HashSet);
        WriteTypeName(writer, elementType);

        // HashSet implements IEnumerable, use reflection to get count and enumerate
        var countProp = runtimeType.GetProperty("Count");
        int count = (int)(countProp?.GetValue(value) ?? 0);
        writer.Write(count);

        foreach (object? item in (IEnumerable)value)
            WriteValue(writer, item, allowCustom, callbacks);

        return true;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadHashSet(CookedBinaryReader reader, CookedBinarySerializationCallbacks? callbacks)
    {
        string elementTypeName = reader.ReadString();
        Type elementType = ResolveType(elementTypeName);
        Type hashSetType = typeof(HashSet<>).MakeGenericType(elementType);

        int count = reader.ReadInt32();
        object hashSet = Activator.CreateInstance(hashSetType)!;
        var addMethod = hashSetType.GetMethod("Add")!;

        for (int i = 0; i < count; i++)
        {
            object? item = ReadValue(reader, elementType, callbacks);
            addMethod.Invoke(hashSet, [item]);
        }

        return hashSet;
    }

    // Frustum read helper
    private static Frustum ReadFrustum(CookedBinaryReader reader)
    {
        // Read 8 corners: NBL, NBR, NTL, NTR, FBL, FBR, FTL, FTR (planes reconstructed automatically)
        Vector3 ReadVector3() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        
        Vector3 nearBottomLeft = ReadVector3();
        Vector3 nearBottomRight = ReadVector3();
        Vector3 nearTopLeft = ReadVector3();
        Vector3 nearTopRight = ReadVector3();
        Vector3 farBottomLeft = ReadVector3();
        Vector3 farBottomRight = ReadVector3();
        Vector3 farTopLeft = ReadVector3();
        Vector3 farTopRight = ReadVector3();
        
        return new Frustum(
            nearBottomLeft, nearBottomRight, nearTopLeft, nearTopRight,
            farBottomLeft, farBottomRight, farTopLeft, farTopRight);
    }

    // Medium/low priority type read helpers
    private static BigInteger ReadBigInteger(CookedBinaryReader reader)
    {
        int length = reader.ReadInt32();
        byte[] bytes = reader.ReadBytes(length);
        return new BigInteger(bytes);
    }

    private static IPAddress ReadIPAddress(CookedBinaryReader reader)
    {
        int length = reader.ReadByte();
        byte[] bytes = reader.ReadBytes(length);
        return new IPAddress(bytes);
    }

    private static IPEndPoint ReadIPEndPoint(CookedBinaryReader reader)
    {
        int length = reader.ReadByte();
        byte[] addressBytes = reader.ReadBytes(length);
        int port = reader.ReadInt32();
        return new IPEndPoint(new IPAddress(addressBytes), port);
    }

    private static BitArray ReadBitArray(CookedBinaryReader reader)
    {
        int bitLength = reader.ReadInt32();
        int byteCount = (bitLength + 7) / 8;
        byte[] bytes = reader.ReadBytes(byteCount);
        return new BitArray(bytes) { Length = bitLength };
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

            // Prefer public parameterless ctor.
            ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor is not null)
                return ctor.Invoke(null);

            // Many engine/runtime types (notably XRComponent-derived) intentionally hide their
            // parameterless ctor (internal/protected) and are normally created via factory APIs.
            // During snapshot restore we still need to instantiate them before we can wire up
            // owning SceneNode and replay serialized members.
            ctor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
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

    // While deserializing SceneNode.ComponentsSerialized, we want each XRComponent to have its
    // owning SceneNode assigned *before* other member setters run.
    private static readonly AsyncLocal<SceneNode?> CurrentOwningSceneNode = new();

    private readonly struct OwningSceneNodeScope : IDisposable
    {
        private readonly SceneNode? _previous;

        public OwningSceneNodeScope(SceneNode? node)
        {
            _previous = CurrentOwningSceneNode.Value;
            CurrentOwningSceneNode.Value = node;
        }

        public void Dispose()
            => CurrentOwningSceneNode.Value = _previous;
    }

    private static void TryAttachComponentToOwningSceneNode(object instance)
    {
        if (instance is not XRComponent component)
            return;

        SceneNode? owner = CurrentOwningSceneNode.Value;
        if (owner is null)
            return;

        try
        {
            // Prefer a dedicated construction hook if present.
            // NOTE: XRComponent declares this as a *private* method, so looking it up on the
            // derived runtime type will not find it. We must query the declaring base type.
            MethodInfo? method = typeof(XRComponent).GetMethod(
                "ConstructionSetSceneNode",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(SceneNode) },
                modifiers: null);

            if (method is not null)
            {
                method.Invoke(component, new object?[] { owner });

                // SceneNode attachment uses a private construction hook that bypasses the
                // SceneNode property setter, so we also need to propagate World explicitly.
                // This will trigger XRComponent's World-change handling to rebind render infos.
                if (owner.World is not null)
                    component.World = owner.World;
                return;
            }

            // Fall back to setting the private SceneNode property setter.
            PropertyInfo? prop = component.GetType().GetProperty(
                nameof(XRComponent.SceneNode),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? setter = prop?.GetSetMethod(nonPublic: true);
            setter?.Invoke(component, new object?[] { owner });

            if (owner.World is not null)
                component.World = owner.World;
        }
        catch
        {
            // Best-effort only; snapshot restore should not fail because a component couldn't attach.
        }
    }

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
    private readonly struct ReferenceLoopScope(CookedBinarySerializer.ReferenceLoopGuard guard, object? instance) : IDisposable
    {
        public void Dispose()
        {
            if (instance is not null)
                guard.Exit(instance);
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
            LogMemoryPackSerializationFailure(runtimeType, ex);
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

            if (IsRuntimeOnlyType(value.GetType()))
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

            // System.Numerics types - fixed sizes
            if (value is Vector2)
            {
                AddBytes(sizeof(float) * 2);
                return;
            }

            if (value is Vector3)
            {
                AddBytes(sizeof(float) * 3);
                return;
            }

            if (value is Vector4 or Quaternion)
            {
                AddBytes(sizeof(float) * 4);
                return;
            }

            if (value is Matrix4x4)
            {
                AddBytes(sizeof(float) * 16);
                return;
            }

            // Color types - fixed sizes
            if (value is ColorF3)
            {
                AddBytes(sizeof(float) * 3);
                return;
            }

            if (value is ColorF4)
            {
                AddBytes(sizeof(float) * 4);
                return;
            }

            // Half type (2 bytes)
            if (value is Half)
            {
                AddBytes(2);
                return;
            }

            // DateTimeOffset (8 + 2 bytes for ticks + offset)
            if (value is DateTimeOffset)
            {
                AddBytes(sizeof(long) + sizeof(short));
                return;
            }

            // DateOnly (4 bytes for day number)
            if (value is DateOnly)
            {
                AddBytes(sizeof(int));
                return;
            }

            // TimeOnly (8 bytes for ticks)
            if (value is TimeOnly)
            {
                AddBytes(sizeof(long));
                return;
            }

            // System.Numerics.Plane (4 floats)
            if (value is System.Numerics.Plane)
            {
                AddBytes(sizeof(float) * 4);
                return;
            }

            // Uri (string)
            if (value is Uri uri)
            {
                AddBytes(SizeOfString(uri.OriginalString));
                return;
            }

            // Geometry types
            if (value is Segment)
            {
                AddBytes(sizeof(float) * 6); // 2 Vector3
                return;
            }

            if (value is Ray)
            {
                AddBytes(sizeof(float) * 6); // 2 Vector3
                return;
            }

            if (value is AABB)
            {
                AddBytes(sizeof(float) * 6); // 2 Vector3
                return;
            }

            if (value is Sphere)
            {
                AddBytes(sizeof(float) * 4); // Vector3 + float
                return;
            }

            if (value is Triangle)
            {
                AddBytes(sizeof(float) * 9); // 3 Vector3
                return;
            }

            if (value is Frustum)
            {
                // 8 corners (3 floats each) - planes reconstructed on deserialize
                AddBytes(sizeof(float) * 8 * 3);
                return;
            }

            // Medium-priority types
            if (value is Version version)
            {
                AddBytes(SizeOfString(version.ToString()));
                return;
            }

            if (value is BigInteger bigInt)
            {
                byte[] bigIntBytes = bigInt.ToByteArray();
                AddBytes(sizeof(int) + bigIntBytes.Length);
                return;
            }

            if (value is Complex)
            {
                AddBytes(sizeof(double) * 2);
                return;
            }

            if (value is IPAddress ip)
            {
                AddBytes(1 + ip.GetAddressBytes().Length); // length byte + address bytes
                return;
            }

            if (value is IPEndPoint endPoint)
            {
                AddBytes(1 + endPoint.Address.GetAddressBytes().Length + sizeof(int)); // length byte + address + port
                return;
            }

            if (value is Range)
            {
                AddBytes(sizeof(int) + sizeof(bool) + sizeof(int) + sizeof(bool)); // start value, start isFromEnd, end value, end isFromEnd
                return;
            }

            if (value is Index)
            {
                AddBytes(sizeof(int) + sizeof(bool)); // value + isFromEnd
                return;
            }

            // Low-priority types
            if (value is BitArray bitArray)
            {
                int byteCount = (bitArray.Length + 7) / 8;
                AddBytes(sizeof(int) + byteCount); // length + bytes
                return;
            }

            if (value is CultureInfo culture)
            {
                AddBytes(SizeOfString(culture.Name));
                return;
            }

            if (value is Regex regex)
            {
                AddBytes(SizeOfString(regex.ToString()) + sizeof(int)); // pattern + options
                return;
            }

            // XREvent and XREvent<T>
            if (value is XREvent xrEvent)
            {
                AddBytes(1); // marker
                AddXRPersistentCallListSize(xrEvent.PersistentCalls);
                return;
            }

            if (TryAddGenericXREventSize(value))
                return;

            Type runtimeType = value.GetType();

            // Nullable<T>
            Type? nullableUnderlying = Nullable.GetUnderlyingType(runtimeType);
            if (nullableUnderlying is not null)
            {
                AddBytes(1); // marker
                AddBytes(SizeOfTypeName(nullableUnderlying));
                AddBytes(sizeof(bool)); // hasValue
                // For boxed nullable, value is always present if we got here
                AddValue(value, allowCustom);
                return;
            }

            // ValueTuple
            if (IsValueTupleType(runtimeType))
            {
                var tuple = (ITuple)value;
                Type[] typeArgs = runtimeType.GetGenericArguments();
                AddBytes(1); // marker
                AddBytes(sizeof(int)); // count
                for (int i = 0; i < tuple.Length; i++)
                {
                    AddBytes(SizeOfTypeName(typeArgs[i]));
                    AddValue(tuple[i], allowCustom);
                }
                return;
            }

            // Type reference
            if (value is Type typeRef)
            {
                AddBytes(SizeOfTypeName(typeRef));
                return;
            }

            // HashSet<T>
            if (runtimeType.IsGenericType && runtimeType.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                Type elementType = runtimeType.GetGenericArguments()[0];
                AddBytes(SizeOfTypeName(elementType));
                AddBytes(sizeof(int)); // count
                foreach (object? item in (IEnumerable)value)
                    AddValue(item, allowCustom);
                return;
            }

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

            // Blittable struct fallback
            if (runtimeType.IsValueType && !runtimeType.IsPrimitive && !runtimeType.IsEnum && IsBlittableStruct(runtimeType))
            {
                int structSize = Marshal.SizeOf(runtimeType);
                AddBytes(SizeOfTypeName(runtimeType) + sizeof(int) + structSize);
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
                object? value = null;
                try
                {
                    value = member.GetValue(instance);
                }
                catch (Exception ex)
                {
                    LogMemberSerializationFailure(metadataType, member.Name, ex);
                }
                AddValue(value, allowCustom);
            }
        }

        private void AddXRPersistentCallListSize(List<XRPersistentCall>? calls)
        {
            AddBytes(sizeof(int)); // count
            if (calls is null || calls.Count == 0)
                return;

            foreach (var call in calls)
                AddXRPersistentCallSize(call);
        }

        private void AddXRPersistentCallSize(XRPersistentCall call)
        {
            AddBytes(16); // NodeId
            AddBytes(16); // TargetObjectId
            AddBytes(SizeOfString(call.MethodName ?? string.Empty));
            AddBytes(sizeof(bool)); // UseTupleExpansion
            AddBytes(sizeof(int)); // param count
            
            var paramTypes = call.ParameterTypeNames;
            if (paramTypes is { Length: > 0 })
            {
                foreach (var typeName in paramTypes)
                    AddBytes(SizeOfString(typeName ?? string.Empty));
            }
        }

        private bool TryAddGenericXREventSize(object value)
        {
            var runtimeType = value.GetType();
            if (!runtimeType.IsGenericType || runtimeType.GetGenericTypeDefinition() != typeof(XREvent<>))
                return false;

            AddBytes(1); // marker
            AddBytes(SizeOfTypeName(runtimeType.GetGenericArguments()[0])); // type argument
            
            var prop = runtimeType.GetProperty(nameof(XREvent.PersistentCalls));
            var calls = prop?.GetValue(value) as List<XRPersistentCall>;
            AddXRPersistentCallListSize(calls);
            return true;
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
                                  .Where(p =>
                                      p.GetCustomAttribute<YamlIgnoreAttribute>() is null &&
                                      p.GetCustomAttribute<RuntimeOnlyAttribute>() is null &&
                                      p.GetCustomAttribute<MemoryPackIgnoreAttribute>() is null &&
                                      !IsRuntimeOnlyType(p.PropertyType))
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

    private static bool IsRuntimeOnlyType(Type type)
        => type.GetCustomAttribute<RuntimeOnlyAttribute>(inherit: true) is not null;

}
