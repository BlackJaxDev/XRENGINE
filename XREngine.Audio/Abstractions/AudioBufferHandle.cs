namespace XREngine.Audio
{
    /// <summary>
    /// Lightweight handle wrapping a transport-level audio buffer.
    /// Value type — zero allocation, comparable, and safe to store in collections.
    /// The underlying uint maps to the native handle (e.g. OpenAL buffer ID).
    /// </summary>
    public readonly record struct AudioBufferHandle(uint Id)
    {
        public static readonly AudioBufferHandle Invalid = new(0);
        public bool IsValid => Id != 0;
        public override string ToString() => $"AudioBufferHandle({Id})";
    }
}
