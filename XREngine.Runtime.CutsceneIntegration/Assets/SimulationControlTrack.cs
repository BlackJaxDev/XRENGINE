namespace XREngine.Cutscenes;

/// <summary>
/// A track that controls the simulations on its bound node: the physics chains and the particle emitters.
/// It has no clips. Its policy applies for the full cutscene at each discontinuity: a jump, a scrub step, a camera cut,
/// or a stop. A hard resync to the audio inside a shot does not reset the simulations. During a scrub, the simulations hold the reset pose. They start again when
/// the transport plays at a rate near one or when the scrub ends.
/// </summary>
public class SimulationControlTrack : CutsceneTrack
{
    /// <summary>The default preroll duration of <see cref="ECutsceneSimulationPolicy.WarmUp"/>, in seconds.</summary>
    public const double DefaultWarmUpSeconds = 1.0;

    /// <summary>The default wall time that a warm-up can use in each frame, in milliseconds.</summary>
    public const double DefaultWarmUpBudgetMilliseconds = 2.0;

    private ECutsceneSimulationPolicy _policy = ECutsceneSimulationPolicy.SettleFromReset;
    private double _warmUpSeconds = DefaultWarmUpSeconds;
    private double _warmUpBudgetMilliseconds = DefaultWarmUpBudgetMilliseconds;
    private int _seed;

    /// <summary>What the simulations of the bound node do after a discontinuity.</summary>
    public ECutsceneSimulationPolicy Policy
    {
        get => _policy;
        set => SetField(ref _policy, value);
    }

    /// <summary>The simulation time that <see cref="ECutsceneSimulationPolicy.WarmUp"/> runs after the reset, in seconds.</summary>
    public double WarmUpSeconds
    {
        get => _warmUpSeconds;
        set => SetField(ref _warmUpSeconds, value);
    }

    /// <summary>
    /// The wall time that <see cref="ECutsceneSimulationPolicy.WarmUp"/> can use in each frame, in milliseconds.
    /// The steps that do not fit continue in the next frames.
    /// </summary>
    public double WarmUpBudgetMilliseconds
    {
        get => _warmUpBudgetMilliseconds;
        set => SetField(ref _warmUpBudgetMilliseconds, value);
    }

    /// <summary>The random seed that a reset or a warm-up gives to a particle emitter, so each run gives the same particles.</summary>
    public int Seed
    {
        get => _seed;
        set => SetField(ref _seed, value);
    }
}
