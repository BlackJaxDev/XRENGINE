namespace XREngine;

public enum EVrViewRenderImplementationPath
{
    Unsupported,
    SequentialViews,
    ParallelCommandBufferRecording,
    OpenXrSinglePassCompatibility,
    TrueSinglePassStereo,
}
