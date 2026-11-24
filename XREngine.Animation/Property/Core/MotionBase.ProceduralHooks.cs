using System.Collections.Generic;

namespace XREngine.Animation;

public abstract partial class MotionBase
{
    /// <summary>
    /// True when this motion writes humanoid muscle channels and requires a valid
    /// <c>HumanoidComponent</c> avatar during state-machine playback validation.
    /// </summary>
    internal virtual bool RequiresHumanoidAvatar => false;

    /// <summary>
    /// True when this motion evaluates procedurally instead of sampling keyframed
    /// <see cref="AnimationClip"/> curves.
    /// </summary>
    internal virtual bool IsProceduralMotion => false;

    /// <summary>
    /// Builds and returns the root <see cref="AnimationMember"/> binding tree for procedural
    /// motions that write directly into <see cref="ValueStore"/> slots.
    /// </summary>
    internal virtual AnimationMember? EnsureProceduralBindingRootMember() => null;

    /// <summary>
    /// Returns the maximum number of humanoid root-motion contributions emitted per tick.
    /// </summary>
    internal virtual int PrepareProceduralHumanoidContributionCapacity() => 0;

    /// <summary>
    /// Called once after state-machine slot indices are assigned so procedural motions can
    /// cache dense integer slot offsets for zero-allocation evaluation.
    /// </summary>
    internal virtual void OnProceduralSlotLayoutBound(
        AnimationSlotLayout layout,
        IReadOnlyDictionary<string, AnimSlot> slotsByPath)
    {
    }

    /// <summary>
    /// Receives the frame delta time when the owning <see cref="AnimState"/> ticks.
    /// </summary>
    internal virtual void OnProceduralTickDelta(float deltaSeconds) { }

    /// <summary>
    /// Called when the owning <see cref="AnimState"/> becomes active.
    /// </summary>
    internal virtual void OnProceduralStateEntered() { }

    /// <summary>
    /// Called when the owning <see cref="AnimState"/> finishes exiting.
    /// </summary>
    internal virtual void OnProceduralStateExited() { }

    /// <summary>
    /// Evaluates procedural animation channels into <see cref="ValueStore"/> without allocating.
    /// </summary>
    internal virtual void EvaluateProceduralMotionValues(
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        bool additive)
    {
    }

    /// <summary>
    /// Returns the effective duration in seconds for state clock advancement.
    /// </summary>
    internal virtual double GetProceduralDurationSeconds(IDictionary<string, AnimVar> variables) => 1.0;

    /// <summary>
    /// Resets procedural playback state when the owning state restarts.
    /// </summary>
    internal virtual void RestartProceduralPlayback(float startSeconds) { }

    /// <summary>
    /// Stops procedural playback state when the owning state stops.
    /// </summary>
    internal virtual void StopProceduralPlayback() { }

    /// <summary>
    /// Releases any owned runtime resources when the state machine deinitializes.
    /// </summary>
    internal virtual void ReleaseProceduralResources() { }

    /// <summary>
    /// Emits a humanoid root-motion contribution for this frame when supported by the motion.
    /// </summary>
    internal virtual bool TryCreateProceduralHumanoidContribution(
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        out HumanoidMotionContribution contribution)
    {
        contribution = default;
        return false;
    }

    protected static float ReadVariableFloat(IDictionary<string, AnimVar> variables, string name, float fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar) && float.IsFinite(animVar.FloatValue))
            return animVar.FloatValue;

        return fallback;
    }

    protected static int ReadVariableInt(IDictionary<string, AnimVar> variables, string name, int fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar))
            return animVar.IntValue;

        return fallback;
    }

    protected static bool ReadVariableBool(IDictionary<string, AnimVar> variables, string name, bool fallback)
    {
        if (!string.IsNullOrEmpty(name) && variables.TryGetValue(name, out AnimVar? animVar))
            return animVar.BoolValue;

        return fallback;
    }
}
