namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Development-only validation diagnostic that fails or cancels a bounded number of upcoming
/// imported-texture upload schedules. It is inert unless armed (through the editor MCP tool)
/// and costs one volatile read per schedule otherwise.
/// </summary>
/// <remarks>
/// Each injected outcome takes an existing scheduling branch instead of inventing a new one:
/// an admission failure follows the generation-ledger rejection path (the request is never
/// registered and <c>onError</c> runs), and a cancellation follows the stale/canceled path
/// (<c>onCanceled</c> runs). Normal streaming transitions and renderer-restart rehydration
/// both schedule through this boundary, so their retry and terminal-failure behavior can be
/// observed without contriving device or allocation failures.
/// </remarks>
public static class VulkanTextureUploadFaultInjection
{
    /// <summary>Upper bound for one arming request, to keep an armed run finite.</summary>
    public const int MaximumArmedOutcomes = 256;

    private static int _armedAdmissionFailures;
    private static int _armedCancellations;
    private static long _injectedAdmissionFailures;
    private static long _injectedCancellations;

    /// <summary>Remaining admission failures that upcoming schedules will receive.</summary>
    public static int ArmedAdmissionFailures => Volatile.Read(ref _armedAdmissionFailures);

    /// <summary>Remaining cancellations that upcoming schedules will receive.</summary>
    public static int ArmedCancellations => Volatile.Read(ref _armedCancellations);

    /// <summary>Total admission failures injected since process start.</summary>
    public static long InjectedAdmissionFailures => Interlocked.Read(ref _injectedAdmissionFailures);

    /// <summary>Total cancellations injected since process start.</summary>
    public static long InjectedCancellations => Interlocked.Read(ref _injectedCancellations);

    /// <summary>Replaces both remaining armed counts; zeros disarm.</summary>
    public static void Arm(int admissionFailures, int cancellations)
    {
        Volatile.Write(ref _armedAdmissionFailures, Math.Clamp(admissionFailures, 0, MaximumArmedOutcomes));
        Volatile.Write(ref _armedCancellations, Math.Clamp(cancellations, 0, MaximumArmedOutcomes));
    }

    internal static bool TryConsumeAdmissionFailure()
        => TryConsume(ref _armedAdmissionFailures, ref _injectedAdmissionFailures);

    internal static bool TryConsumeCancellation()
        => TryConsume(ref _armedCancellations, ref _injectedCancellations);

    private static bool TryConsume(ref int armed, ref long injected)
    {
        while (true)
        {
            int remaining = Volatile.Read(ref armed);
            if (remaining <= 0)
                return false;

            if (Interlocked.CompareExchange(ref armed, remaining - 1, remaining) != remaining)
                continue;

            Interlocked.Increment(ref injected);
            return true;
        }
    }
}
