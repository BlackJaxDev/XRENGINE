using Silk.NET.Input;

namespace XREngine.Runtime.Platform.Desktop.Windowing;

/// <summary>Native resize hook installed and removed only on its window owner.</summary>
internal interface IDesktopInteractiveResizeHook : IDisposable
{
    void Install(DesktopSilkWindowBackend window);
    void OnInputCreated(IInputContext input);
}
