namespace XREngine.Components;

/// <summary>
/// Stores the simulation cadence and render interval for one runtime slot.
/// </summary>
internal struct PhysicsChainSimulationClock
{
    public float Phase;
    public float ElapsedSeconds;
    public float CadenceProgress;
    public long FixedRenderAccumulatedTicks;

    /// <summary>Sets the initial cadence state for one runtime slot.</summary>
    [global::Engine.Diagnostics.HotPath]
    public void Initialize(float phase, float rate)
    {
        Phase = phase;
        CadenceProgress = phase;
        ElapsedSeconds = rate > 0.0f ? phase / rate : 0.0f;
        FixedRenderAccumulatedTicks = 0L;
    }

    /// <summary>Clears elapsed time and retained render time.</summary>
    [global::Engine.Diagnostics.HotPath]
    public void Reset()
    {
        ElapsedSeconds = 0.0f;
        FixedRenderAccumulatedTicks = 0L;
    }

    /// <summary>Sets the retained progress for the current simulation cadence.</summary>
    [global::Engine.Diagnostics.HotPath]
    public void SetCadenceProgress(float progress, float rate)
    {
        CadenceProgress = progress;
        ElapsedSeconds = rate > 0.0f ? progress / rate : 0.0f;
    }

    /// <summary>Counts the simulation steps due from accumulated frame time.</summary>
    [global::Engine.Diagnostics.HotPath]
    public void ResolveLoop(float deltaSeconds, float rate, int maximumCatchUpSteps, out int loop, out float stepDelta)
    {
        float frameTime = 1.0f / rate;
        ElapsedSeconds += deltaSeconds;
        loop = 0;
        while (ElapsedSeconds >= frameTime)
        {
            ElapsedSeconds -= frameTime;
            if (++loop >= maximumCatchUpSteps)
            {
                ElapsedSeconds = 0.0f;
                break;
            }
        }

        stepDelta = frameTime;
    }
}
