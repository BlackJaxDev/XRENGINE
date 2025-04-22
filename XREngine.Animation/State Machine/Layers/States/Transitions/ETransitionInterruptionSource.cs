namespace XREngine.Animation
{
    public enum ETransitionInterruptionSource
    {
        /// <summary>
        /// Neither the current nor next state can interrupt the transition.
        /// </summary>
        Neither,
        /// <summary>
        /// The current state can interrupt the transition.
        /// </summary>
        Current,
        /// <summary>
        /// The next state can interrupt the transition.
        /// </summary>
        Next,
        /// <summary>
        /// The current state can interrupt the transition, and if no transitions pass, the next state can interrupt the transition.
        /// </summary>
        CurrentThenNext,
        /// <summary>
        /// The next state can interrupt the transition, and if no transitions pass, the current state can interrupt the transition.
        /// </summary>
        NextThenCurrent,
    }
}
