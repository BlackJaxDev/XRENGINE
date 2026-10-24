namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        public static partial class Stats
        {
            public static partial class ResourceChurn
            {
                /// <summary>Event counts from the same published frame as copied churn rows.</summary>
                public readonly record struct SnapshotCounts(
                    int Created,
                    int Recreated,
                    int Resized,
                    int Destroyed);
            }
        }
    }
}
