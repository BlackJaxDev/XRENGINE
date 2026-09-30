namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>Compatibility identity for persisted OpenXR pacing values.</summary>
    public enum OpenXrRenderPacingMode
    {
        InRenderCallback = 0,
        PostRenderCallback = 1,
        DedicatedThread = 2,
        CollectVisibleThread = 3
    }
}
