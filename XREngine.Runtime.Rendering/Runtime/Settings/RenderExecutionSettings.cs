using MemoryPack;
using System.ComponentModel;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Execution;

namespace XREngine;

/// <summary>
/// Startup-only execution-budget settings shared by renderer-neutral engine work.
/// The process scheduler applies these values to its persistent general and
/// renderer-neutral render domains. Vulkan and OpenXR recording remain on
/// their legacy workers until their later migration phase.
/// </summary>
[Serializable]
[MemoryPackable]
public partial class RenderExecutionSettings : XRBase
{
    private int _generalWorkerThreadCount = EngineExecutionTopology.AutomaticWorkerCount;
    private int _generalWorkerThreadCap = EngineExecutionTopology.DefaultGeneralWorkerCap;
    private int _renderWorkerThreadCount;
    private int _renderWorkerThreadCap = EngineExecutionTopology.DefaultRenderWorkerCap;
    private int _reservedForegroundThreadCount = EngineExecutionTopology.AutomaticWorkerCount;
    private bool _allowCpuOversubscription;
    private ERenderWorkerQos _renderWorkerQos = ERenderWorkerQos.OsDefault;

    [Category("Execution")]
    [Description("General worker threads: -1 selects the startup auto policy, 0 uses cooperative inline execution, and 1..32 selects an explicit count. Requires restart.")]
    public int GeneralWorkerThreadCount
    {
        get => _generalWorkerThreadCount;
        set => SetField(ref _generalWorkerThreadCount, value);
    }

    [Category("Execution")]
    [Description("Upper bound for automatic or explicit general worker threads. Valid range is 1..32. Requires restart.")]
    public int GeneralWorkerThreadCap
    {
        get => _generalWorkerThreadCap;
        set => SetField(ref _generalWorkerThreadCap, value);
    }

    [Category("Execution")]
    [Description("Renderer-neutral background render workers: -1 selects auto, 0 uses render-thread lane 0 only, and 1..32 creates explicit background lanes. Vulkan/OpenXR recording remains on its legacy workers until migration. Requires restart.")]
    public int RenderWorkerThreadCount
    {
        get => _renderWorkerThreadCount;
        set => SetField(ref _renderWorkerThreadCount, value);
    }

    [Category("Execution")]
    [Description("Upper bound used by automatic render-worker selection. Valid range is 1..32. Requires restart.")]
    public int RenderWorkerThreadCap
    {
        get => _renderWorkerThreadCap;
        set => SetField(ref _renderWorkerThreadCap, value);
    }

    [Category("Execution")]
    [Description("Continuously active foreground engine thread reservation: -1 selects the mode-aware startup default; 1..32 selects an explicit reservation. Requires restart.")]
    public int ReservedForegroundThreadCount
    {
        get => _reservedForegroundThreadCount;
        set => SetField(ref _reservedForegroundThreadCount, value);
    }

    [Category("Execution")]
    [Description("Allows an explicitly oversubscribed CPU topology for diagnostics. Disabled by default. Requires restart.")]
    public bool AllowCpuOversubscription
    {
        get => _allowCpuOversubscription;
        set => SetField(ref _allowCpuOversubscription, value);
    }

    [Category("Execution")]
    [Description("Scheduling policy requested for renderer-neutral render workers. High remains diagnostic until hardware validation passes. Requires restart.")]
    public ERenderWorkerQos RenderWorkerQos
    {
        get => _renderWorkerQos;
        set => SetField(ref _renderWorkerQos, value);
    }
}
