using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using XREngine.Data;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files;

public interface ICookedBinarySerializable
{
    void WriteCookedBinary(CookedBinaryWriter writer);
    void ReadCookedBinary(CookedBinaryReader reader);
}

public sealed class CookedBinaryWriter
{
    private readonly BinaryWriter _writer;

    internal CookedBinaryWriter(BinaryWriter writer)
    {
        _writer = writer;
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteValue(object? value)
        => CookedBinarySerializer.WriteValue(_writer, value, allowCustom: true);

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void WriteBaseObject<T>(T instance) where T : class
        => CookedBinarySerializer.WriteObjectContent(_writer, instance!, typeof(T), allowCustom: true);

    public void WriteBytes(ReadOnlySpan<byte> data)
        => _writer.Write(data);
}

public sealed class CookedBinaryReader
{
    private readonly BinaryReader _reader;

    internal CookedBinaryReader(BinaryReader reader)
    {
        _reader = reader;
    }

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public T? ReadValue<T>()
        => (T?)CookedBinarySerializer.ReadValue(_reader, typeof(T));

    [RequiresUnreferencedCode(CookedBinarySerializer.ReflectionWarningMessage)]
    [RequiresDynamicCode(CookedBinarySerializer.ReflectionWarningMessage)]
    public void ReadBaseObject<T>(T instance) where T : class
        => CookedBinarySerializer.ReadObjectContent(_reader, instance!, typeof(T));

    public byte[] ReadBytes(int length)
        => _reader.ReadBytes(length);
}

internal enum CookedBinaryTypeMarker : byte
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
    TimeSpan = 17,
    ByteArray = 18,
    Enum = 19,
    Array = 20,
    List = 21,
    Dictionary = 22,
    Object = 23,
    CustomObject = 24,
    DataSource = 25
}

public static class CookedBinarySerializer
{
    internal const string ReflectionWarningMessage = "Cooked binary serialization relies on reflection and cannot be statically analyzed for trimming or AOT";

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static byte[] Serialize(object? value)
    {
        using MemoryStream stream = new();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteValue(writer, value, allowCustom: true);
        }

        return stream.ToArray();
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    public static object? Deserialize(Type expectedType, ReadOnlySpan<byte> data)
    {
        using MemoryStream stream = new(data.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        object? value = ReadValue(reader, expectedType);
        return ConvertValue(value, expectedType);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteValue(BinaryWriter writer, object? value, bool allowCustom)
    {
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
                WriteValue(writer, item, allowCustom);
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
                WriteValue(writer, entry.Key, allowCustom);
                WriteValue(writer, entry.Value, allowCustom);
            }

            return;
        }

        if (value is IList list)
        {
            writer.Write((byte)CookedBinaryTypeMarker.List);
            WriteTypeName(writer, runtimeType);
            writer.Write(list.Count);
            foreach (object? item in list)
                WriteValue(writer, item, allowCustom);
            return;
        }

        if (allowCustom && value is ICookedBinarySerializable custom)
        {
            writer.Write((byte)CookedBinaryTypeMarker.CustomObject);
            WriteTypeName(writer, runtimeType);
            custom.WriteCookedBinary(new CookedBinaryWriter(writer));
            return;
        }

        writer.Write((byte)CookedBinaryTypeMarker.Object);
        WriteTypeName(writer, runtimeType);
        WriteObjectContent(writer, value, runtimeType, allowCustom);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static object? ReadValue(BinaryReader reader, Type? expectedType)
    {
        var marker = (CookedBinaryTypeMarker)reader.ReadByte();
        return marker switch
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
            CookedBinaryTypeMarker.Array => ReadArray(reader),
            CookedBinaryTypeMarker.List => ReadList(reader),
            CookedBinaryTypeMarker.Dictionary => ReadDictionary(reader),
            CookedBinaryTypeMarker.CustomObject => ReadCustomObject(reader, expectedType),
            CookedBinaryTypeMarker.DataSource => ReadDataSource(reader),
            CookedBinaryTypeMarker.Object => ReadObject(reader),
            _ => throw new NotSupportedException($"Unknown cooked binary marker '{marker}'.")
        };
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void WriteObjectContent(BinaryWriter writer, object instance, Type metadataType, bool allowCustom)
    {
        var metadata = TypeMetadataCache.Get(metadataType);
        writer.Write(metadata.Members.Length);
        foreach (var member in metadata.Members)
        {
            writer.Write(member.Name);
            object? value = member.GetValue(instance);
            WriteValue(writer, value, allowCustom);
        }
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    internal static void ReadObjectContent(BinaryReader reader, object instance, Type metadataType)
    {
        int count = reader.ReadInt32();
        var metadata = TypeMetadataCache.Get(metadataType);
        for (int i = 0; i < count; i++)
        {
            string propertyName = reader.ReadString();
            object? value = ReadValue(reader, null);
            if (metadata.TryGetMember(propertyName, out var member))
            {
                object? converted = ConvertValue(value, member.MemberType);
                member.SetValue(instance, converted);
            }
        }
    }

    private static byte[] ReadByteArray(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return reader.ReadBytes(length);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadEnum(BinaryReader reader)
    {
        string typeName = reader.ReadString();
        Type enumType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve enum type '{typeName}'.");
        long raw = reader.ReadInt64();
        return Enum.ToObject(enumType, raw);
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadArray(BinaryReader reader)
    {
        string elementTypeName = reader.ReadString();
        Type elementType = ResolveType(elementTypeName) ?? typeof(object);
        int length = reader.ReadInt32();
        Array array = Array.CreateInstance(elementType, length);
        for (int i = 0; i < length; i++)
        {
            object? value = ReadValue(reader, elementType);
            array.SetValue(ConvertValue(value, elementType), i);
        }

        return array;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadList(BinaryReader reader)
    {
        string listTypeName = reader.ReadString();
        Type listType = ResolveType(listTypeName) ?? typeof(List<object?>);
        IList list = (IList)(CreateInstance(listType) ?? new List<object?>());
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            object? value = ReadValue(reader, null);
            list.Add(value);
        }

        return list;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object ReadDictionary(BinaryReader reader)
    {
        string dictTypeName = reader.ReadString();
        Type dictType = ResolveType(dictTypeName) ?? typeof(Dictionary<object, object?>);
        IDictionary dictionary = (IDictionary)(CreateInstance(dictType) ?? new Dictionary<object, object?>());
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            object? key = ReadValue(reader, null);
            object? value = ReadValue(reader, null);
            dictionary[key!] = value;
        }

        return dictionary;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadCustomObject(BinaryReader reader, Type? expectedType)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName) ?? expectedType ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
        if (CreateInstance(targetType) is not ICookedBinarySerializable instance)
            throw new InvalidOperationException($"Type '{targetType}' does not implement {nameof(ICookedBinarySerializable)}.");

        instance.ReadCookedBinary(new CookedBinaryReader(reader));
        return instance;
    }

    [RequiresUnreferencedCode(ReflectionWarningMessage)]
    [RequiresDynamicCode(ReflectionWarningMessage)]
    private static object? ReadObject(BinaryReader reader)
    {
        string typeName = reader.ReadString();
        Type targetType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
        object instance = CreateInstance(targetType) ?? throw new InvalidOperationException($"Unable to create instance of '{targetType}'.");
        ReadObjectContent(reader, instance, targetType);
        return instance;
    }

    private static object ReadDataSource(BinaryReader reader)
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

        return System.Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static void WriteTypeName(BinaryWriter writer, Type type)
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
