using XREngine.Rendering;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Owns the graphics resources transferred to one secondary worker.</summary>
internal interface IDesktopSecondaryGpuContextResources
{
    bool HasRenderer { get; }
    void Initialize();
    void Execute(Action<AbstractRenderer> job);
    void CleanUp();
    /// <summary>Completes on the native owner after window disposal, or reports retained ownership.</summary>
    void Release(Action<bool> completed);
}
