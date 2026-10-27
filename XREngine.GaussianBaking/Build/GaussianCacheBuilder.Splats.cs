using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Driver;
using XREngine.Rendering.Models.Gaussian.Baking.Encoding;
using XREngine.Rendering.Models.Gaussian.Baking.Sampling;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

// Splat stream sections. Each attribute is its own substream:
//   Binding    4 x u16 (bound: u, v, half h; static: grid x, y, z), then the flags. Vertex codec, stride 8.
//   Triangle   u32 (the static value for a static splat). Vertex codec, stride 4.
//   Rotation   quaternion filter at 12 bits, stride 8, then the vertex codec. Bound splats store the local rotation.
//   Scale      exponent filter at 16 bits with one exponent per splat, stride 12, then the vertex codec.
//   Color      color filter at 8 bits, stride 4, then the vertex codec. sRGB red, green, and blue, and linear opacity.
//   ChildCount u8 for each node, without a codec. Only levels above the last level have it.
public static partial class GaussianCacheBuilder
{
    private const int BindingStride = 8;
    private const int TriangleStride = 4;
    private const int RotationStride = 8;
    private const int ScaleStride = 12;
    private const int ColorStride = 4;
    private const int RotationBits = 12;
    private const int ScaleBits = 16;
    private const int ColorBits = 8;
    private const int VertexCodecLevel = 3;

    /// <summary>Makes the attribute streams of a level. Bound splats move to the stored driver triangles.</summary>
    private static FilteredSplatLevel FilterSplatLevel(
        GaussianCacheBuildInput input,
        int level,
        int staticCount,
        uint[][] storedTriangles,
        GridQuantizer quantizer,
        IMeshOptimizerBackend meshopt)
    {
        BakedSplat[] splats = input.Tree.Levels[level];
        int count = splats.Length;
        SplatBinding[] bindings = level < input.BindingsPerLevel.Length ? input.BindingsPerLevel[level] : [];
        (uint Triangle, byte Turn)[] map = bindings.Length > 0
            ? MapTriangles(input.Drivers.LevelTriangleIndices[level], storedTriangles[level])
            : [];

        byte[] binding = new byte[count * BindingStride];
        byte[] triangle = new byte[count * TriangleStride];
        float[] rotations = new float[count * 4];
        float[] scales = new float[count * 3];
        float[] colors = new float[count * 4];
        for (int node = 0; node < count; node++)
        {
            ref readonly BakedSplat splat = ref splats[node];
            ushort a, b, c, flags;
            uint triangleIndex;
            Quaternion rotation;
            if (node < staticCount)
            {
                quantizer.Quantize(splat.Position, out a, out b, out c);
                flags = GaussianCacheSplatRecord.StaticFlag;
                triangleIndex = GaussianCacheSplatRecord.StaticTriangle;
                rotation = splat.Rotation;
            }
            else
            {
                SplatBinding source = bindings[node - staticCount];
                SplatBinding bound = MoveBinding(source, map[source.Triangle], input.Drivers.LevelTriangleIndices[level], input.DriverRestPositions);
                a = PlaneCoordinate.Pack(bound.U);
                b = PlaneCoordinate.Pack(bound.V);
                c = BitConverter.HalfToUInt16Bits((Half)bound.H);
                flags = 0;
                triangleIndex = bound.Triangle;
                rotation = bound.LocalRotation;
            }

            Span<byte> bindingBytes = binding.AsSpan(node * BindingStride, BindingStride);
            BinaryPrimitives.WriteUInt16LittleEndian(bindingBytes, a);
            BinaryPrimitives.WriteUInt16LittleEndian(bindingBytes[2..], b);
            BinaryPrimitives.WriteUInt16LittleEndian(bindingBytes[4..], c);
            BinaryPrimitives.WriteUInt16LittleEndian(bindingBytes[6..], flags);
            BinaryPrimitives.WriteUInt32LittleEndian(triangle.AsSpan(node * TriangleStride), triangleIndex);

            rotation = rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
            rotations[node * 4] = rotation.X;
            rotations[node * 4 + 1] = rotation.Y;
            rotations[node * 4 + 2] = rotation.Z;
            rotations[node * 4 + 3] = rotation.W;

            scales[node * 3] = splat.Scale.X;
            scales[node * 3 + 1] = splat.Scale.Y;
            scales[node * 3 + 2] = splat.Scale.Z;

            // The color filter gets the 8-bit sRGB values, so the decoded bytes are close to the sRGB encoding of the color.
            colors[node * 4] = Srgb8.FromLinear(splat.LinearColor.X) / 255.0f;
            colors[node * 4 + 1] = Srgb8.FromLinear(splat.LinearColor.Y) / 255.0f;
            colors[node * 4 + 2] = Srgb8.FromLinear(splat.LinearColor.Z) / 255.0f;
            colors[node * 4 + 3] = Math.Clamp(splat.Opacity, 0.0f, 1.0f);
        }

        byte[] rotation16 = new byte[count * RotationStride];
        meshopt.EncodeFilterQuat(rotation16, count, RotationStride, RotationBits, rotations);
        byte[] scale = new byte[count * ScaleStride];
        meshopt.EncodeFilterExp(scale, count, ScaleStride, ScaleBits, scales, MeshOptimizerExpMode.SharedVector);
        byte[] color = new byte[count * ColorStride];
        meshopt.EncodeFilterColor(color, count, ColorStride, ColorBits, colors);

        byte[]? childCounts = null;
        if (level < input.Tree.LevelCount - 1)
        {
            int[] counts = input.Tree.ChildCounts[level];
            childCounts = new byte[count];
            for (int node = 0; node < count; node++)
                childCounts[node] = checked((byte)counts[node]);
        }

        return new FilteredSplatLevel(level, count, binding, triangle, rotation16, scale, color, childCounts);
    }

    /// <summary>Appends the splats of a level above 0. A split falls only between the children of two parents.</summary>
    private static void AppendSplatLevel(ChunkPlanner planner, FilteredSplatLevel splats, int[] parentChildCounts, IMeshOptimizerBackend meshopt)
    {
        int[] childStarts = new int[parentChildCounts.Length + 1];
        for (int parent = 0; parent < parentChildCounts.Length; parent++)
            childStarts[parent + 1] = childStarts[parent] + parentChildCounts[parent];

        planner.AppendSplit(
            parentChildCounts.Length,
            (firstParent, parentCount) =>
            {
                int first = childStarts[firstParent];
                return EncodeSplatSection(splats, first, childStarts[firstParent + parentCount] - first, meshopt);
            },
            $"splat level {splats.Level}");
    }

    /// <summary>Encodes the nodes <c>[first, first + count)</c> of a level as one splat streams section.</summary>
    private static GaussianCacheSection EncodeSplatSection(FilteredSplatLevel splats, int first, int count, IMeshOptimizerBackend meshopt)
    {
        GaussianCacheSplatStreamsWriter writer = new();
        writer.Append(GaussianCacheSplatAttribute.Binding, EncodeVertices(splats.Binding, first, count, BindingStride, meshopt));
        writer.Append(GaussianCacheSplatAttribute.Triangle, EncodeVertices(splats.Triangle, first, count, TriangleStride, meshopt));
        writer.Append(GaussianCacheSplatAttribute.Rotation, EncodeVertices(splats.Rotation, first, count, RotationStride, meshopt));
        writer.Append(GaussianCacheSplatAttribute.Scale, EncodeVertices(splats.Scale, first, count, ScaleStride, meshopt));
        writer.Append(GaussianCacheSplatAttribute.Color, EncodeVertices(splats.Color, first, count, ColorStride, meshopt));
        if (splats.ChildCounts is { } childCounts)
            writer.Append(GaussianCacheSplatAttribute.ChildCount, childCounts.AsSpan(first, count));

        return new GaussianCacheSection(GaussianCacheSectionKind.SplatStreams, (byte)splats.Level, 0, 0, first, count, 0, 0, writer.ToArray());
    }

    private static byte[] EncodeVertices(byte[] raw, int first, int count, int stride, IMeshOptimizerBackend meshopt)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(meshopt.EncodeVertexBufferBound(count, stride));
        try
        {
            int size = meshopt.EncodeVertexBuffer(buffer, raw.AsSpan(first * stride, count * stride), count, stride, VertexCodecLevel, MotionCodecVersion);
            return buffer.AsSpan(0, size).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
