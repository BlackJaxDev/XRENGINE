namespace XREngine.Rendering.Commands;

/// <summary>
/// Development-only validation diagnostic that rejects a bounded number of canonical scene
/// publications. It is inert unless armed (through the editor MCP tool) and costs one volatile
/// read per publication boundary otherwise.
/// </summary>
/// <remarks>
/// An armed rejection is consumed only when a boundary would open a new publication, after the
/// unchanged-publication reuse check. It takes the same non-throwing path as a capacity
/// rejection: the previously accepted publication stays current, no provisional identity is
/// delivered, and the next boundary retries. This exercises retry of a real pending mutation
/// without contriving a capacity failure.
/// </remarks>
public static class AdvancedPublicationFaultInjection
{
    /// <summary>Upper bound for one arming request, to keep an armed run finite.</summary>
    public const int MaximumArmedRejections = 600;

    private static int _armedPreflightRejections;
    private static long _injectedPreflightRejections;

    /// <summary>Remaining rejections that will be injected at upcoming publication boundaries.</summary>
    public static int ArmedPreflightRejections
        => Volatile.Read(ref _armedPreflightRejections);

    /// <summary>Total rejections injected since process start.</summary>
    public static long InjectedPreflightRejections
        => Interlocked.Read(ref _injectedPreflightRejections);

    /// <summary>Replaces the remaining armed count; zero disarms.</summary>
    public static void ArmPreflightRejections(int count)
        => Volatile.Write(ref _armedPreflightRejections, Math.Clamp(count, 0, MaximumArmedRejections));

    internal static bool TryConsumePreflightRejection()
    {
        while (true)
        {
            int armed = Volatile.Read(ref _armedPreflightRejections);
            if (armed <= 0)
                return false;

            if (Interlocked.CompareExchange(ref _armedPreflightRejections, armed - 1, armed) != armed)
                continue;

            Interlocked.Increment(ref _injectedPreflightRejections);
            return true;
        }
    }
}
