using System.Buffers.Binary;
using System.Text;

namespace XREngine.Scene.Importers;

/// <summary>
/// Generates Unity ModelImporter local file identifiers for fileIdsGeneration 2.
/// </summary>
public static class SerializedModelFileId
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static long ForGameObject(string hierarchyPath)
        => Compute("GameObject", NormalizeHierarchyPath(hierarchyPath));

    public static long ForTransform(string hierarchyPath)
        => Compute("Transform", $"{NormalizeHierarchyPath(hierarchyPath)}/Transform");

    public static long ForComponent(string componentType, string hierarchyPath)
        => Compute(componentType, $"{NormalizeHierarchyPath(hierarchyPath)}/{componentType}");

    public static long ForMesh(string meshName)
        => Compute("Mesh", meshName);

    public static long Compute(string sourceTypeName, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        byte[] bytes = Encoding.UTF8.GetBytes($"Type:{sourceTypeName}->{identifier}0");
        return unchecked((long)Hash64(bytes));
    }

    private static string NormalizeHierarchyPath(string hierarchyPath)
    {
        string path = hierarchyPath.Replace('\\', '/').TrimEnd('/');
        if (path.StartsWith("//RootNode", StringComparison.Ordinal))
            return path;
        return string.IsNullOrEmpty(path)
            ? "//RootNode"
            : $"//RootNode/{path.TrimStart('/')}";
    }

    private static ulong Hash64(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        ulong hash;
        if (data.Length >= 32)
        {
            ulong v1 = unchecked(Prime1 + Prime2);
            ulong v2 = Prime2;
            ulong v3 = 0;
            ulong v4 = unchecked(0UL - Prime1);
            int limit = data.Length - 32;
            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
                offset += 8;
            }
            while (offset <= limit);

            hash = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = Prime5;
        }

        hash += (ulong)data.Length;
        while (offset <= data.Length - 8)
        {
            ulong lane = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
            hash ^= lane;
            hash = RotateLeft(hash, 27) * Prime1 + Prime4;
            offset += 8;
        }

        if (offset <= data.Length - 4)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) * Prime1;
            hash = RotateLeft(hash, 23) * Prime2 + Prime3;
            offset += 4;
        }

        while (offset < data.Length)
        {
            hash ^= data[offset] * Prime5;
            hash = RotateLeft(hash, 11) * Prime1;
            offset++;
        }

        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong Round(ulong accumulator, ulong input)
    {
        accumulator += input * Prime2;
        accumulator = RotateLeft(accumulator, 31);
        return accumulator * Prime1;
    }

    private static ulong MergeRound(ulong accumulator, ulong value)
    {
        accumulator ^= Round(0, value);
        return accumulator * Prime1 + Prime4;
    }

    private static ulong RotateLeft(ulong value, int count)
        => (value << count) | (value >> (64 - count));
}
