namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>Thread on which OpenXR prepares the next frame.</summary>
public enum OpenXrRenderPacingMode
{
    InRenderCallback,
    PostRenderCallback,
    DedicatedThread,
    CollectVisibleThread
}
