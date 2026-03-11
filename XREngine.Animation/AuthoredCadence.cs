using MemoryPack;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial struct AuthoredCadence
    {
        public int FrameCount { get; set; }
        public int FramesPerSecond { get; set; }

        [MemoryPackIgnore]
        public readonly bool IsValid => FrameCount > 0 && FramesPerSecond > 0;

        public AuthoredCadence(int frameCount, int framesPerSecond)
        {
            FrameCount = Math.Max(0, frameCount);
            FramesPerSecond = Math.Max(0, framesPerSecond);
        }

        public readonly AuthoredCadence Sanitize()
            => new(FrameCount, FramesPerSecond);

        public readonly float GetLengthSeconds()
            => !IsValid ? 0.0f : FrameCount / (float)FramesPerSecond;

        public readonly long GetLengthStopwatchTicks(long stopwatchFrequency)
            => !IsValid || stopwatchFrequency <= 0L
                ? 0L
                : Math.Max(0L, (long)Math.Round(FrameCount * (double)stopwatchFrequency / FramesPerSecond));

        public readonly int GetFrameFloor(long clipRelativeTicks, long stopwatchFrequency)
        {
            if (!IsValid || stopwatchFrequency <= 0L)
                return 0;

            long clipLengthTicks = GetLengthStopwatchTicks(stopwatchFrequency);
            if (clipRelativeTicks <= 0L)
                return 0;
            if (clipRelativeTicks >= clipLengthTicks)
                return FrameCount - 1;

            return Math.Clamp(
                (int)(clipRelativeTicks * (double)FramesPerSecond / stopwatchFrequency),
                0,
                FrameCount - 1);
        }

        public readonly float GetFrameFraction(long clipRelativeTicks, long stopwatchFrequency)
        {
            if (!IsValid || stopwatchFrequency <= 0L)
                return 0.0f;

            long clipLengthTicks = GetLengthStopwatchTicks(stopwatchFrequency);
            if (clipRelativeTicks <= 0L || clipRelativeTicks >= clipLengthTicks)
                return 0.0f;

            double authoredFrame = clipRelativeTicks * (double)FramesPerSecond / stopwatchFrequency;
            return (float)(authoredFrame - Math.Floor(authoredFrame));
        }

        public readonly float GetSecondsForFrame(int frameIndex)
            => GetSecondsForFrame(frameIndex, FramesPerSecond);

        public static float GetSecondsForFrame(int frameIndex, int framesPerSecond)
            => framesPerSecond <= 0
                ? 0.0f
                : Math.Max(0, frameIndex) / (float)framesPerSecond;

        public static bool TryNormalizeFramesPerSecond(float framesPerSecond, out int authoredFramesPerSecond)
        {
            authoredFramesPerSecond = 0;
            if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0.0f)
                return false;

            float roundedFramesPerSecond = MathF.Round(framesPerSecond);
            if (MathF.Abs(framesPerSecond - roundedFramesPerSecond) > 0.0001f)
                return false;

            authoredFramesPerSecond = Math.Max(0, (int)roundedFramesPerSecond);
            return authoredFramesPerSecond > 0;
        }
    }
}