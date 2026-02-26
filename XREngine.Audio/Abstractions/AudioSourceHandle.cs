namespace XREngine.Audio
{
    /// <summary>
    /// Lightweight handle wrapping a transport-level audio source.
    /// Value type — zero allocation, comparable, and safe to store in collections.
    /// The underlying uint maps to the native handle (e.g. OpenAL source ID).
    /// </summary>
    public readonly record struct AudioSourceHandle(uint Id)
    {
        public static readonly AudioSourceHandle Invalid = new(0);
        public bool IsValid => Id != 0;
        public override string ToString() => $"AudioSourceHandle({Id})";
    }
}
