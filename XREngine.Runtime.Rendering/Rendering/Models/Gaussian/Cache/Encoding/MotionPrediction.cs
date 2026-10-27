namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Holds the prediction rules that the motion encoder and the motion decoder share.</summary>
public static class MotionPrediction
{
    /// <summary>Returns the neighbour frames of a frame. A value of -1 means there is no neighbour.</summary>
    public static void GetNeighbors(byte temporalLevel, int frame, DriverFrameStore store, out int first, out int second)
    {
        if (temporalLevel == 0)
        {
            first = frame >= 8 ? frame - 8 : -1;
            second = -1;
            return;
        }

        int step = TemporalLevels.Step(temporalLevel);
        first = frame - step;
        second = TemporalLevels.NextNeighbor(frame, step, store.FrameCount, store.LoopMode);
    }

    /// <summary>Returns the prediction of one axis value from the neighbour values. Values are 0 to 65535.</summary>
    public static int Predict(byte temporalLevel, int firstValue, int secondValue)
        => temporalLevel == 0 ? firstValue : (firstValue + secondValue + 1) >> 1;

    /// <summary>Checks that a block fits the store and that its frames belong to its temporal level.</summary>
    /// <exception cref="GaussianCacheFormatException">The block is not valid.</exception>
    public static void ValidateBlock(in MotionBlockDescriptor block, DriverFrameStore store)
    {
        if (block.TemporalLevel >= TemporalLevels.Count || block.DriverLevel >= store.DriverLevelCount)
            throw Malformed("A motion block names a level that does not exist.");
        if (block.FirstVertex < 0 || block.VertexCount <= 0 || block.FirstVertex > store.VertexCount - block.VertexCount)
            throw Malformed("A motion block vertex range is outside the store.");
        if (block.FirstFrame < 0 || block.FrameCount <= 0 || block.FirstFrame >= store.FrameCount)
            throw Malformed("A motion block frame range is outside the store.");
        if (TemporalLevels.Of(block.FirstFrame) != block.TemporalLevel)
            throw Malformed("A motion block first frame is not in its temporal level.");

        long lastFrame = block.FirstFrame + ((long)(block.FrameCount - 1) * TemporalLevels.FrameStride(block.TemporalLevel));
        if (lastFrame >= store.FrameCount)
            throw Malformed("A motion block has more frames than the store.");
    }

    private static GaussianCacheFormatException Malformed(string message)
        => new(GaussianCacheFormatException.MalformedCode, message);
}
