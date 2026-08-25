namespace XREngine.Core.Files
{
    public class EventArgs<T>(T value) : EventArgs
    {
        public T Value
        {
            get;
            private set;
        } = value;
    }
}
