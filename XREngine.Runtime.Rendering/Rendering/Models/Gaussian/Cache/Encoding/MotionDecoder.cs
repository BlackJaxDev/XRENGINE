using System;
using System.Buffers;
using System.IO;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Rebuilds driver vertex positions from encoded motion residuals.</summary>
public static class MotionDecoder
{
    /// <summary>Decodes one block into the store.</summary>
    /// <exception cref="GaussianCacheFormatException">The block is not valid, the data is damaged, or a frame that the block predicts from is not decoded yet.</exception>
    public static void DecodeBlock(in MotionBlockDescriptor block, ReadOnlySpan<byte> encoded, DriverFrameStore store, IMeshOptimizerBackend meshopt)
    {
        MotionPrediction.ValidateBlock(block, store);

        int stride = block.WideResiduals ? 16 : 8;
        long totalVertices = (long)block.FrameCount * block.VertexCount;
        long rawLength = totalVertices * stride;
        if (rawLength > int.MaxValue)
            throw new GaussianCacheFormatException(GaussianCacheFormatException.MalformedCode, "A motion block is too large.");

        int rawSize = (int)rawLength;
        byte[] scratch = ArrayPool<byte>.Shared.Rent(rawSize);
        try
        {
            try
            {
                meshopt.DecodeVertexBuffer(scratch.AsSpan(0, rawSize), (int)totalVertices, stride, encoded);
            }
            catch (InvalidDataException exception)
            {
                throw new GaussianCacheFormatException(GaussianCacheFormatException.MalformedCode, "A motion block is damaged: " + exception.Message);
            }

            ApplyResiduals(block, scratch, store);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void ApplyResiduals(in MotionBlockDescriptor block, byte[] raw, DriverFrameStore store)
    {
        int stride = block.WideResiduals ? 16 : 8;
        int frameStride = TemporalLevels.FrameStride(block.TemporalLevel);
        ushort[] positions = store.Positions;

        for (int k = 0; k < block.FrameCount; k++)
        {
            int frame = block.FirstFrame + (k * frameStride);
            MotionPrediction.GetNeighbors(block.TemporalLevel, frame, store, out int first, out int second);
            RequireDecoded(first, block, store, frame);
            RequireDecoded(second, block, store, frame);

            ReadOnlySpan<byte> frameBytes = raw.AsSpan(k * block.VertexCount * stride, block.VertexCount * stride);
            for (int v = 0; v < block.VertexCount; v++)
            {
                ReadOnlySpan<byte> record = frameBytes.Slice(v * stride, stride);
                int vertex = block.FirstVertex + v;
                int target = ((frame * store.VertexCount) + vertex) * 3;
                for (int axis = 0; axis < 3; axis++)
                {
                    int residual = block.WideResiduals
                        ? ZigZag.Decode32(BitConverter.ToUInt32(record.Slice(axis * 4, 4)))
                        : ZigZag.Decode16(BitConverter.ToUInt16(record.Slice(axis * 2, 2)));
                    int firstValue = first >= 0 ? positions[(((first * store.VertexCount) + vertex) * 3) + axis] : 0;
                    int secondValue = second >= 0 ? positions[(((second * store.VertexCount) + vertex) * 3) + axis] : 0;
                    int value = MotionPrediction.Predict(block.TemporalLevel, firstValue, secondValue) + residual;
                    if (value < 0 || value > ushort.MaxValue)
                        throw new GaussianCacheFormatException(GaussianCacheFormatException.MalformedCode, "A motion residual leaves the position grid.");
                    positions[target + axis] = (ushort)value;
                }
            }

            store.MarkDecoded(block.DriverLevel, block.TemporalLevel, frame, block.FirstVertex, block.VertexCount);
        }

        store.CompleteBlock(block.DriverLevel, block.FirstVertex, block.VertexCount);
    }

    private static void RequireDecoded(int neighbor, in MotionBlockDescriptor block, DriverFrameStore store, int frame)
    {
        if (neighbor < 0)
            return;
        if (neighbor >= store.FrameCount || !store.IsDecoded(neighbor, block.FirstVertex, block.VertexCount))
            throw new GaussianCacheFormatException(
                GaussianCacheFormatException.ChunkOutOfOrderCode,
                $"Frame {frame} predicts from frame {neighbor}, which is not decoded yet.");
    }
}
