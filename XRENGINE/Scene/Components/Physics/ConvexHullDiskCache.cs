using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Tools;

namespace XREngine.Components.Physics;

internal static class ConvexHullDiskCache
{
    private const uint FileMagic = 0x434F4143; // "CAOC"
    private const int FileVersion = 1;
    private const int HashSchemaVersion = 1;
    private const string CacheFolderName = "CoACD";
    private const string CacheVersionFolderName = "v1";

    internal static string? CacheRootOverride { get; set; }

    public static bool TryLoad(CoACD.CoACDParameters parameters, in ConvexHullInput input, out List<CoACD.ConvexHullMesh> hulls)
    {
        hulls = [];

        if (!TryResolveCacheFilePath(parameters, input, out string? cachePath))
            return false;

        if (!File.Exists(cachePath))
            return false;

        try
        {
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != FileMagic)
                return false;

            if (reader.ReadInt32() != FileVersion)
                return false;

            int hullCount = reader.ReadInt32();
            if (hullCount < 0)
                return false;

            hulls = new List<CoACD.ConvexHullMesh>(hullCount);
            for (int hullIndex = 0; hullIndex < hullCount; hullIndex++)
            {
                int vertexCount = reader.ReadInt32();
                int indexCount = reader.ReadInt32();
                if (vertexCount < 0 || indexCount < 0)
                    return false;

                var vertices = new Vector3[vertexCount];
                for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                {
                    vertices[vertexIndex] = new Vector3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle());
                }

                var indices = new int[indexCount];
                for (int index = 0; index < indexCount; index++)
                    indices[index] = reader.ReadInt32();

                hulls.Add(new CoACD.ConvexHullMesh(vertices, indices));
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CoACD Cache] Failed to load cached hulls '{cachePath}'. {ex.Message}");
            return false;
        }
    }

    public static void TryStore(CoACD.CoACDParameters parameters, in ConvexHullInput input, IReadOnlyList<CoACD.ConvexHullMesh> hulls)
    {
        if (!TryResolveCacheFilePath(parameters, input, out string? cachePath))
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(FileMagic);
                writer.Write(FileVersion);
                writer.Write(hulls.Count);

                for (int hullIndex = 0; hullIndex < hulls.Count; hullIndex++)
                {
                    var hull = hulls[hullIndex];
                    var vertices = hull.Vertices ?? Array.Empty<Vector3>();
                    var indices = hull.Indices ?? Array.Empty<int>();

                    writer.Write(vertices.Length);
                    writer.Write(indices.Length);

                    for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                    {
                        Vector3 vertex = vertices[vertexIndex];
                        writer.Write(vertex.X);
                        writer.Write(vertex.Y);
                        writer.Write(vertex.Z);
                    }

                    for (int index = 0; index < indices.Length; index++)
                        writer.Write(indices[index]);
                }
            }

            string destinationPath = cachePath!;
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(tempPath, destinationPath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CoACD Cache] Failed to store cached hulls '{cachePath}'. {ex.Message}");
        }
    }

    private static bool TryResolveCacheFilePath(CoACD.CoACDParameters parameters, in ConvexHullInput input, out string? cachePath)
    {
        cachePath = null;

        string? cacheRoot = ResolveCacheRoot();
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return false;

        ulong key = ComputeCacheKey(parameters, input);
        string keyHex = key.ToString("X16");
        cachePath = Path.Combine(cacheRoot, CacheFolderName, CacheVersionFolderName, keyHex[..2], $"{keyHex}.bin");
        return true;
    }

    private static string? ResolveCacheRoot()
    {
        if (!string.IsNullOrWhiteSpace(CacheRootOverride))
            return CacheRootOverride;

        string? gameCachePath = Engine.Assets.GameCachePath;
        if (!string.IsNullOrWhiteSpace(gameCachePath))
            return gameCachePath;

        string gameAssetsPath = Engine.Assets.GameAssetsPath;
        if (string.IsNullOrWhiteSpace(gameAssetsPath))
            return null;

        string normalizedAssetsPath = Path.GetFullPath(gameAssetsPath);
        if (string.Equals(Path.GetFileName(normalizedAssetsPath), "Assets", StringComparison.OrdinalIgnoreCase))
        {
            string? projectRoot = Path.GetDirectoryName(normalizedAssetsPath);
            return string.IsNullOrWhiteSpace(projectRoot) ? null : Path.Combine(projectRoot, "Cache");
        }

        if (Directory.Exists(Path.Combine(normalizedAssetsPath, "Assets")))
            return Path.Combine(normalizedAssetsPath, "Cache");

        string? parent = Path.GetDirectoryName(normalizedAssetsPath);
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, "Cache");
    }

    private static ulong ComputeCacheKey(CoACD.CoACDParameters parameters, in ConvexHullInput input)
    {
        var hash = new XxHash64();

        AppendInt32(hash, HashSchemaVersion);
        AppendParameters(hash, parameters);
        AppendInt32(hash, input.Positions.Length);
        hash.Append(MemoryMarshal.AsBytes(input.Positions.AsSpan()));
        AppendInt32(hash, input.Indices.Length);
        hash.Append(MemoryMarshal.AsBytes(input.Indices.AsSpan()));

        return hash.GetCurrentHashAsUInt64();
    }

    private static void AppendParameters(XxHash64 hash, CoACD.CoACDParameters parameters)
    {
        AppendDouble(hash, parameters.Threshold);
        AppendInt32(hash, parameters.MaxConvexHulls);
        AppendInt32(hash, (int)parameters.PreprocessMode);
        AppendInt32(hash, parameters.PreprocessResolution);
        AppendInt32(hash, parameters.SampleResolution);
        AppendInt32(hash, parameters.MctsNodes);
        AppendInt32(hash, parameters.MctsIterations);
        AppendInt32(hash, parameters.MctsMaxDepth);
        AppendBoolean(hash, parameters.EnablePca);
        AppendBoolean(hash, parameters.EnableMerge);
        AppendBoolean(hash, parameters.EnableDecimation);
        AppendInt32(hash, parameters.MaxConvexHullVertices);
        AppendBoolean(hash, parameters.EnableExtrusion);
        AppendDouble(hash, parameters.ExtrusionMargin);
        AppendInt32(hash, (int)parameters.ApproximationMode);
        AppendUInt32(hash, parameters.Seed);
    }

    private static void AppendBoolean(XxHash64 hash, bool value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value ? (byte)1 : (byte)0;
        hash.Append(buffer);
    }

    private static void AppendInt32(XxHash64 hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendUInt32(XxHash64 hash, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        hash.Append(buffer);
    }

    private static void AppendDouble(XxHash64 hash, double value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value));
        hash.Append(buffer);
    }
}