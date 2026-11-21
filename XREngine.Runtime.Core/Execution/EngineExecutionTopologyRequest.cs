using XREngine.Data.Rendering;

namespace XREngine.Execution;

/// <summary>
/// Immutable startup inputs used to resolve the process-wide execution budget.
/// </summary>
public sealed record EngineExecutionTopologyRequest
{
    public required int EffectiveProcessorCount { get; init; }
    public required int GeneralWorkerThreadCount { get; init; }
    public required int GeneralWorkerThreadCap { get; init; }
    public required int RenderWorkerThreadCount { get; init; }
    public required int RenderWorkerThreadCap { get; init; }
    public required int ReservedForegroundThreadCount { get; init; }
    public required int DedicatedBackgroundThreadCount { get; init; }
    public required bool AllowCpuOversubscription { get; init; }
    public required ERenderWorkerQos RenderWorkerQos { get; init; }

    /// <summary>
    /// Logical processors of the machine when the process affinity was narrowed below them
    /// (for example to the cores of the largest L3 cache); 0 when it was not narrowed. Automatic
    /// worker counts use <see cref="EffectiveProcessorCount"/>. The startup oversubscription
    /// check uses this larger count, because the fixed dedicated loops and explicit worker
    /// counts still fit the machine and only share the narrowed cores.
    /// </summary>
    public int SystemProcessorCount { get; init; }

    public EEngineExecutionSettingSource GeneralWorkerThreadCountSource { get; init; }
    public EEngineExecutionSettingSource GeneralWorkerThreadCapSource { get; init; }
    public EEngineExecutionSettingSource RenderWorkerThreadCountSource { get; init; }
    public EEngineExecutionSettingSource RenderWorkerThreadCapSource { get; init; }
    public EEngineExecutionSettingSource ReservedForegroundThreadCountSource { get; init; }
    public EEngineExecutionSettingSource AllowCpuOversubscriptionSource { get; init; }
    public EEngineExecutionSettingSource RenderWorkerQosSource { get; init; }

    /// <summary>
    /// Names the continuously active engine loops represented by the foreground
    /// reservation. This is startup diagnostic data and is never read per frame.
    /// </summary>
    public string[] ForegroundThreadNames { get; init; } = [];

    /// <summary>
    /// Names separately owned background lanes already included in
    /// <see cref="DedicatedBackgroundThreadCount"/>.
    /// </summary>
    public string[] DedicatedBackgroundThreadNames { get; init; } = [];

    /// <summary>
    /// Dedicated lanes, already included in <see cref="DedicatedBackgroundThreadCount"/>, that run
    /// only while an XR session is active. Without a session their processors go to general workers.
    /// </summary>
    public int XrSessionDedicatedThreadCount { get; init; }
}
