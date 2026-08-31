namespace XREngine.Rendering.Occlusion
{
    /// <summary>Frame-local safety budget for all-or-nothing software AABB visibility tests.</summary>
    internal sealed class CpuSoftwareOcclusionAabbTestWorkBudget
    {
        // Safety limits only; they do not express a profitability crossover.
        private const int MaxTestsPerFrame = 8_192;
        private const int MaxTileTestsPerFrame = 65_536;

        public int TestCount { get; private set; }
        public int TileTests { get; private set; }
        public int BypassedTests { get; private set; }
        public bool IsExhausted { get; private set; }

        public void Reset()
        {
            TestCount = 0;
            TileTests = 0;
            BypassedTests = 0;
            IsExhausted = false;
        }

        /// <summary>Reserves a query token before projection work begins.</summary>
        public bool TryReserveQuery()
        {
            if (TestCount >= MaxTestsPerFrame)
            {
                BypassedTests++;
                IsExhausted = true;
                return false;
            }

            TestCount++;
            return true;
        }

        /// <summary>Reserves all projected tile reads before any mask tile is inspected.</summary>
        public bool TryReserveTileWork(int tileTests)
        {
            if (tileTests < 0 || tileTests > MaxTileTestsPerFrame - TileTests)
            {
                BypassedTests++;
                IsExhausted = true;
                return false;
            }

            TileTests += tileTests;
            return true;
        }
    }
}
