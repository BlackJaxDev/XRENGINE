using XREngine.Scene.Components.Particles;

namespace XREngine.Cutscenes;

/// <summary>
/// Controls a <see cref="ParticleEmitterComponent"/>. A reset clears the particles and sets the seed. A warm-up uses
/// <see cref="ParticleEmitterComponent.Prewarm"/>. A hold uses <see cref="ParticleEmitterComponent.BeginSimulationHold"/>,
/// so the user's <see cref="ParticleEmitterComponent.IsSimulating"/>, <see cref="ParticleEmitterComponent.Pause"/>, and
/// <see cref="ParticleEmitterComponent.Resume"/> state and the tick registration do not change.
/// </summary>
public sealed class ParticleEmitterSimulationTarget : ICutsceneSimulationTarget
{
    // A large prime spreads the seeds of the emitters on one node, so they do not repeat the same random pattern.
    private const int SeedStride = 7919;

    private readonly ParticleEmitterComponent _emitter;
    private readonly int _seedOffset;
    private bool _held;
    private bool _clearedInHold;

    /// <summary>Creates a target for a particle emitter.</summary>
    /// <param name="emitter">The particle emitter.</param>
    /// <param name="emitterIndex">The index of the emitter among the emitters of its node. It changes the seed of each emitter.</param>
    public ParticleEmitterSimulationTarget(ParticleEmitterComponent emitter, int emitterIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        _emitter = emitter;
        _seedOffset = unchecked(emitterIndex * SeedStride);
        Name = $"particle emitter on '{emitter.SceneNode?.Name}'";
    }

    /// <inheritdoc/>
    public object Source => _emitter;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool SupportsWarmUp => true;

    /// <inheritdoc/>
    public void ResetToCurrentPose(int seed)
    {
        // A held emitter does not step, so its cleared state does not change. Each scrub step does not need a new clear.
        if (_held && _clearedInHold)
            return;

        _emitter.Prewarm(0.0f, unchecked(seed + _seedOffset), 0.0);
        _clearedInHold = _held;
    }

    /// <inheritdoc/>
    public void SetHeld(bool held)
    {
        if (held == _held)
            return;

        _held = held;
        _clearedInHold = false;
        if (held)
            _emitter.BeginSimulationHold();
        else
            _emitter.EndSimulationHold();
    }

    /// <inheritdoc/>
    public void KeepHeld()
    {
        // The controller hold stays until SetHeld(false), so the emitter does not step.
    }

    /// <inheritdoc/>
    public void WarmUp(float seconds, int seed, double budgetMilliseconds)
    {
        _emitter.Prewarm(seconds, unchecked(seed + _seedOffset), budgetMilliseconds);
        _clearedInHold = false;
    }
}
