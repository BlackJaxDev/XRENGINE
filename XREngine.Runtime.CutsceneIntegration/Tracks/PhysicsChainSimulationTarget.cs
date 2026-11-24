using XREngine.Components;

namespace XREngine.Cutscenes;

/// <summary>
/// Controls a <see cref="PhysicsChainComponent"/>. A reset asks the chain to reset at its next simulation step, so the
/// chain reads the current pose of its bones and the cutscene tick writes no bone. A hold asks for that reset in each frame.
/// A physics chain cannot run steps on request, so it has no warm-up.
/// </summary>
public sealed class PhysicsChainSimulationTarget : ICutsceneSimulationTarget
{
    private readonly PhysicsChainComponent _chain;

    /// <summary>Creates a target for a physics chain.</summary>
    /// <param name="chain">The physics chain.</param>
    public PhysicsChainSimulationTarget(PhysicsChainComponent chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        _chain = chain;
        Name = $"physics chain on '{chain.SceneNode?.Name}'";
    }

    /// <inheritdoc/>
    public object Source => _chain;

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool SupportsWarmUp => false;

    /// <inheritdoc/>
    public void ResetToCurrentPose(int seed)
        => _chain.ResetSimulationToCurrentPose();

    /// <inheritdoc/>
    public void SetHeld(bool held)
    {
        // The hold is a reset in each frame, from KeepHeld. The chain has no other state to change.
    }

    /// <inheritdoc/>
    public void KeepHeld()
        => _chain.ResetSimulationToCurrentPose();

    /// <inheritdoc/>
    public void WarmUp(float seconds, int seed, double budgetMilliseconds)
        => _chain.ResetSimulationToCurrentPose();
}
