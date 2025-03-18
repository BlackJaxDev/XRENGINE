using XREngine.Animation;

namespace XREngine.Components
{
    /// <summary>
    /// Describes a condition and how to transition to a new state.
    /// </summary>
    public class AnimStateTransition
    {
        //[Browsable(false)]
        public AnimState? Owner { get; internal set; }

        public event Action? Started;
        public event Action? Finished;

        /// <summary>
        /// The index of the next state to go to if this transition's condition method returns true.
        /// </summary>
        //[TDropDownIndexSelector("Owner.Owner.States")]
        public int DestinationStateIndex { get; set; }
        /// <summary>
        /// The condition to test if this transition should occur; run every frame.
        /// </summary>
        public Func<bool>? Condition { get; set; }
        /// <summary>
        /// How quickly the current state should blend into the next, in seconds.
        /// </summary>
        public float BlendDuration { get; set; }
        /// <summary>
        /// The interpolation method to use to blend to the next state.
        /// </summary>
        public EAnimBlendType BlendType { get; set; }
        /// <summary>
        /// If <see cref="BlendType"/> == <see cref="EAnimBlendType.Custom"/>, 
        /// uses these keyframes to interpolate between 0.0f and 1.0f.
        /// </summary>
        public PropAnimFloat? CustomBlendFunction { get; set; }
        /// <summary>
        /// If multiple transitions evaluate to true at the same time, this dictates which transition will occur.
        /// </summary>
        public int Priority { get; set; } = 0;

        internal void OnFinished()
        {
            Started?.Invoke();
        }
        internal void OnStarted()
        {
            Finished?.Invoke();
        }
    }
}
