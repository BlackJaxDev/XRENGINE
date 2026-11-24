namespace XREngine.Rendering;

/// <summary>Combines the draw readiness of several parts of one draw, for example the mono and the stereo version.</summary>
public static class RenderDrawReadinessCombiner
{
    /// <summary>
    /// Gives the state that blocks the draw most, with its reason. The order is <see cref="ERenderDrawReadiness.Failed"/>,
    /// <see cref="ERenderDrawReadiness.NotSupported"/>, <see cref="ERenderDrawReadiness.NotStarted"/>,
    /// <see cref="ERenderDrawReadiness.Compiling"/>, and <see cref="ERenderDrawReadiness.Ready"/>. The result is ready only
    /// when both parts are ready.
    /// </summary>
    /// <param name="first">The state of the first part.</param>
    /// <param name="firstReason">The reason of the first part.</param>
    /// <param name="second">The state of the second part.</param>
    /// <param name="secondReason">The reason of the second part.</param>
    /// <param name="reason">The reason of the result.</param>
    public static ERenderDrawReadiness Combine(
        ERenderDrawReadiness first,
        string firstReason,
        ERenderDrawReadiness second,
        string secondReason,
        out string reason)
    {
        if (Severity(second) > Severity(first))
        {
            reason = secondReason;
            return second;
        }

        reason = firstReason;
        return first;
    }

    private static int Severity(ERenderDrawReadiness state)
        => state switch
        {
            ERenderDrawReadiness.Ready => 0,
            ERenderDrawReadiness.Compiling => 1,
            ERenderDrawReadiness.NotStarted => 2,
            ERenderDrawReadiness.NotSupported => 3,
            _ => 4,
        };
}
