namespace XREngine.Rendering;

/// <summary>
/// Number of budget periods that the current collect stands for. Collect-keyed budgets (cascade
/// refresh grants, texture streaming transitions and their frame clock, the VR desktop pressure
/// hold) multiply their per-collect allowance by this value, so that they keep their real-time rate
/// when the collect rate changes.
/// </summary>
/// <remarks>
/// With render pacing (desktop, or VR without XR collect pacing) the value is 1, and each collect is
/// one budget period, as before. With XR collect pacing there is one collect for each XR frame, and
/// the engine timer sets the value to the number of XR display periods since the last collect
/// (<c>xrWaitFrame</c>'s predicted display period). The fraction carries to the next collect, so
/// the sum over time equals the elapsed display periods. A budget then means "per display period".
/// The collect thread writes the value before the collect callbacks run; collect and swap
/// callbacks read it.
/// </remarks>
public static class CollectBudgetPeriods
{
    /// <summary>Largest value; a longer stall does not release a larger burst of budgeted work.</summary>
    public const int Maximum = 8;

    private static int s_current = 1;

    /// <summary>Budget periods of the current collect, from 1 to <see cref="Maximum"/>.</summary>
    public static int Current => Volatile.Read(ref s_current);

    /// <summary>Sets the budget periods of the collect that starts now. The collect thread calls it.</summary>
    public static void Set(int periods)
        => Volatile.Write(ref s_current, Math.Clamp(periods, 1, Maximum));

    /// <summary>Multiplies a per-period allowance by <see cref="Current"/>; a non-positive allowance (no limit) stays as it is.</summary>
    public static int Scale(int perPeriodAllowance)
        => perPeriodAllowance <= 0 ? perPeriodAllowance : perPeriodAllowance * Current;

    /// <summary>Multiplies a per-period byte allowance by <see cref="Current"/>.</summary>
    public static long Scale(long perPeriodAllowance)
        => perPeriodAllowance <= 0L ? perPeriodAllowance : perPeriodAllowance * Current;
}
