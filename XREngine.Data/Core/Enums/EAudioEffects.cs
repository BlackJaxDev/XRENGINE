namespace XREngine
{
    /// <summary>
    /// Audio effects processor selection for the cascading settings system and runtime audio layer.
    /// </summary>
    public enum EAudioEffects
    {
        /// <summary>OpenAL EFX-based spatial audio. Requires OpenAL transport.</summary>
        OpenAL_EFX,

        /// <summary>Valve Steam Audio processor (HRTF, occlusion, reflections, pathing).</summary>
        SteamAudio,

        /// <summary>No spatial processing — raw pass-through.</summary>
        Passthrough,
    }
}
