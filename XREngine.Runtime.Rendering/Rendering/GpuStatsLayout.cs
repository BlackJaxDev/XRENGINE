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

        public const uint StatsTriangleCount = 17;

        public const uint MeshletTaskRecordsEmitted = 18;
        public const uint MeshletTaskRecordsFrustumCulled = 19;
        public const uint MeshletTaskRecordsConeCulled = 20;
        public const uint MeshletTaskRecordsHiZCulled = 21;

        public const uint MaterialScatterInputCount = 22;
        public const uint MaterialScatterRejectedDrawId = 23;
        public const uint MaterialScatterRejectedMaterial = 24;
        public const uint MaterialScatterRejectedMesh = 25;
        public const uint MaterialScatterRejectedAtlas = 26;
        public const uint MaterialScatterRejectedBucket = 27;
        public const uint MaterialScatterEmitted = 28;
        public const uint MaterialScatterRejectedBounds = 29;
        public const uint MaterialScatterMaxDrawId = 30;
        public const uint MaterialScatterMetadataLength = 31;
        public const uint MaterialScatterCulledCount = 32;
        public const uint MaterialScatterKeyCount = 33;

        public const uint BvhRawNodeCount = 34;
        public const uint BvhSafeNodeCount = 35;

        public const uint FieldCount = 36;
    }
}
