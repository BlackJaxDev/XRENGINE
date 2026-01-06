namespace XREngine.Data.Core
{
    public struct XREventGroup<T>
    {
        public XREvent<T> PreChanged;
        public XREvent<T> PostChanged;
        
        public static XREventGroup<T> operator +(XREventGroup<T> e, (Action<T> pre, Action<T> post) events)
        {
            e.PreChanged += events.pre;
            e.PostChanged += events.post;
            return e;
        }
        public static XREventGroup<T> operator -(XREventGroup<T> e, (Action<T> pre, Action<T> post) events)
        {
            e.PreChanged -= events.pre;
            e.PostChanged -= events.post;
            return e;
        }

        public readonly void InvokePreChanged(T value)
            => PreChanged.Invoke(value);

        public readonly void InvokePostChanged(T value)
            => PostChanged.Invoke(value);
    }
}
