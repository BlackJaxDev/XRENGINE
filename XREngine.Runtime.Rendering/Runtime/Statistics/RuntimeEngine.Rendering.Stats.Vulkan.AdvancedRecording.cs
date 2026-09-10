using System.Threading;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class Vulkan
            {
                private static long _advancedEarlyVisibilityBarrierEmissions;

                /// <summary>
                /// Process-wide native barrier emissions at Advanced early visibility
                /// to indirect generation. Counts recording, including recordings
                /// later discarded; it is not a GPU execution/completion receipt.
                /// </summary>
                public static long AdvancedEarlyVisibilityBarrierEmissions
                    => Interlocked.Read(ref _advancedEarlyVisibilityBarrierEmissions);

                /// <summary>Records that the exact native barrier call was issued.</summary>
                public static void RecordAdvancedEarlyVisibilityBarrierEmission()
                    => Interlocked.Increment(ref _advancedEarlyVisibilityBarrierEmissions);
            }
        }
    }
}
