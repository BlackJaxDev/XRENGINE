namespace XREngine.Animation
{
    /// <summary>
    /// Base class for animations that animate properties such as Vector3, bool and float.
    /// </summary>
    public abstract class BasePropAnim(float lengthInSeconds, bool looped) : BaseAnimation(lengthInSeconds, looped)
    {
        public const string PropAnimCategory = "Property Animation";

        /// <summary>
        /// Retrieves the value for the animation's current time.
        /// Used by the internal animation implementation to set property/field values and call methods,
        /// so must be overridden.
        /// </summary>
        public abstract object? GetCurrentValueGeneric();
        /// <summary>
        /// Retrieves the value for the given second.
        /// Used by the internal animation implementation to set property/field values and call methods,
        /// so must be overridden.
        /// </summary>
        public abstract object? GetValueGeneric(float second);

        /// <summary>
        /// Create an independent runtime track. Derived runtime tracks can provide a copy contract.
        /// </summary>
        protected internal virtual BasePropAnim CreatePlaybackCopy()
            => throw new NotSupportedException($"Animation track type '{GetType().FullName}' does not support independent playback.");
    }
}
