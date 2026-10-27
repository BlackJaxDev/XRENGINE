using System;
using System.IO;
using System.Numerics;
using System.Text;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Models.Gaussian.Cache;

// Binary layout, little-endian. The format version is the first field.
//   u16 FormatVersion, u8 Representation, u16 MotionCodecVersion
//   4 x (i32 count, count x i32): SplatLevelCounts, StaticSplatCounts, DriverVertexCounts, DriverTriangleCounts
//   i32 FrameCount, f32 FrameRate, f32 DurationSeconds, u8 LoopMode
//   6 x f32 Bounds (min xyz, max xyz), u8 GridBits, f32 ColorScale
//   u8 HasCameraHints; if 1: 3 x f32 OrbitTarget, 5 x f32 (Min, Max, Default radius, yaw, pitch),
//     i32 key count, count x (f32 time, 3 x f32 position, 4 x f32 rotation)
//   i32 chunk count, count x (i32 Index, u8 Kind, u8 SplatLevel, u8 DriverLevel, u8 TemporalLevel,
//     i32 ByteLength, i32 hash length, hash UTF-8 bytes)
public sealed partial class GaussianCacheHeader
{
    private const int CameraKeyByteSize = 32;
    private const int ChunkInfoMinByteSize = 16;

    /// <summary>Returns the exact byte count that <see cref="Write"/> produces.</summary>
    public long CalculateByteSize()
    {
        long size = 2 + 1 + 2;
        size += 4 * 4L + 4L * (SplatLevelCounts.Length + StaticSplatCounts.Length + DriverVertexCounts.Length + DriverTriangleCounts.Length);
        size += 4 + 4 + 4 + 1;
        size += 24 + 1 + 4;
        size += 1;
        if (CameraHints is { } hints)
            size += 12 + 20 + 4 + (long)CameraKeyByteSize * hints.Path.Length;
        size += 4;
        foreach (GaussianCacheChunkInfo chunk in Chunks)
            size += ChunkInfoMinByteSize + System.Text.Encoding.UTF8.GetByteCount(chunk.Sha256 ?? string.Empty);
        return size;
    }

    public void Write(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Write(FormatVersion);
        writer.Write((byte)Representation);
        writer.Write(MotionCodecVersion);
        WriteInts(writer, SplatLevelCounts);
        WriteInts(writer, StaticSplatCounts);
        WriteInts(writer, DriverVertexCounts);
        WriteInts(writer, DriverTriangleCounts);
        writer.Write(FrameCount);
        writer.Write(FrameRate);
        writer.Write(DurationSeconds);
        writer.Write((byte)LoopMode);
        WriteVector3(writer, Bounds.Min);
        WriteVector3(writer, Bounds.Max);
        writer.Write(GridBits);
        writer.Write(ColorScale);

        if (CameraHints is { } hints)
        {
            writer.Write((byte)1);
            WriteVector3(writer, hints.OrbitTarget);
            writer.Write(hints.MinRadius);
            writer.Write(hints.MaxRadius);
            writer.Write(hints.DefaultRadius);
            writer.Write(hints.DefaultYawDegrees);
            writer.Write(hints.DefaultPitchDegrees);
            writer.Write(hints.Path.Length);
            foreach (GaussianCacheCameraKey key in hints.Path)
            {
                writer.Write(key.TimeSeconds);
                WriteVector3(writer, key.Position);
                writer.Write(key.Rotation.X);
                writer.Write(key.Rotation.Y);
                writer.Write(key.Rotation.Z);
                writer.Write(key.Rotation.W);
            }
        }
        else
            writer.Write((byte)0);

        writer.Write(Chunks.Length);
        foreach (GaussianCacheChunkInfo chunk in Chunks)
        {
            writer.Write(chunk.Index);
            writer.Write((byte)chunk.Kind);
            writer.Write(chunk.SplatLevel);
            writer.Write(chunk.DriverLevel);
            writer.Write(chunk.TemporalLevel);
            writer.Write(chunk.ByteLength);
            byte[] hash = System.Text.Encoding.UTF8.GetBytes(chunk.Sha256 ?? string.Empty);
            writer.Write(hash.Length);
            writer.Write(hash);
        }
    }

    /// <summary>Reads a header. Counts and lengths are checked against the bytes that remain.</summary>
    public static GaussianCacheHeader Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        GaussianCacheHeader header = new();
        try
        {
            header.FormatVersion = reader.ReadUInt16();
            if (header.FormatVersion > CurrentFormatVersion)
                throw new GaussianCacheFormatException(
                    GaussianCacheFormatException.UnsupportedFormatVersionCode,
                    $"Gaussian cache format version {header.FormatVersion} is newer than the supported version {CurrentFormatVersion}.");

            byte representation = reader.ReadByte();
            if (representation != (byte)GaussianCacheRepresentation.BakedDriver)
                throw new GaussianCacheFormatException(
                    GaussianCacheFormatException.UnsupportedRepresentationCode,
                    $"Gaussian cache representation {representation} is not supported.");
            header.Representation = GaussianCacheRepresentation.BakedDriver;

            header.MotionCodecVersion = reader.ReadUInt16();
            header.SplatLevelCounts = ReadInts(reader);
            header.StaticSplatCounts = ReadInts(reader);
            header.DriverVertexCounts = ReadInts(reader);
            header.DriverTriangleCounts = ReadInts(reader);
            header.FrameCount = ReadNonNegative(reader, "frame count");
            header.FrameRate = reader.ReadSingle();
            header.DurationSeconds = reader.ReadSingle();
            header.LoopMode = (GaussianCacheLoopMode)reader.ReadByte();
            Vector3 boundsMin = ReadVector3(reader);
            Vector3 boundsMax = ReadVector3(reader);
            header.Bounds = new AABB(boundsMin, boundsMax);
            header.GridBits = reader.ReadByte();
            header.ColorScale = reader.ReadSingle();

            byte hasHints = reader.ReadByte();
            if (hasHints > 1)
                throw Malformed("camera hints flag is not 0 or 1");
            if (hasHints == 1)
                header.CameraHints = ReadCameraHints(reader);

            int chunkCount = ReadCount(reader, ChunkInfoMinByteSize, "chunk count");
            GaussianCacheChunkInfo[] chunks = new GaussianCacheChunkInfo[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                int index = reader.ReadInt32();
                GaussianCacheChunkKind kind = (GaussianCacheChunkKind)reader.ReadByte();
                byte splatLevel = reader.ReadByte();
                byte driverLevel = reader.ReadByte();
                byte temporalLevel = reader.ReadByte();
                int byteLength = ReadNonNegative(reader, "chunk byte length");
                int hashLength = ReadCount(reader, 1, "chunk hash length");
                string hash = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(hashLength));
                chunks[i] = new GaussianCacheChunkInfo(index, kind, splatLevel, driverLevel, temporalLevel, byteLength, hash);
            }
            header.Chunks = chunks;
        }
        catch (EndOfStreamException)
        {
            throw Malformed("the header ends early");
        }

        return header;
    }

    private static GaussianCacheCameraHints ReadCameraHints(BinaryReader reader)
    {
        GaussianCacheCameraHints hints = new()
        {
            OrbitTarget = ReadVector3(reader),
            MinRadius = reader.ReadSingle(),
            MaxRadius = reader.ReadSingle(),
            DefaultRadius = reader.ReadSingle(),
            DefaultYawDegrees = reader.ReadSingle(),
            DefaultPitchDegrees = reader.ReadSingle(),
        };
        int keyCount = ReadCount(reader, CameraKeyByteSize, "camera key count");
        GaussianCacheCameraKey[] keys = new GaussianCacheCameraKey[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            float time = reader.ReadSingle();
            Vector3 position = ReadVector3(reader);
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float w = reader.ReadSingle();
            keys[i] = new GaussianCacheCameraKey(time, position, new Quaternion(x, y, z, w));
        }
        hints.Path = keys;
        return hints;
    }

    private static GaussianCacheFormatException Malformed(string reason)
        => new(GaussianCacheFormatException.MalformedCode, $"Gaussian cache header is malformed: {reason}.");

    private static long Remaining(BinaryReader reader)
    {
        Stream stream = reader.BaseStream;
        return stream.CanSeek ? stream.Length - stream.Position : long.MaxValue;
    }

    private static int ReadNonNegative(BinaryReader reader, string what)
    {
        int value = reader.ReadInt32();
        if (value < 0)
            throw Malformed($"{what} is negative");
        return value;
    }

    private static int ReadCount(BinaryReader reader, int elementByteSize, string what)
    {
        int count = ReadNonNegative(reader, what);
        if ((long)count * elementByteSize > Remaining(reader))
            throw Malformed($"{what} {count} is larger than the remaining data");
        return count;
    }

    private static int[] ReadInts(BinaryReader reader)
    {
        int count = ReadCount(reader, sizeof(int), "array length");
        int[] values = new int[count];
        for (int i = 0; i < count; i++)
            values[i] = reader.ReadInt32();
        return values;
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (int value in values)
            writer.Write(value);
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
