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
        /// True when a sample depends only on the requested time and the authored data.
        /// </summary>
        internal virtual bool SupportsHistoryFreeSampling => true;

        /// <summary>
        /// Writes the value for the given second to a store. This method does not change the
        /// current time, the current value, the retained key, or the event state. A time out of
        /// range resolves as a seek does: a looped animation wraps, and other animations clamp.
        /// Typed overrides do not box the value.
        /// </summary>
        /// <param name="second">The time to sample in seconds.</param>
        /// <param name="store">The store that receives the value.</param>
        /// <param name="slot">The store slot. Its type must agree with the value type of this animation.</param>
        public virtual void WriteSampleToStore(float second, AnimationValueStore store, in AnimSlot slot)
            => store.SetValue(slot, GetValueGeneric(ResolveSampleSecond(second)));

        /// <summary>
        /// Create an independent runtime track. Derived runtime tracks can provide a copy contract.
        /// </summary>
        protected internal virtual BasePropAnim CreatePlaybackCopy()
            => throw new NotSupportedException($"Animation track type '{GetType().FullName}' does not support independent playback.");
    }
}
