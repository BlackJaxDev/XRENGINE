using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming;

public interface IRuntimeVideoStreamingServices
{
    IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer);
}

public static class RuntimeVideoStreamingServices
{
    public static IRuntimeVideoStreamingServices? Current { get; set; }
}
