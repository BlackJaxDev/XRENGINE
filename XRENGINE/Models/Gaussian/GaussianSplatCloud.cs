using MemoryPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XREngine.Core.Files;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Models.Gaussian;

/// <summary>
/// Represents a collection of gaussian splats loaded from a binary .splat file.
/// </summary>
[Serializable]
[MemoryPackable(GenerateType.NoGenerate)]
public sealed partial class GaussianSplatCloud : XRAsset
{
    public const int RecordFloatCount = 14;
    public const int RecordByteCount = RecordFloatCount * sizeof(float);

    public GaussianSplatCloud()
    {
    }

    public GaussianSplatCloud(IEnumerable<GaussianSplat> splats)
    {
        _splats = [.. splats];
        RecalculateBounds();
    }

    private readonly List<GaussianSplat> _splats = [];
    public IReadOnlyList<GaussianSplat> Splats => _splats;

    public int Count => _splats.Count;

    private AABB _bounds;
    public AABB Bounds
    {
        get => _bounds;
        private set => SetField(ref _bounds, value);
    }

    public void Clear()
    {
        _splats.Clear();
        _bounds = default;
    }

    public void Add(GaussianSplat splat)
    {
        _splats.Add(splat);
        ExpandBounds(splat);
    }

    public void AddRange(IEnumerable<GaussianSplat> splats)
    {
        foreach (var splat in splats)
            Add(splat);
    }

    private void ExpandBounds(in GaussianSplat splat)
    {
        Vector3 extent = splat.Extents;
        Vector3 min = splat.Position - extent;
        Vector3 max = splat.Position + extent;

        if (_splats.Count == 1)
        {
            _bounds = new AABB(min, max);
        }
        else
        {
            _bounds = AABB.Union(_bounds, new AABB(min, max));
        }
    }

    public void RecalculateBounds()
    {
        if (_splats.Count == 0)
        {
            _bounds = default;
            return;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (var splat in _splats)
        {
            Vector3 extent = splat.Extents;
            min = Vector3.Min(min, splat.Position - extent);
            max = Vector3.Max(max, splat.Position + extent);
        }

        _bounds = new AABB(min, max);
    }

    public static GaussianSplatCloud? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    public static GaussianSplatCloud Load(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);

        long length = stream.Length - stream.Position;
        if (length < RecordByteCount)
            return new GaussianSplatCloud();

        // Detect optional header magic "GSPT"
        long startPosition = stream.Position;
        uint magic = reader.ReadUInt32();
        uint version = 0;

        bool hasHeader = magic == 0x54505347; // 'GSPT'
        if (hasHeader)
        {
            version = reader.ReadUInt32();
        }
        else
        {
            // No header, rewind and treat file as pure record stream
            stream.Position = startPosition;
            reader.BaseStream.Position = startPosition;
        }

        List<GaussianSplat> splats = [];

        while (stream.Position + RecordByteCount <= stream.Length)
        {
            Vector3 position = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 scale = new(MathF.Abs(reader.ReadSingle()), MathF.Abs(reader.ReadSingle()), MathF.Abs(reader.ReadSingle()));

            // Rotation quaternion stored as (x, y, z, w)
            Vector4 rotationRaw = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Quaternion rotation = Quaternion.Normalize(new Quaternion(rotationRaw.X, rotationRaw.Y, rotationRaw.Z, rotationRaw.W));

            Vector3 color = new(
                Math.Clamp(reader.ReadSingle(), 0.0f, 1.0f),
                Math.Clamp(reader.ReadSingle(), 0.0f, 1.0f),
                Math.Clamp(reader.ReadSingle(), 0.0f, 1.0f));
            float opacity = Math.Clamp(reader.ReadSingle(), 0.0f, 1.0f);

            splats.Add(new GaussianSplat(position, scale, rotation, color, opacity));
        }

        var cloud = new GaussianSplatCloud(splats);
        if (stream is FileStream fs)
            cloud.Name = Path.GetFileNameWithoutExtension(fs.Name);
        return cloud;
    }
}

public readonly record struct GaussianSplat(Vector3 Position, Vector3 Scale, Quaternion Rotation, Vector3 Color, float Opacity)
{
    public Vector3 Extents => Scale;

    public Vector4 ColorWithOpacity => new(Color, Math.Clamp(Opacity, 0.0f, 1.0f));
}
