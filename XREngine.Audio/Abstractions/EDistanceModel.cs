namespace XREngine.Audio
{
    /// <summary>
    /// Distance attenuation model for audio sources.
    /// Maps 1:1 to OpenAL's DistanceModel enum values for zero-overhead conversion.
    /// </summary>
    public enum EDistanceModel
    {
        /// <summary>No distance attenuation.</summary>
        None = 0,
        /// <summary>Inverse distance rolloff (unbounded).</summary>
        InverseDistance = 0xD001,
        /// <summary>Inverse distance rolloff, clamped to [referenceDistance, maxDistance].</summary>
        InverseDistanceClamped = 0xD002,
        /// <summary>Linear falloff from referenceDistance to maxDistance.</summary>
        LinearDistance = 0xD003,
        /// <summary>Linear falloff, clamped to [referenceDistance, maxDistance].</summary>
        LinearDistanceClamped = 0xD004,
        /// <summary>Exponential rolloff (unbounded).</summary>
        ExponentDistance = 0xD005,
        /// <summary>Exponential rolloff, clamped to [referenceDistance, maxDistance].</summary>
        ExponentDistanceClamped = 0xD006,
    }
}
