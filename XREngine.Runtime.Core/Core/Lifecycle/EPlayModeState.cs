namespace XREngine
{
    /// <summary>
    /// Represents the current state of play mode.
    /// </summary>
    public enum EPlayModeState
    {
        /// <summary>
        /// Normal editor mode - no simulation running.
        /// Physics is disabled, gameplay code is dormant.
        /// </summary>
        Edit,

        /// <summary>
        /// Transitioning into play mode.
        /// Saving state, loading assemblies, initializing worlds.
        /// </summary>
        EnteringPlay,

        /// <summary>
        /// Full simulation running.
        /// Physics active, gameplay code executing, GameMode running.
        /// </summary>
        Play,

        /// <summary>
        /// Transitioning out of play mode.
        /// Unloading assemblies, restoring state, cleaning up.
        /// </summary>
        ExitingPlay,

        /// <summary>
        /// Simulation is paused but still in play context.
        /// Physics paused, gameplay paused, but state preserved.
        /// </summary>
        Paused,
    }
}
