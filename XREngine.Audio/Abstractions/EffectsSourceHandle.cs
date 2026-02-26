namespace XREngine.Audio
{
    /// <summary>
    /// Lightweight handle wrapping an effects-processor-level source tracking entry.
    /// Value type — zero allocation. Used by <see cref="IAudioEffectsProcessor"/>
    /// to track per-source effect state (e.g. Steam Audio IPLSource).
    /// </summary>
    public readonly record struct EffectsSourceHandle(uint Id)
    {
        public static readonly EffectsSourceHandle Invalid = new(0);
        public bool IsValid => Id != 0;
        public override string ToString() => $"EffectsSourceHandle({Id})";
    }
}
