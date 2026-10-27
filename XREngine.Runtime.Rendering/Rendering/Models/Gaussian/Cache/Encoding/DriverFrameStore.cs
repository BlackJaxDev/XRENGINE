using System;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Holds quantized driver vertex positions for all frames and tracks which parts are decoded.</summary>
public sealed class DriverFrameStore
{
    private readonly byte[] _decodedCells;
    private readonly int[] _levelFirstVertex;
    private readonly int[] _levelEndVertex;
    private readonly bool[] _levelRangeFixed;
    private readonly long[,] _levelTemporalCells;

    public DriverFrameStore(int vertexCount, int frameCount, int driverLevelCount, GaussianCacheLoopMode loopMode = GaussianCacheLoopMode.Loop)
    {
        LoopMode = loopMode;
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(driverLevelCount);
        long cells = (long)vertexCount * frameCount;
        if (cells * 3 > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(vertexCount), "The store is too large.");

        VertexCount = vertexCount;
        FrameCount = frameCount;
        DriverLevelCount = driverLevelCount;
        Positions = new ushort[cells * 3];
        _decodedCells = new byte[cells];
        DecodedTemporalLevel = new sbyte[driverLevelCount];
        Array.Fill(DecodedTemporalLevel, (sbyte)-1);
        _levelFirstVertex = new int[driverLevelCount];
        _levelEndVertex = new int[driverLevelCount];
        _levelRangeFixed = new bool[driverLevelCount];
        _levelTemporalCells = new long[driverLevelCount, TemporalLevels.Count];
        Array.Fill(_levelFirstVertex, int.MaxValue);
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

    /// <summary>Sets the vertex range of a driver level. Without it, the range is the union of the decoded blocks.</summary>
    public void SetDriverLevelRange(int driverLevel, int firstVertex, int vertexCount)
    {
        _levelFirstVertex[driverLevel] = firstVertex;
        _levelEndVertex[driverLevel] = firstVertex + vertexCount;
        _levelRangeFixed[driverLevel] = true;
        UpdateDecodedLevel(driverLevel);
    }

    /// <summary>Returns true when every vertex of the range is decoded in the frame.</summary>
    public bool IsDecoded(int frame, int firstVertex, int vertexCount)
        => _decodedCells.AsSpan((frame * VertexCount) + firstVertex, vertexCount).IndexOf((byte)0) < 0;

    internal void MarkDecoded(int driverLevel, byte temporalLevel, int frame, int firstVertex, int vertexCount)
    {
        _decodedCells.AsSpan((frame * VertexCount) + firstVertex, vertexCount).Fill(1);
        _levelTemporalCells[driverLevel, temporalLevel] += vertexCount;
    }

    internal void CompleteBlock(int driverLevel, int firstVertex, int vertexCount)
    {
        if (!_levelRangeFixed[driverLevel])
        {
            _levelFirstVertex[driverLevel] = Math.Min(_levelFirstVertex[driverLevel], firstVertex);
            _levelEndVertex[driverLevel] = Math.Max(_levelEndVertex[driverLevel], firstVertex + vertexCount);
        }
        UpdateDecodedLevel(driverLevel);
    }

    private void UpdateDecodedLevel(int driverLevel)
    {
        long width = (long)_levelEndVertex[driverLevel] - _levelFirstVertex[driverLevel];
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
