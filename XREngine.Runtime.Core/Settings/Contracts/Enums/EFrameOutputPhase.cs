namespace XREngine;

public enum EFrameOutputPhase
{
    Collect,
    Swap,
    Render,
    Submit,
    GpuComplete,
    Overlay,
    Present,
}
