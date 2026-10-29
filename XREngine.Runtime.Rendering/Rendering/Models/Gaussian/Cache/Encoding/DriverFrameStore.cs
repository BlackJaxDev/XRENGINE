using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>
/// Holds quantized driver vertex positions for all frames and tracks which parts are decoded.
/// The store is the single source of truth for the loop mode. The encoder and the decoder must use stores with the same loop mode.
/// </summary>
public sealed class DriverFrameStore
{
    private readonly byte[] _decodedCells;
    private readonly int[] _levelFirstVertex;
    private readonly int[] _levelVertexCount;
    private readonly long[,] _levelTemporalCells;

    /// <param name="cumulativeDriverVertexCounts">The cumulative driver vertex count of each driver level, as in the cache header.</param>
    /// <param name="frameCount">The number of frames. It must be 1 or a positive multiple of 8.</param>
    /// <param name="loopMode">Tells how the later prediction neighbour of the last frames is chosen.</param>
    /// <exception cref="ArgumentException">A count is not valid.</exception>
    public DriverFrameStore(IReadOnlyList<int> cumulativeDriverVertexCounts, int frameCount, GaussianCacheLoopMode loopMode)
    {
        ArgumentNullException.ThrowIfNull(cumulativeDriverVertexCounts);
        if (frameCount != 1 && (frameCount <= 0 || (frameCount & 7) != 0))
            throw new ArgumentException("The frame count must be 1 or a positive multiple of 8.", nameof(frameCount));

        int levels = cumulativeDriverVertexCounts.Count;
        _levelFirstVertex = new int[levels];
        _levelVertexCount = new int[levels];
        int previous = 0;
        for (int level = 0; level < levels; level++)
        {
            int cumulative = cumulativeDriverVertexCounts[level];
            if (cumulative < previous)
                throw new ArgumentException("The cumulative vertex counts must not decrease and must not be negative.", nameof(cumulativeDriverVertexCounts));
            _levelFirstVertex[level] = previous;
            _levelVertexCount[level] = cumulative - previous;
            previous = cumulative;
        }

        long cells = (long)previous * frameCount;
        if (cells * 3 > int.MaxValue)
            throw new ArgumentException("The store is too large.", nameof(cumulativeDriverVertexCounts));

        LoopMode = loopMode;
        VertexCount = previous;
        FrameCount = frameCount;
        DriverLevelCount = levels;
        Positions = new ushort[cells * 3];
        _decodedCells = new byte[cells];
        DecodedTemporalLevel = new sbyte[levels];
        Array.Fill(DecodedTemporalLevel, (sbyte)-1);
        _levelTemporalCells = new long[levels, TemporalLevels.Count];
    }

    public int VertexCount { get; }
    public int FrameCount { get; }
    public int DriverLevelCount { get; }

    /// <summary>Tells how the later prediction neighbour of the last frames is chosen.</summary>
    public GaussianCacheLoopMode LoopMode { get; }

    /// <summary>Positions on the 16-bit grid, indexed ((frame * VertexCount) + vertex) * 3.</summary>
    public ushort[] Positions { get; }

    /// <summary>Holds, per driver level, the highest temporal level for which all frames of the level vertex range are decoded. It is -1 when none are.</summary>
    public sbyte[] DecodedTemporalLevel { get; }

    /// <summary>Returns the first vertex of a driver level.</summary>
    public int GetLevelFirstVertex(int driverLevel) => _levelFirstVertex[driverLevel];

    /// <summary>Returns the number of vertices of a driver level.</summary>
    public int GetLevelVertexCount(int driverLevel) => _levelVertexCount[driverLevel];

    /// <summary>Returns true when every vertex of the range is decoded in the frame.</summary>
    public bool IsDecoded(int frame, int firstVertex, int vertexCount)
        => _decodedCells.AsSpan((frame * VertexCount) + firstVertex, vertexCount).IndexOf((byte)0) < 0;

    internal void MarkDecoded(int driverLevel, byte temporalLevel, int frame, int firstVertex, int vertexCount)
    {
        Span<byte> cells = _decodedCells.AsSpan((frame * VertexCount) + firstVertex, vertexCount);
        _levelTemporalCells[driverLevel, temporalLevel] += cells.Count((byte)0);
        cells.Fill(1);
    }

    internal void CompleteBlock(int driverLevel)
    {
        long width = _levelVertexCount[driverLevel];
        sbyte reached = -1;
        if (width > 0)
        {
            for (byte t = 0; t < TemporalLevels.Count; t++)
            {
                long expected = width * TemporalLevels.FramesInLevel(t, FrameCount);
                if (_levelTemporalCells[driverLevel, t] < expected)
                    break;
                reached = (sbyte)t;
            }
        }
        DecodedTemporalLevel[driverLevel] = reached;
    }
}
