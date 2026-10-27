using System.Buffers;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Cache;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Encoding;

/// <summary>Encodes driver vertex positions as prediction residuals.</summary>
public static class MotionEncoder
{
    /// <summary>Encodes one block. The block uses wide residuals when any residual does not fit in 16 bits.</summary>
    public static (byte[] Encoded, bool Wide) EncodeBlock(in MotionBlockDescriptor block, DriverFrameStore source, IMeshOptimizerBackend meshopt)
    {
        MotionPrediction.ValidateBlock(block, source);

        int frameStride = TemporalLevels.FrameStride(block.TemporalLevel);
        int recordCount = block.FrameCount * block.VertexCount;
        int[] residuals = ArrayPool<int>.Shared.Rent(recordCount * 3);
        try
        {
            bool wide = ComputeResiduals(block, source, frameStride, residuals);
            int stride = wide ? 16 : 8;
            int rawSize = recordCount * stride;
            byte[] raw = ArrayPool<byte>.Shared.Rent(rawSize);
            try
            {
                Span<byte> rawSpan = raw.AsSpan(0, rawSize);
                rawSpan.Clear();
                for (int r = 0; r < recordCount; r++)
                {
                    Span<byte> record = rawSpan.Slice(r * stride, stride);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int residual = residuals[(r * 3) + axis];
                        if (wide)
                            BitConverter.TryWriteBytes(record.Slice(axis * 4, 4), ZigZag.Encode32(residual));
                        else
                            BitConverter.TryWriteBytes(record.Slice(axis * 2, 2), ZigZag.Encode16(residual));
                    }
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(meshopt.EncodeVertexBufferBound(recordCount, stride));
                try
                {
                    int size = meshopt.EncodeVertexBuffer(buffer, rawSpan, recordCount, stride, level: 3, version: 1);
                    return (buffer.AsSpan(0, size).ToArray(), wide);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(residuals);
        }
    }

    private static bool ComputeResiduals(in MotionBlockDescriptor block, DriverFrameStore source, int frameStride, int[] residuals)
    {
        bool wide = false;
        ushort[] positions = source.Positions;
        int index = 0;
        for (int k = 0; k < block.FrameCount; k++)
        {
            int frame = block.FirstFrame + (k * frameStride);
            MotionPrediction.GetNeighbors(block.TemporalLevel, frame, source, out int first, out int second);
            for (int v = 0; v < block.VertexCount; v++)
            {
                int vertex = block.FirstVertex + v;
                int target = ((frame * source.VertexCount) + vertex) * 3;
                for (int axis = 0; axis < 3; axis++)
                {
                    int firstValue = first >= 0 ? positions[(((first * source.VertexCount) + vertex) * 3) + axis] : 0;
                    int secondValue = second >= 0 ? positions[(((second * source.VertexCount) + vertex) * 3) + axis] : 0;
                    int residual = positions[target + axis] - MotionPrediction.Predict(block.TemporalLevel, firstValue, secondValue);
                    if (residual < short.MinValue || residual > short.MaxValue)
                        wide = true;
                    residuals[index++] = residual;
                }
            }
        }
        return wide;
    }
}
