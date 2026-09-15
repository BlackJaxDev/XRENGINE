using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Allocation-free timing facts captured for one completed OpenXR lifecycle frame.</summary>
public struct OpenXrSmokeFrameTiming
{
    public int OpenXrLifecycleFrameId { get; set; }
    public ulong EngineRenderFrameId { get; set; }
    public long PredictedDisplayTimeXr { get; set; }
    public long PredictedDisplayPeriodXr { get; set; }
    public uint ShouldRender { get; set; }
    public long WaitStartQpc { get; set; }
    public long WaitEndQpc { get; set; }
    public long BeginStartQpc { get; set; }
    public long BeginEndQpc { get; set; }
    public long EndStartQpc { get; set; }
    public long EndEndQpc { get; set; }
    public bool WaitEndXrAvailable { get; set; }
    public long WaitEndXr { get; set; }
    public bool EndStartXrAvailable { get; set; }
    public long EndStartXr { get; set; }
    public bool EndEndXrAvailable { get; set; }
    public long EndEndXr { get; set; }
    public long SafetyMarginNanoseconds { get; set; }
    public bool HandoffSlackAvailable { get; set; }
    public long HandoffSlackNanoseconds { get; set; }
    public bool ReturnSlackAvailable { get; set; }
    public long ReturnSlackNanoseconds { get; set; }
    public int EndFrameResult { get; set; }
    public uint EndFrameLayerCount { get; set; }
    public XRWindowCompletedRenderInterval DesktopRenderWindowCpuInterval { get; set; }
}
