namespace XREngine
{
    public static partial class RuntimeEngine
    {
        public static partial class Rendering
        {
            public static partial class Stats
            {
                /// <summary>Unpaced VR CPU frame work statistics over a window of recent frames.</summary>
                /// <param name="SampleCount">The number of frames in the window.</param>
                /// <param name="AverageMs">The mean frame work time, in milliseconds.</param>
                /// <param name="MedianMs">The median frame work time, in milliseconds.</param>
                /// <param name="P95Ms">The 95th percentile frame work time, in milliseconds.</param>
                /// <param name="WorstMs">The largest frame work time, in milliseconds.</param>
                public readonly record struct VrHeadroomWindow(
                    int SampleCount,
                    double AverageMs,
                    double MedianMs,
                    double P95Ms,
                    double WorstMs);
            }
        }
    }
}
