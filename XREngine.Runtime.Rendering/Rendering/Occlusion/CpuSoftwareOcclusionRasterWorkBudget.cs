namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Frame-local safety budget for scalar software rasterization. Limits work rather than
    /// selecting a profitability threshold; rejected triangles write no mask coverage.
    /// </summary>
    internal sealed class CpuSoftwareOcclusionRasterWorkBudget
    {
        internal const int MaxPixelWorkPerFrame = 1_048_576;
        internal const int MaxTileWorkPerFrame = 32_768;

        /// <summary>Bounding-box pixel iterations reserved before rasterization.</summary>
        public int ReservedPixelWork { get; private set; }
        /// <summary>Pixel iterations actually completed by rasterization.</summary>
        public int ExecutedPixelWork { get; private set; }
        /// <summary>Bounding-box tile visits reserved before rasterization.</summary>
        public int ReservedTileWork { get; private set; }
        public int SkippedTriangles { get; private set; }
        public bool IsExhausted { get; private set; }

        public void Reset()
        {
            ReservedPixelWork = 0;
            ExecutedPixelWork = 0;
            ReservedTileWork = 0;
            SkippedTriangles = 0;
            IsExhausted = false;
        }

        /// <summary>
        /// Reserves whole-triangle bounding-box work before any coverage is written. A failed
        /// reservation must leave the triangle absent from the occluder mask.
        /// </summary>
        public bool TryReserve(int pixelWork, int tileWork)
        {
            if (pixelWork <= 0 || tileWork <= 0 ||
                pixelWork > MaxPixelWorkPerFrame - ReservedPixelWork ||
                tileWork > MaxTileWorkPerFrame - ReservedTileWork)
            {
                SkippedTriangles++;
                IsExhausted = true;
                return false;
            }

            ReservedPixelWork += pixelWork;
            ReservedTileWork += tileWork;
            return true;
        }

        public void RecordExecutedPixels(int count) => ExecutedPixelWork += count;
    }
}
