using XREngine.Data.Geometry;

namespace XREngine.Data.Trees
{
    public interface IRenderTree
    {
        public static Func<string, IDisposable>? ProfilingHook = null;
        
        /// <summary>
        /// Called when octree commands are executed.
        /// Parameters: (addCount, moveCount, removeCount, skippedCount)
        /// </summary>
        public static Action<int, int, int, int>? OctreeStatsHook = null;

        void Remake();
        void Swap();
        void Add(ITreeItem item);
        void Remove(ITreeItem item);
        void AddRange(IEnumerable<ITreeItem> renderedObjects);
        void RemoveRange(IEnumerable<ITreeItem> renderedObjects);
    }
    public interface IRenderTree<T> : IRenderTree where T : class, ITreeItem
    {
        void Add(T value);
        void AddRange(IEnumerable<T> value);
        void Remove(T value);
        void RemoveRange(IEnumerable<T> value);
        void CollectAll(Action<T> action);
    }
}
