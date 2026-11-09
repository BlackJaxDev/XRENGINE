using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>
/// Immutable process-wide CPU execution budget resolved by the composition
/// root before worker domains or backend-owned lanes are created.
/// </summary>
public sealed class EngineExecutionTopology
{
    public const int AutomaticWorkerCount = -1;
    public const int MaximumWorkerCount = 32;
    public const int DefaultGeneralWorkerCap = 16;
    public const int DefaultRenderWorkerCap = 8;
    public const int DefaultForegroundReservation = 4;

    /// <summary>
    /// Smallest automatic general worker pool when processors remain after the foreground
    /// reservation. Dedicated background lanes (recording, pipeline compile, OpenXR) are
    /// mostly idle, so they must not take the last processors from Any-affinity jobs such
    /// as imports and texture cooks.
    /// </summary>
    public const int MinimumAutomaticGeneralWorkerCount = 2;

    private EngineExecutionTopology(
        EngineExecutionTopologyRequest request,
        int reservedForegroundThreadCount,
        int generalWorkerThreadCount,
        int generalWorkerThreadCountWithoutXrSession,
        int renderWorkerThreadCount)
    {
        Request = request;
        GeneralWorkerThreadCountWithoutXrSession = generalWorkerThreadCountWithoutXrSession;
        EffectiveProcessorCount = request.EffectiveProcessorCount;
        ReservedForegroundThreadCount = reservedForegroundThreadCount;
        GeneralWorkerThreadCount = generalWorkerThreadCount;
        RenderWorkerThreadCount = renderWorkerThreadCount;
        DedicatedBackgroundThreadCount = request.DedicatedBackgroundThreadCount;
        AllowCpuOversubscription = request.AllowCpuOversubscription;
        RenderWorkerQos = request.RenderWorkerQos;
        TotalReservedThreadCount = checked(
            reservedForegroundThreadCount +
            generalWorkerThreadCount +
            renderWorkerThreadCount +
            request.DedicatedBackgroundThreadCount);
    }

    public EngineExecutionTopologyRequest Request { get; }
    public int EffectiveProcessorCount { get; }
    public int ReservedForegroundThreadCount { get; }
    /// <summary>Gets the general workers while an XR session is active and its dedicated lanes run.</summary>
    public int GeneralWorkerThreadCount { get; }

    /// <summary>
    /// Gets the general workers while no XR session is active. The XR session lanes are idle then,
    /// so their processors are given to general work. It is never below <see cref="GeneralWorkerThreadCount"/>.
    /// </summary>
    public int GeneralWorkerThreadCountWithoutXrSession { get; }
    public int RenderWorkerThreadCount { get; }
    public int DedicatedBackgroundThreadCount { get; }
    public int TotalReservedThreadCount { get; }
    public bool AllowCpuOversubscription { get; }
    public ERenderWorkerQos RenderWorkerQos { get; }
    public bool IsOversubscribed => TotalReservedThreadCount > EffectiveProcessorCount;

    /// <summary>
    /// True when the reserved threads exceed the machine budget: the larger of
    /// <see cref="EffectiveProcessorCount"/> and <see cref="EngineExecutionTopologyRequest.SystemProcessorCount"/>.
    /// </summary>
    public bool ExceedsProcessorBudget
        => TotalReservedThreadCount > Math.Max(EffectiveProcessorCount, Request.SystemProcessorCount);
    public bool GeneralWorkerCountIsAutomatic => Request.GeneralWorkerThreadCount == AutomaticWorkerCount;
    public bool RenderWorkerCountIsAutomatic => Request.RenderWorkerThreadCount == AutomaticWorkerCount;
    public bool ForegroundReservationIsAutomatic => Request.ReservedForegroundThreadCount == AutomaticWorkerCount;

    /// <summary>
    /// Resolves and validates one immutable startup topology.
    /// </summary>
    public static EngineExecutionTopology Resolve(EngineExecutionTopologyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.EffectiveProcessorCount <= 0)
            throw new InvalidOperationException("The effective processor count must be positive.");

        ValidateCap(request.GeneralWorkerThreadCap, nameof(request.GeneralWorkerThreadCap));
        ValidateCap(request.RenderWorkerThreadCap, nameof(request.RenderWorkerThreadCap));
        ValidateGeneralWorkerCount(request.GeneralWorkerThreadCount, request.GeneralWorkerThreadCap);
        ValidateRenderWorkerCount(request.RenderWorkerThreadCount, request.RenderWorkerThreadCap);
        ValidateForegroundReservation(request.ReservedForegroundThreadCount);

        if (request.DedicatedBackgroundThreadCount < 0 || request.DedicatedBackgroundThreadCount > MaximumWorkerCount)
        {
            throw new InvalidOperationException(
                $"{nameof(request.DedicatedBackgroundThreadCount)} must be between 0 and {MaximumWorkerCount}.");
        }

        if (request.XrSessionDedicatedThreadCount < 0 ||
            request.XrSessionDedicatedThreadCount > request.DedicatedBackgroundThreadCount)
        {
            throw new InvalidOperationException(
                $"{nameof(request.XrSessionDedicatedThreadCount)} must be between 0 and {nameof(request.DedicatedBackgroundThreadCount)}.");
        }

        if (!Enum.IsDefined(request.RenderWorkerQos))
            throw new InvalidOperationException($"Unknown render worker QoS value '{request.RenderWorkerQos}'.");

        int foregroundCount = request.ReservedForegroundThreadCount == AutomaticWorkerCount
            ? ResolveCompatibilityForegroundReservation(request.EffectiveProcessorCount)
            : request.ReservedForegroundThreadCount;

        int availableWorkerBudget = Math.Max(
            0,
            request.EffectiveProcessorCount - foregroundCount - request.DedicatedBackgroundThreadCount);

        int renderWorkerCount = request.RenderWorkerThreadCount == AutomaticWorkerCount
            ? request.EffectiveProcessorCount < 8
                ? 0
                : Math.Min(request.RenderWorkerThreadCap, availableWorkerBudget / 3)
            : request.RenderWorkerThreadCount;

        int remainingGeneralBudget = Math.Max(0, availableWorkerBudget - renderWorkerCount);
        // The floor oversubscribes only the dedicated background lanes, never the
        // foreground reservation. With no processor left after the foreground threads,
        // the floor is zero and jobs keep the cooperative inline mode.
        int automaticGeneralFloor = Math.Min(
            MinimumAutomaticGeneralWorkerCount,
            Math.Max(0, request.EffectiveProcessorCount - foregroundCount));
        bool automaticGeneral = request.GeneralWorkerThreadCount == AutomaticWorkerCount;
        int generalWorkerCount = automaticGeneral
            ? Math.Min(request.GeneralWorkerThreadCap, Math.Max(automaticGeneralFloor, remainingGeneralBudget))
            : request.GeneralWorkerThreadCount;
        // Without an XR session its dedicated lanes are idle. The render worker count stays the
        // one resolved with the lanes, so only the general pool changes between the two states.
        int generalWorkerCountWithoutXrSession = automaticGeneral
            ? Math.Min(
                request.GeneralWorkerThreadCap,
                Math.Max(generalWorkerCount, remainingGeneralBudget + request.XrSessionDedicatedThreadCount))
            : generalWorkerCount;

        var topology = new EngineExecutionTopology(
            request,
            foregroundCount,
            generalWorkerCount,
            generalWorkerCountWithoutXrSession,
            renderWorkerCount);

        bool hasExplicitReservation =
            request.GeneralWorkerThreadCount != AutomaticWorkerCount ||
            request.RenderWorkerThreadCount != AutomaticWorkerCount ||
            request.ReservedForegroundThreadCount != AutomaticWorkerCount;
        if (topology.ExceedsProcessorBudget &&
            !topology.AllowCpuOversubscription &&
            hasExplicitReservation)
        {
            throw new InvalidOperationException(
                "Execution topology oversubscribes the process CPU budget: " +
                $"effectiveProcessors={topology.EffectiveProcessorCount}, " +
                $"foreground={topology.ReservedForegroundThreadCount}, " +
                $"general={topology.GeneralWorkerThreadCount}, " +
                $"render={topology.RenderWorkerThreadCount}, " +
                $"dedicated={topology.DedicatedBackgroundThreadCount}, " +
                $"total={topology.TotalReservedThreadCount}. " +
                "Reduce an explicit worker count or enable AllowCpuOversubscription for a diagnostic run.");
        }

        return topology;
    }

    /// <summary>
    /// Builds the single startup diagnostic line for the resolved topology.
    /// </summary>
    public string CreateDiagnosticSummary()
    {
        var builder = new StringBuilder(384);
        builder.Append("[ExecutionTopology] processors=")
            .Append(EffectiveProcessorCount)
            .Append(" systemProcessors=")
            .Append(Request.SystemProcessorCount)
            .Append(" foreground=")
            .Append(ReservedForegroundThreadCount)
            .Append(" general=")
            .Append(GeneralWorkerThreadCount)
            .Append(" generalWithoutXrSession=")
            .Append(GeneralWorkerThreadCountWithoutXrSession)
            .Append(" render=")
            .Append(RenderWorkerThreadCount)
            .Append(" dedicated=")
            .Append(DedicatedBackgroundThreadCount)
            .Append(" total=")
            .Append(TotalReservedThreadCount)
            .Append(" oversubscribed=")
            .Append(IsOversubscribed)
            .Append(" allowOversubscription=")
            .Append(AllowCpuOversubscription)
            .Append(" renderQos=")
            .Append(RenderWorkerQos)
            .Append(" requests={foreground:")
            .Append(Request.ReservedForegroundThreadCount)
            .Append(",general:")
            .Append(Request.GeneralWorkerThreadCount)
            .Append(",generalCap:")
            .Append(Request.GeneralWorkerThreadCap)
            .Append(",render:")
            .Append(Request.RenderWorkerThreadCount)
            .Append(",renderCap:")
            .Append(Request.RenderWorkerThreadCap)
            .Append(",dedicated:")
            .Append(Request.DedicatedBackgroundThreadCount)
            .Append(",xrSessionDedicated:")
            .Append(Request.XrSessionDedicatedThreadCount)
            .Append('}')
            .Append(" sources={general:")
            .Append(Request.GeneralWorkerThreadCountSource)
            .Append(",generalCap:")
            .Append(Request.GeneralWorkerThreadCapSource)
            .Append(",render:")
            .Append(Request.RenderWorkerThreadCountSource)
            .Append(",renderCap:")
            .Append(Request.RenderWorkerThreadCapSource)
            .Append(",foreground:")
            .Append(Request.ReservedForegroundThreadCountSource)
            .Append(",allowOversubscription:")
            .Append(Request.AllowCpuOversubscriptionSource)
            .Append(",renderQos:")
            .Append(Request.RenderWorkerQosSource)
            .Append('}');

        AppendNames(builder, " foregroundLoops", Request.ForegroundThreadNames);
        AppendNames(builder, " dedicatedLanes", Request.DedicatedBackgroundThreadNames);
        builder.Append(" schedulerDomains=[general,render,job-auxiliary] backendRecording=retained");
        return builder.ToString();
    }

    private static int ResolveCompatibilityForegroundReservation(int processorCount)
        => Math.Min(DefaultForegroundReservation, Math.Max(0, processorCount - 1));

    private static void ValidateCap(int value, string name)
    {
        if (value is < 1 or > MaximumWorkerCount)
            throw new InvalidOperationException($"{name} must be between 1 and {MaximumWorkerCount}; received {value}.");
    }

    private static void ValidateGeneralWorkerCount(int value, int cap)
    {
        if (value != AutomaticWorkerCount && value is < 0 or > MaximumWorkerCount)
        {
            throw new InvalidOperationException(
                $"GeneralWorkerThreadCount must be -1 (auto) or between 0 and {MaximumWorkerCount}; received {value}.");
        }

        if (value > cap)
            throw new InvalidOperationException($"GeneralWorkerThreadCount {value} exceeds its cap {cap}.");
    }

    private static void ValidateRenderWorkerCount(int value, int cap)
    {
        if (value != AutomaticWorkerCount && value is < 0 or > MaximumWorkerCount)
        {
            throw new InvalidOperationException(
                $"RenderWorkerThreadCount must be -1 (auto) or between 0 and {MaximumWorkerCount}; received {value}.");
        }

        if (value > cap)
            throw new InvalidOperationException($"RenderWorkerThreadCount {value} exceeds its cap {cap}.");
    }

    private static void ValidateForegroundReservation(int value)
    {
        if (value != AutomaticWorkerCount && value is < 1 or > MaximumWorkerCount)
        {
            throw new InvalidOperationException(
                $"ReservedForegroundThreadCount must be -1 (auto) or between 1 and {MaximumWorkerCount}; received {value}.");
        }
    }

    private static void AppendNames(StringBuilder builder, string label, string[] names)
    {
        if (names.Length == 0)
            return;

        builder.Append(label).Append("=[");
        for (int i = 0; i < names.Length; i++)
        {
            if (i != 0)
                builder.Append(',');
            builder.Append(names[i]);
        }
        builder.Append(']');
    }
}
