namespace XREngine.Audio
{
    /// <summary>
    /// Abstraction for acoustic scene geometry used by physics-based audio processors.
    /// Bridges the engine's visual scene graph with the processor's internal scene representation.
    /// <para>
    /// Implementations: <c>SteamAudioScene</c> (wraps IPLScene), etc.
    /// </para>
    /// </summary>
    public interface IAudioScene : IDisposable
    {
        /// <summary>Whether the scene has been committed and is ready for simulation.</summary>
        bool IsCommitted { get; }

        /// <summary>
        /// Commit any pending geometry changes so the processor can use them.
        /// Must be called after adding/removing/updating meshes.
        /// </summary>
        void Commit();
    }
}
