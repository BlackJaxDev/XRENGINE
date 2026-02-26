namespace XREngine.Audio
{
    /// <summary>
    /// Global audio subsystem feature flags and configuration.
    /// Used to gate architecture changes during the transport/effects split migration.
    /// </summary>
    public static class AudioSettings
    {
        /// <summary>
        /// When <c>true</c>, the audio subsystem uses the new transport/effects split
        /// architecture (IAudioTransport + IAudioEffectsProcessor composed in ListenerContext).
        /// When <c>false</c> (default), the legacy monolithic OpenAL code path is used.
        /// <para>
        /// This flag exists as a safety net during the migration from the monolithic OpenAL
        /// architecture to the transport/effects split. It allows instant rollback to the
        /// proven OpenAL path if regressions are detected.
        /// </para>
        /// <para>
        /// The flag will be removed once Phase 3 (Steam Audio processor) is stable and the
        /// legacy path is deleted.
        /// </para>
        /// </summary>
        public static bool AudioArchitectureV2 { get; set; } = false;
    }
}
