namespace XREngine.Rendering
{
    /// <summary>
    /// Defines the layout of the GPU stats buffer shared across culling/indirect/BVH stages.
    /// </summary>
    public static class GpuStatsLayout
    {
        public const uint StatsInputCount = 0;
        public const uint StatsCulledCount = 1;
        public const uint StatsDrawCount = 2;
        public const uint StatsRejectedFrustum = 3;
        public const uint StatsRejectedDistance = 4;

        public const uint BvhBuildCount = 5;
        public const uint BvhRefitCount = 6;
        public const uint BvhCullCount = 7;
        public const uint BvhRayCount = 8;

        public const uint BvhBuildTimeLo = 9;
        public const uint BvhBuildTimeHi = 10;
        public const uint BvhRefitTimeLo = 11;
        public const uint BvhRefitTimeHi = 12;
        public const uint BvhCullTimeLo = 13;
        public const uint BvhCullTimeHi = 14;
        public const uint BvhRayTimeLo = 15;
        public const uint BvhRayTimeHi = 16;

        public const uint FieldCount = 17;
    }
}
