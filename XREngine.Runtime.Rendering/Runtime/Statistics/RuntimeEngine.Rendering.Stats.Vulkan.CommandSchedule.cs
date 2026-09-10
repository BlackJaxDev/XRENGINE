using XREngine.Data.Rendering;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class Vulkan
            {
                private static long _commandChainSchedulingAttempts;
                private static int _commandChainScheduleDecision;

                /// <summary>Process cumulative scheduling decisions, including attempts whose frame is discarded.</summary>
                public static long CommandChainSchedulingAttempts => Interlocked.Read(ref _commandChainSchedulingAttempts);
                /// <summary>Latest process-wide scheduling decision; this is not a GPU completion receipt.</summary>
                public static EVulkanCommandChainScheduleDecision CommandChainScheduleDecision
                    => (EVulkanCommandChainScheduleDecision)Volatile.Read(ref _commandChainScheduleDecision);

                public static void RecordCommandChainScheduleDecision(EVulkanCommandChainScheduleDecision decision)
                {
                    Volatile.Write(ref _commandChainScheduleDecision, (int)decision);
                    Interlocked.Increment(ref _commandChainSchedulingAttempts);
                }
            }
        }
    }
}
