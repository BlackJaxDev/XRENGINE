namespace XREngine.RenderBench;

/// <summary>Named real-scene fixture contracts for the Phase 5.2 visibility oracle.</summary>
internal static class RenderBenchScenarioWorkloads
{
    public const string Default = "default";
    public const string OpenStatic = "open-static";
    public const string ModerateStatic = "moderate-static";
    public const string HeavyStatic = "heavy-static";
    public const string HeavyMovingCut = "heavy-moving-cut";
    public const string MaskedStatic = "masked-static";
    public const string MaskedMoving = "masked-moving";

    public static readonly string[] Matrix =
    [
        OpenStatic,
        ModerateStatic,
        HeavyStatic,
        HeavyMovingCut,
        MaskedStatic,
        MaskedMoving,
    ];

    public static bool IsKnown(string workload)
        => workload == Default || Matrix.Contains(workload, StringComparer.Ordinal);

    public static bool RequiresOcclusion(string workload) => workload != OpenStatic;

    public static bool IsMoving(string workload)
        => workload is Default or HeavyMovingCut or MaskedMoving;

    public static bool IsMasked(string workload)
        => workload is MaskedStatic or MaskedMoving;

    public static bool IsHeavy(string workload)
        => workload is HeavyStatic or HeavyMovingCut;
}
