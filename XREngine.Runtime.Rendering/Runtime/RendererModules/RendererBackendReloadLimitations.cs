namespace XREngine.Rendering;

/// <summary>
/// Conditions that prevent a backend module from being safely replaced in-place.
/// </summary>
[Flags]
public enum RendererBackendReloadLimitations
{
    None = 0,

    /// <summary>
    /// All renderer instances created by the module must be destroyed first.
    /// </summary>
    RequiresRendererTeardown = 1 << 0,

    /// <summary>
    /// Native graphics-loader state remains process scoped.
    /// </summary>
    NativeLoaderIsProcessScoped = 1 << 1,

    /// <summary>
    /// Active OpenXR sessions and swapchains must be torn down first.
    /// </summary>
    RequiresOpenXrSessionTeardown = 1 << 2,

    /// <summary>
    /// External SDK state cannot be unloaded without restarting the process.
    /// </summary>
    RequiresProcessRestart = 1 << 3,
}
