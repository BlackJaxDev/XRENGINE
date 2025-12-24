namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private sealed class ProfilerThreadCacheEntry
    {
        public int ThreadId;
        public string Name = string.Empty;
        public DateTime LastSeen;
        public bool IsStale;
        public Engine.CodeProfiler.ProfilerThreadSnapshot? Snapshot;
        public float[] Samples = Array.Empty<float>();
    }
}
