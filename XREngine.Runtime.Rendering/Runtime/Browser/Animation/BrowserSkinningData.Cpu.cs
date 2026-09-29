using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

public sealed partial class BrowserSkinningData
{
    /// <summary>CPU backend for the same packed position deformation contract consumed by the compute shader.</summary>
    public void EvaluatePositionsCpu(ReadOnlySpan<SkinPaletteMatrix> palette, ReadOnlySpan<Vector2> activeMorphs, Span<float> vertices)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(_packet);
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(_packet);
        int count = (int)words[2];
        if (vertices.Length != count * 5 || palette.Length != BoneCount || activeMorphs.Length > MorphCount)
            throw new ArgumentException("CPU deformation inputs must match the immutable packed mesh.");
        foreach (float value in MemoryMarshal.Cast<SkinPaletteMatrix, float>(palette))
            if (!float.IsFinite(value)) throw new ArgumentException("Palette components must be finite.");
        Span<bool> seen = stackalloc bool[256];
        seen.Clear();
        for (int i = 0; i < activeMorphs.Length; i++)
        {
            Vector2 active = activeMorphs[i];
            int shape = (int)active.X;
            if (!float.IsFinite(active.X) || active.X != shape || shape < 0 || shape >= MorphCount ||
                seen[shape] || !float.IsFinite(active.Y) || MathF.Abs(active.Y) > 100)
                throw new ArgumentException("Active morphs require unique in-range indices and bounded weights.");
            seen[shape] = true;
        }
        int format = (int)words[4];
        for (int vertex = 0; vertex < count; vertex++)
        {
            int input = (int)words[8] + vertex * 5;
            Vector3 position = ReadVector(values, input);
            Vector3 accumulated = Vector3.Zero;
            for (int active = 0; active < activeMorphs.Length; active++)
            {
                float weight = activeMorphs[active].Y;
                if (MathF.Abs(weight) <= values[24]) continue;
                int shape = (int)activeMorphs[active].X;
                int range = (int)words[15] + shape * 4;
                int low = (int)words[range], high = low + (int)words[range + 1];
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    int record = (int)words[16] + middle * 4;
                    uint candidate = words[record];
                    if (candidate < vertex) low = middle + 1;
                    else if (candidate > vertex) high = middle;
                    else
                    {
                        Vector3 delta = DecodeDelta(words, values, (int)words[record + 1], shape) * weight;
                        accumulated = (words[5] & 8) != 0 ? Vector3.Max(accumulated, delta) : accumulated + delta;
                        break;
                    }
                }
            }
            position += accumulated;
            if (BoneCount > 0)
            {
                Vector3 skinned = Vector3.Zero;
                float total = 0;
                uint weights = words[(int)words[12] + vertex];
                for (int lane = 0; lane < Math.Min(4u, words[19]); lane++)
                {
                    uint packed = words[(int)words[11] + vertex * format + (format == 1 ? 0 : lane / 2)];
                    int bone = (int)(format == 1 ? (packed >> (lane * 8)) & 255 : (packed >> ((lane & 1) * 16)) & 65535);
                    AccumulatePosition(palette, bone, ((weights >> (lane * 8)) & 255) / 255f, position, ref skinned, ref total);
                }
                if ((words[5] & 16) != 0)
                {
                    uint header = words[(int)words[13] + vertex];
                    int offset = (int)(header & 0xffffff), length = (int)(header >> 24);
                    for (int lane = 0; lane < length && lane + 4 < words[19]; lane++)
                    {
                        uint entry = words[(int)words[14] + offset + lane];
                        AccumulatePosition(palette, (int)(entry & 65535), ((entry >> 16) & 255) / 255f,
                            position, ref skinned, ref total);
                    }
                }
                if (total > 0.0001f) position = skinned;
            }
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidOperationException("Packed CPU deformation exceeded the finite position range.");
            int output = vertex * 5;
            vertices[output] = position.X;
            vertices[output + 1] = position.Y;
            vertices[output + 2] = position.Z;
            vertices[output + 3] = values[input + 3];
            vertices[output + 4] = values[input + 4];
        }
    }

    private static void AccumulatePosition(ReadOnlySpan<SkinPaletteMatrix> palette, int bone, float weight,
        Vector3 position, ref Vector3 sum, ref float total)
    {
        if (weight <= 0 || (uint)bone >= palette.Length) return;
        SkinPaletteMatrix matrix = palette[bone];
        Vector4 source = new(position, 1);
        sum += new Vector3(Vector4.Dot(matrix.Row0, source), Vector4.Dot(matrix.Row1, source), Vector4.Dot(matrix.Row2, source)) * weight;
        total += weight;
    }

    private static Vector3 DecodeDelta(ReadOnlySpan<uint> words, ReadOnlySpan<float> values, int delta, int shape)
    {
        if (delta == 0) return Vector3.Zero;
        int offset = (int)words[17] + delta * 2;
        uint xy = words[offset], z = words[offset + 1];
        Vector3 packed = new(MathF.Max(-1, unchecked((short)xy) / 32767f),
            MathF.Max(-1, unchecked((short)(xy >> 16)) / 32767f), MathF.Max(-1, unchecked((short)z) / 32767f));
        int metadata = (int)words[18] + shape * 16;
        return ReadVector(values, metadata + 12) + packed * ReadVector(values, metadata + 8);
    }

    private static Vector3 ReadVector(ReadOnlySpan<float> values, int offset)
        => new(values[offset], values[offset + 1], values[offset + 2]);

    private void ValidateCanonicalRecords()
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(_packet);
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(_packet);
        for (int i = (int)words[8]; i < words[11]; i++)
            if (!float.IsFinite(values[i])) throw new ArgumentException("Bind attributes must be finite.");
        for (int i = (int)words[18]; i < words.Length; i++)
            if (!float.IsFinite(values[i])) throw new ArgumentException("Morph metadata must be finite.");
        int format = (int)words[4];
        for (int vertex = 0; vertex < words[2]; vertex++)
        {
            uint weights = words[(int)words[12] + vertex];
            for (int lane = 0; lane < 4; lane++)
            {
                uint packed = words[(int)words[11] + vertex * format + (format == 1 ? 0 : lane / 2)];
                uint bone = format == 1 ? (packed >> (lane * 8)) & 255 : (packed >> ((lane & 1) * 16)) & 65535;
                if (BoneCount > 0 && ((weights >> (lane * 8)) & 255) != 0 && bone >= BoneCount)
                    throw new ArgumentException("Core influence exceeds the palette.");
            }
            if ((words[5] & 16) != 0)
            {
                uint header = words[(int)words[13] + vertex];
                if ((header & 0xffffff) + (header >> 24) > words[22]) throw new ArgumentException("Spill range exceeds its buffer.");
            }
        }
        for (int i = 0; i < words[22]; i++)
        {
            uint entry = words[(int)words[14] + i];
            if ((entry >> 24) != 0 || (BoneCount > 0 && ((entry >> 16) & 255) != 0 && (entry & 65535) >= BoneCount))
                throw new ArgumentException("Invalid packed spill influence.");
        }
        if (words[21] > 0 && (words[(int)words[17]] != 0 || words[(int)words[17] + 1] != 0))
            throw new ArgumentException("Quantized delta zero must be the null sentinel.");
        for (int shape = 0; shape < MorphCount; shape++)
        {
            int range = (int)words[15] + shape * 4;
            uint start = words[range], count = words[range + 1];
            if (start > words[20] || count > words[20] - start) throw new ArgumentException("Sparse shape range exceeds its records.");
            int previous = -1;
            for (int i = (int)start; i < start + count; i++)
            {
                int record = (int)words[16] + i * 4;
                uint vertex = words[record];
                if (vertex >= words[2] || vertex <= previous) throw new ArgumentException("Sparse vertices must be unique and sorted per shape.");
                previous = (int)vertex;
                for (int lane = 1; lane < 4; lane++)
                    if (words[record + lane] != 0 && words[record + lane] >= words[21]) throw new ArgumentException("Sparse delta index exceeds its buffer.");
            }
        }
    }
}
