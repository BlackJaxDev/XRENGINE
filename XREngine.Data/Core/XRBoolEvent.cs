using System.Collections;
using System.Collections.Concurrent;

namespace XREngine.Data.Core
{
    public class XRBoolEvent<T>() : IEnumerable<Func<T, bool>>
    {
        private List<Func<T, bool>>? _actions = [];
        private List<Func<T, bool>> Actions => _actions ??= [];

        private ConcurrentQueue<Func<T, bool>>? _pendingAdds;
        private ConcurrentQueue<Func<T, bool>> PendingAdds => _pendingAdds ??= [];

        private ConcurrentQueue<Func<T, bool>>? _pendingRemoves;
        private ConcurrentQueue<Func<T, bool>> PendingRemoves => _pendingRemoves ??= [];

        public int Count => Actions.Count;

        public void AddListener(Func<T, bool> action)
        {
            PendingAdds.Enqueue(action);
        }

        public void RemoveListener(Func<T, bool> action)
        {
            PendingRemoves.Enqueue(action);
        }

        public bool Invoke(T item)
        {
            ConsumeQueues();
            return Actions.All(x => x.Invoke(item));
        }

        private void ConsumeQueues()
        {
            while (PendingAdds.TryDequeue(out Func<T, bool>? add))
                Actions.Add(add);
            while (PendingRemoves.TryDequeue(out Func<T, bool>? remove))
                Actions.Remove(remove);
        }

        public IEnumerator<Func<T, bool>> GetEnumerator()
            => ((IEnumerable<Func<T, bool>>)Actions).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable)Actions).GetEnumerator();

        public static XRBoolEvent<T>? operator +(XRBoolEvent<T>? e, Func<T, bool> a)
        {
            e ??= new();
            e.AddListener(a);
            return e;
        }
        public static XRBoolEvent<T>? operator -(XRBoolEvent<T>? e, Func<T, bool> a)
        {
            if (e is null)
                return null;
            e.RemoveListener(a);
            if (e.Count == 0)
                return null;
            return e;
        }
    }
}
