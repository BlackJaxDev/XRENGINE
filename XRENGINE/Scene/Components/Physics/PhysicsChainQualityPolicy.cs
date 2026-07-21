namespace XREngine.Components;

/// <summary>
/// Explicit runtime policy resolved from a physics-chain quality tier.
/// </summary>
public readonly record struct PhysicsChainQualityPolicy(
    PhysicsChainQualityTier Tier,
    float SimulationRateHz,
    int SolverSubstepCount,
    int ConstraintIterationCount,
    int MaximumCatchUpSteps,
    bool CollisionEnabled,
    PhysicsChainOutputCadence PaletteCadence,
    PhysicsChainOutputCadence BoundsCadence,
    PhysicsChainOutputCadence TransformMirrorCadence)
{
    /// <summary>Whether this policy advances physical simulation.</summary>
    public bool SimulationEnabled => SimulationRateHz > 0.0f && SolverSubstepCount > 0;

    /// <summary>
    /// Resolves a named tier without hiding the component's authored strict rate.
    /// </summary>
    public static PhysicsChainQualityPolicy Resolve(PhysicsChainQualityTier tier, float authoredRateHz)
    {
        float safeAuthoredRate = MathF.Max(authoredRateHz, 0.0f);
        float simulationRate = tier switch
        {
            PhysicsChainQualityTier.Hz30 => 30.0f,
            PhysicsChainQualityTier.Hz15 => 15.0f,
            PhysicsChainQualityTier.Hz7_5 => 7.5f,
            PhysicsChainQualityTier.Sleep => 0.0f,
            _ => safeAuthoredRate,
        };

        bool sleeping = tier == PhysicsChainQualityTier.Sleep;
        return new PhysicsChainQualityPolicy(
            tier,
            simulationRate,
            SolverSubstepCount: sleeping ? 0 : 1,
            ConstraintIterationCount: sleeping ? 0 : 1,
            MaximumCatchUpSteps: sleeping ? 0 : 3,
            CollisionEnabled: !sleeping,
            PaletteCadence: sleeping ? PhysicsChainOutputCadence.Hold : PhysicsChainOutputCadence.EverySimulationStep,
            BoundsCadence: sleeping ? PhysicsChainOutputCadence.Hold : PhysicsChainOutputCadence.EverySimulationStep,
            TransformMirrorCadence: sleeping ? PhysicsChainOutputCadence.Hold : PhysicsChainOutputCadence.EverySimulationStep);
    }

    /// <summary>
    /// Applies independent simulation, collision, and output publication controls.
    /// </summary>
    public PhysicsChainQualityPolicy WithOverrides(
        PhysicsChainPolicyControl simulation,
        PhysicsChainPolicyControl collision,
        PhysicsChainOutputControl palette,
        PhysicsChainOutputControl bounds,
        PhysicsChainOutputControl transformMirror)
    {
        bool simulationEnabled = Tier != PhysicsChainQualityTier.Sleep
            && ResolveControl(simulation, SimulationEnabled);
        bool collisionEnabled = simulationEnabled && ResolveControl(collision, CollisionEnabled);
        return this with
        {
            SimulationRateHz = simulationEnabled ? SimulationRateHz : 0.0f,
            SolverSubstepCount = simulationEnabled ? SolverSubstepCount : 0,
            ConstraintIterationCount = simulationEnabled ? ConstraintIterationCount : 0,
            MaximumCatchUpSteps = simulationEnabled ? MaximumCatchUpSteps : 0,
            CollisionEnabled = collisionEnabled,
            PaletteCadence = ResolveOutputControl(palette, PaletteCadence),
            BoundsCadence = ResolveOutputControl(bounds, BoundsCadence),
            TransformMirrorCadence = ResolveOutputControl(transformMirror, TransformMirrorCadence),
        };
    }

    private static bool ResolveControl(PhysicsChainPolicyControl control, bool inherited)
        => control switch
        {
            PhysicsChainPolicyControl.Enabled => true,
            PhysicsChainPolicyControl.Disabled => false,
            _ => inherited,
        };

    private static PhysicsChainOutputCadence ResolveOutputControl(
        PhysicsChainOutputControl control,
        PhysicsChainOutputCadence inherited)
        => control switch
        {
            PhysicsChainOutputControl.EverySimulationStep => PhysicsChainOutputCadence.EverySimulationStep,
            PhysicsChainOutputControl.Hold => PhysicsChainOutputCadence.Hold,
            _ => inherited,
        };
}
