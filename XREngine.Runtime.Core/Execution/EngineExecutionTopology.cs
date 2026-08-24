using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>
/// Resolved process-wide CPU execution budget. Phase 1A establishes this
/// composition-root contract without changing existing Vulkan recording workers.
/// </summary>
public sealed class EngineExecutionTopology
{
    public const int AutomaticWorkerCount = -1;
    public const int MaximumWorkerCount = 32;
    public const int DefaultGeneralWorkerCap = 16;
    public const int DefaultRenderWorkerCap = 8;
    public const int DefaultForegroundReservation = 4;

    private EngineExecutionTopology(
        EngineExecutionTopologyRequest request,
        int reservedForegroundThreadCount,
        int generalWorkerThreadCount,
        int renderWorkerThreadCount)
    {
        Request = request;
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
    public int GeneralWorkerThreadCount { get; }
    public int RenderWorkerThreadCount { get; }
    public int DedicatedBackgroundThreadCount { get; }
    public int TotalReservedThreadCount { get; }
    public bool AllowCpuOversubscription { get; }
    public ERenderWorkerQos RenderWorkerQos { get; }
    public bool IsOversubscribed => TotalReservedThreadCount > EffectiveProcessorCount;
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
        int generalWorkerCount = request.GeneralWorkerThreadCount == AutomaticWorkerCount
            ? Math.Min(request.GeneralWorkerThreadCap, remainingGeneralBudget)
            : request.GeneralWorkerThreadCount;

        var topology = new EngineExecutionTopology(
            request,
            foregroundCount,
            generalWorkerCount,
            renderWorkerCount);

        bool hasExplicitReservation =
            request.GeneralWorkerThreadCount != AutomaticWorkerCount ||
            request.RenderWorkerThreadCount != AutomaticWorkerCount ||
            request.ReservedForegroundThreadCount != AutomaticWorkerCount;
        if (topology.IsOversubscribed &&
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
            .Append(" foreground=")
            .Append(ReservedForegroundThreadCount)
            .Append(" general=")
            .Append(GeneralWorkerThreadCount)
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
        builder.Append(" phase1B=scheduler-active; existing Vulkan/OpenXR recording workers unchanged");
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
