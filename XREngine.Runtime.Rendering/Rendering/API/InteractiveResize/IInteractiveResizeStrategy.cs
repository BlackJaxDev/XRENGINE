using Silk.NET.Input;
using Silk.NET.Maths;

namespace XREngine.Rendering;

internal interface IInteractiveResizeStrategy
{
    EInteractiveWindowResizeStrategy Kind { get; }
    bool IsInstalled { get; }

    void Install(XRWindow window);
    void Uninstall();

    void OnFramebufferResizeQueued(Vector2D<int> framebufferSize)
    {
    }

    void OnInputCreated(IInputContext input)
    {
    }
}
