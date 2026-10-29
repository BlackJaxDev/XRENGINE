using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Gaussian.Baking.Encoding;
using XREngine.Rendering.Models.Gaussian.Cache.Encoding;

namespace XREngine.Rendering.Models.Gaussian.Baking.Build;

// Driver motion sections. Each section is one motion block over the new vertices of one driver level.
public static partial class GaussianCacheBuilder
{
    /// <summary>
    /// Appends the motion of the new vertices of a driver level at one temporal level. A split falls between two
    /// frames of the temporal level, and the parts stay in frame order, so each part predicts only from frames that
    /// earlier sections hold.
    /// </summary>
    private static void AppendMotion(ChunkPlanner planner, DriverFrameStore frames, int level, byte temporalLevel, IMeshOptimizerBackend meshopt)
    {
        int frameCount = TemporalLevels.FramesInLevel(temporalLevel, frames.FrameCount);
        if (frames.GetLevelVertexCount(level) == 0 || frameCount == 0)
            return;

        planner.AppendSplit(
            frameCount,
            (first, count) => EncodeMotion(frames, level, temporalLevel, first, count, meshopt),
            $"driver level {level} motion at T{temporalLevel}");
    }

    /// <summary>Encodes the frames <c>[first, first + count)</c> of a temporal level, counted in that level.</summary>
    private static GaussianCacheSection EncodeMotion(DriverFrameStore frames, int level, byte temporalLevel, int first, int count, IMeshOptimizerBackend meshopt)
    {
        int firstVertex = frames.GetLevelFirstVertex(level);
        int vertexCount = frames.GetLevelVertexCount(level);
        int firstFrame = TemporalLevels.FirstFrame(temporalLevel) + (first * TemporalLevels.FrameStride(temporalLevel));
        MotionBlockDescriptor block = new((byte)level, temporalLevel, firstVertex, vertexCount, firstFrame, count, false);
        (byte[] encoded, bool wide) = MotionEncoder.EncodeBlock(block, frames, meshopt);
        byte flags = wide ? GaussianCacheSection.WideResidualsFlag : (byte)0;
        return new GaussianCacheSection(
            GaussianCacheSectionKind.DriverMotion, (byte)level, temporalLevel, flags, firstVertex, vertexCount, firstFrame, count, encoded);
    }
}
