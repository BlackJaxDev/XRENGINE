using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using XREngine.Core.Files;
using YamlDotNet.Serialization;

namespace XREngine.Cutscenes;

/// <summary>
/// Computes the content hash that tells if the residency manifest of a cutscene asset is current.
/// The hash covers the duration, the restore setting, the bindings, and the tracks with their clips. It does not cover
/// the manifest, the stored hash, the name, or the file path.
/// </summary>
/// <remarks>
/// <para>
/// The hash walks the object graph of the bindings and the tracks. For each object it adds a stable type name (the
/// namespace and the type name, with no assembly name or version), then each public instance property with a public
/// getter and setter and no <see cref="YamlIgnoreAttribute"/>, and each public instance field, in name order. A list
/// adds its count and its items. A referenced <see cref="XRAsset"/>, for example an animation clip, adds only its ID,
/// so an edit inside the referenced asset does not make the manifest stale. A runtime or engine upgrade does not
/// change the hash, but a rename or a move of a track or clip type does.
/// </para>
/// <para>
/// The value is a 64-bit FNV-1a hash. It is not a security hash. The calculation uses reflection and allocates,
/// so call it at an open, at a bake, or after an edit, and not in each frame.
/// </para>
/// </remarks>
public static class CutsceneManifestHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;
    private const int MaxDepth = 32;

    private static readonly ConcurrentDictionary<Type, MemberInfo[]> MemberCache = new();
    private static readonly ConcurrentDictionary<Type, string> TypeNameCache = new();

    /// <summary>Computes the content hash of an asset.</summary>
    /// <param name="asset">The asset.</param>
    public static ulong Compute(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        ulong hash = OffsetBasis;
        hash = AddInt64(hash, asset.Duration.Flicks);
        hash = AddInt64(hash, asset.ForceRestoreAll ? 1L : 0L);
        hash = AddValue(hash, asset.Bindings, 0);
        hash = AddValue(hash, asset.Tracks, 0);
        return hash;
    }

    /// <summary>Gets the state of the manifest of an asset. It does not validate the manifest data.</summary>
    /// <param name="asset">The asset.</param>
    public static ECutsceneManifestState GetState(CutsceneAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Manifest is null)
            return ECutsceneManifestState.Missing;
        return asset.ManifestContentHash == Compute(asset) ? ECutsceneManifestState.Current : ECutsceneManifestState.Stale;
    }

    private static ulong AddValue(ulong hash, object? value, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidOperationException("The cutscene data is nested too deeply for the content hash.");

        switch (value)
        {
            case null:
                return AddByte(hash, 0);
            case string text:
                return AddString(AddByte(hash, 1), text);
            case bool flag:
                return AddInt64(AddByte(hash, 2), flag ? 1L : 0L);
            case Enum enumValue:
                return AddInt64(AddByte(hash, 3), ToInt64(enumValue, Enum.GetUnderlyingType(enumValue.GetType())));
            case float single:
                return AddInt64(AddByte(hash, 4), BitConverter.SingleToInt32Bits(single));
            case double number:
                return AddInt64(AddByte(hash, 5), BitConverter.DoubleToInt64Bits(number));
            case Guid guid:
                return AddGuid(AddByte(hash, 6), guid);
            case XRAsset reference:
                return AddGuid(AddByte(hash, 7), reference.ID);
            case IConvertible convertible when value.GetType().IsPrimitive:
                return AddInt64(AddByte(hash, 8), ToInt64(convertible, value.GetType()));
            case IEnumerable items:
                hash = AddByte(hash, 9);
                int count = 0;
                foreach (object? item in items)
                {
                    hash = AddValue(hash, item, depth + 1);
                    count++;
                }
                return AddInt64(hash, count);
        }

        Type type = value.GetType();
        hash = AddString(AddByte(hash, 10), StableTypeName(type));
        foreach (MemberInfo member in GetMembers(type))
        {
            hash = AddString(hash, member.Name);
            object? memberValue = member is PropertyInfo property ? property.GetValue(value) : ((FieldInfo)member).GetValue(value);
            hash = AddValue(hash, memberValue, depth + 1);
        }
        return hash;
    }

    private static MemberInfo[] GetMembers(Type type)
        => MemberCache.GetOrAdd(type, static t =>
        {
            List<MemberInfo> members = [];
            foreach (PropertyInfo property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length > 0 || property.GetGetMethod() is null || property.GetSetMethod() is null)
                    continue;
                if (property.IsDefined(typeof(YamlIgnoreAttribute), true))
                    continue;
                members.Add(property);
            }
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (!field.IsDefined(typeof(YamlIgnoreAttribute), true))
                    members.Add(field);

            members.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            return [.. members];
        });

    /// <summary>The namespace, the type name, and the generic arguments, with no assembly name or version.</summary>
    private static string StableTypeName(Type type)
        => TypeNameCache.GetOrAdd(type, static t =>
        {
            StringBuilder builder = new();
            AppendTypeName(builder, t);
            return builder.ToString();
        });

    private static void AppendTypeName(StringBuilder builder, Type type)
    {
        if (type.DeclaringType is { } declaring && !type.IsGenericParameter)
        {
            AppendTypeName(builder, declaring);
            builder.Append('+');
        }
        else if (!string.IsNullOrEmpty(type.Namespace))
            builder.Append(type.Namespace).Append('.');

        builder.Append(type.Name);
        if (!type.IsGenericType)
            return;

        builder.Append('[');
        Type[] arguments = type.GetGenericArguments();
        for (int i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            AppendTypeName(builder, arguments[i]);
        }
        builder.Append(']');
    }

    /// <summary>Converts an integer value to 64 bits. An unsigned 64-bit value keeps its bits.</summary>
    private static long ToInt64(IConvertible value, Type integerType)
        => integerType == typeof(ulong)
            ? unchecked((long)value.ToUInt64(System.Globalization.CultureInfo.InvariantCulture))
            : value.ToInt64(System.Globalization.CultureInfo.InvariantCulture);

    private static ulong AddByte(ulong hash, byte value)
        => (hash ^ value) * Prime;

    private static ulong AddInt64(ulong hash, long value)
    {
        for (int i = 0; i < sizeof(long); i++)
            hash = AddByte(hash, (byte)(value >> (i * 8)));
        return hash;
    }

    private static ulong AddGuid(ulong hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        for (int i = 0; i < bytes.Length; i++)
            hash = AddByte(hash, bytes[i]);
        return hash;
    }

    private static ulong AddString(ulong hash, string value)
    {
        hash = AddInt64(hash, value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            hash = AddByte(hash, (byte)value[i]);
            hash = AddByte(hash, (byte)(value[i] >> 8));
        }
        return hash;
    }
}
