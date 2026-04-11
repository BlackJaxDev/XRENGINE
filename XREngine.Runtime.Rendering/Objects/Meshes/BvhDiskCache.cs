using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SimpleScene.Util.ssBVH;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Disk-level cache for mesh BVH trees, keyed by an XxHash64 of the triangle
/// vertex positions.  Layout mirrors <c>ConvexHullDiskCache</c>.
/// <para>Cache path: <c>{CacheRoot}/BVH/v1/{hex[0:2]}/{hex16}.bin</c></para>
/// </summary>
internal static class BvhDiskCache
{
    private const uint FileMagic = 0x42564854; // "BVHT"
    private const int FileVersion = 1;
    private const int HashSchemaVersion = 1;
    private const string CacheFolderName = "BVH";
    private const string CacheVersionFolderName = "v1";

    /// <summary>
    /// Absolute path to the cache root directory.  Falls back to
    /// <see cref="RuntimeRenderingHostServices.GameCachePath"/> when not
    /// explicitly overridden.
    /// </summary>
    internal static string? CacheRootOverride { get; set; }

    // ------------------------------------------------------------------
    //  Load
    // ------------------------------------------------------------------

    public static bool TryLoad(
        List<Triangle> triangles,
        List<IndexTriangle> indexTriangles,
        out BVH<Triangle>? bvh,
        out Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)>? triangleLookup)
    {
        bvh = null;
        triangleLookup = null;

        if (!TryResolveCacheFilePath(triangles, out string? cachePath) || !File.Exists(cachePath))
            return false;

        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != FileMagic)
                return false;
            if (reader.ReadInt32() != FileVersion)
                return false;

            int triangleCount = reader.ReadInt32();
            int nodeCount = reader.ReadInt32();
            if (triangleCount < 0 || nodeCount < 0)
                return false;

            // Verify the triangle count matches the mesh we're loading for.
            if (triangleCount != triangles.Count)
                return false;

            // Read index-triangle data (for TriangleLookup reconstruction).
            var indexTriData = new (int P0, int P1, int P2)[triangleCount];
            for (int i = 0; i < triangleCount; i++)
            {
                indexTriData[i] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }

            // Read flat BVH nodes.
            int nodeByteCount = nodeCount * Unsafe.SizeOf<BVH<Triangle>.FlatBvhNode>();
            byte[] nodeBytes = reader.ReadBytes(nodeByteCount);
            if (nodeBytes.Length != nodeByteCount)
                return false;

            var flatNodes = MemoryMarshal.Cast<byte, BVH<Triangle>.FlatBvhNode>(nodeBytes.AsSpan());

            // Validate all leaf object indices are in range.
            for (int i = 0; i < flatNodes.Length; i++)
            {
                int idx = flatNodes[i].ObjectIndex;
                if (idx >= triangleCount)
                    return false;
            }

            // Reconstruct the BVH tree from flat nodes.
            var adapter = new TriangleAdapter();
            bvh = BVH<Triangle>.FromFlatNodes(adapter, triangles, flatNodes);

            // Reconstruct TriangleLookup.
            triangleLookup = new Dictionary<Triangle, (IndexTriangle, int)>(triangleCount);
            for (int i = 0; i < triangleCount; i++)
            {
                var (p0, p1, p2) = indexTriData[i];
                triangleLookup[triangles[i]] = (new IndexTriangle(p0, p1, p2), i);
            }

            return true;
        }
        catch (Exception ex)
        {
            RuntimeRenderingHostServices.Current.LogWarning($"[BVH Cache] Failed to load cached BVH '{cachePath}'. {ex.Message}");
            bvh = null;
            triangleLookup = null;
            return false;
        }
    }

    // ------------------------------------------------------------------
    //  Store
    // ------------------------------------------------------------------

    public static void TryStore(
        List<Triangle> triangles,
        Dictionary<Triangle, (IndexTriangle Indices, int FaceIndex)> triangleLookup,
        BVH<Triangle> bvh)
    {
        if (!TryResolveCacheFilePath(triangles, out string? cachePath))
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);

            // Build a triangle → index map so the flat-node serializer can record
            // which triangle lives in each leaf.
            var triangleIndexMap = new Dictionary<Triangle, int>(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
                triangleIndexMap[triangles[i]] = i;

            BVH<Triangle>.FlatBvhNode[] flatNodes = bvh.ToFlatNodes(tri =>
                triangleIndexMap.TryGetValue(tri, out int idx) ? idx : -1);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(triangles.Count);
                writer.Write(flatNodes.Length);

                // Write index-triangle data so we can reconstruct TriangleLookup on load.
                for (int i = 0; i < triangles.Count; i++)
                {
                    if (triangleLookup.TryGetValue(triangles[i], out var entry))
                    {
                        writer.Write(entry.Indices.Point0);
                        writer.Write(entry.Indices.Point1);
                        writer.Write(entry.Indices.Point2);
                    }
                    else
                    {
                        writer.Write(0);
                        writer.Write(0);
                        writer.Write(0);
                    }
                }

                // Write flat BVH nodes as a contiguous block.
                ReadOnlySpan<byte> nodeBytes = MemoryMarshal.AsBytes(flatNodes.AsSpan());
                writer.Write(nodeBytes);
            }

            if (File.Exists(cachePath))
                File.Delete(cachePath);

            File.Move(tempPath, cachePath!);
        }
        catch (Exception ex)
        {
            RuntimeRenderingHostServices.Current.LogWarning($"[BVH Cache] Failed to store BVH cache '{cachePath}'. {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    //  Hashing / path resolution
    // ------------------------------------------------------------------

    private static bool TryResolveCacheFilePath(List<Triangle> triangles, out string? cachePath)
    {
        cachePath = null;
        string? cacheRoot = CacheRootOverride ?? RuntimeRenderingHostServices.GameCachePath;
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return false;

        ulong key = ComputeCacheKey(triangles);
        string keyHex = key.ToString("X16");
        cachePath = Path.Combine(cacheRoot, CacheFolderName, CacheVersionFolderName, keyHex[..2], $"{keyHex}.bin");
        return true;
    }

    private static ulong ComputeCacheKey(List<Triangle> triangles)
    {
        var hash = new XxHash64();

        Span<byte> intBuf = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(intBuf, HashSchemaVersion);
        hash.Append(intBuf);

        BinaryPrimitives.WriteInt32LittleEndian(intBuf, triangles.Count);
        hash.Append(intBuf);

        // Hash every triangle's vertex positions.
        Span<byte> vecBuf = stackalloc byte[sizeof(float) * 3];
        for (int i = 0; i < triangles.Count; i++)
        {
            Triangle tri = triangles[i];
            WriteVector3(vecBuf, tri.A); hash.Append(vecBuf);
            WriteVector3(vecBuf, tri.B); hash.Append(vecBuf);
            WriteVector3(vecBuf, tri.C); hash.Append(vecBuf);
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static void WriteVector3(Span<byte> buffer, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(buffer, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[sizeof(float)..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(buffer[(sizeof(float) * 2)..], v.Z);
    }
}
