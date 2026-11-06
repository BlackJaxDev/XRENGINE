using XREngine.Scene.Components.Particles;

namespace XREngine.Cutscenes;

/// <summary>
/// Controls a <see cref="ParticleEmitterComponent"/>. A reset clears the particles and sets the seed. A warm-up uses
/// <see cref="ParticleEmitterComponent.Prewarm"/>. A hold sets <see cref="ParticleEmitterComponent.IsSimulating"/> to false
/// and puts back the earlier value at the release.
/// </summary>
public sealed class ParticleEmitterSimulationTarget : ICutsceneSimulationTarget
{
    private readonly ParticleEmitterComponent _emitter;
    private bool _held;
    private bool _simulatingBeforeHold;
    private bool _clearedInHold;

    /// <summary>Creates a target for a particle emitter.</summary>
    /// <param name="emitter">The particle emitter.</param>
    public ParticleEmitterSimulationTarget(ParticleEmitterComponent emitter)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        _emitter = emitter;
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
        // A held emitter does not run, so its cleared state does not change. Each scrub step does not need a new clear.
        if (_held && _clearedInHold)
            return;

        _emitter.Prewarm(0.0f, seed, 0.0);
        _clearedInHold = _held;
    }

    /// <inheritdoc/>
    public void SetHeld(bool held)
    {
        if (held == _held)
            return;

        _held = held;
        if (held)
        {
            _simulatingBeforeHold = _emitter.IsSimulating;
            _emitter.IsSimulating = false;
            _clearedInHold = false;
        }
        else
        {
            _emitter.IsSimulating = _simulatingBeforeHold;
        }
    }

    /// <inheritdoc/>
    public void KeepHeld()
    {
        // IsSimulating is false while held, so the emitter does not move.
    }

    /// <inheritdoc/>
    public void WarmUp(float seconds, int seed, double budgetMilliseconds)
    {
        _emitter.Prewarm(seconds, seed, budgetMilliseconds);
        _clearedInHold = false;
    }
}
