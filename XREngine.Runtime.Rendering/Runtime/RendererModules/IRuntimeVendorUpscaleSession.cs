namespace XREngine.Rendering;

/// <summary>
/// Stable lifetime handle for backend-owned vendor-upscale session state.
/// </summary>
internal interface IRuntimeVendorUpscaleSession : IDisposable
{
    void ResetResources();
}
