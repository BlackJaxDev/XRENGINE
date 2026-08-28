using XREngine.Input;
using XREngine.Rendering;

namespace XREngine.Runtime.InputIntegration;

/// <summary>
/// Preserves all rendering viewport bindings while a local controller is
/// replaced. The registry owns the operation; Rendering only exposes the
/// window-local rebind primitive needed to maintain its bidirectional links.
/// </summary>
internal static class InputIntegrationViewportBindingRebinder
{
    internal static XRWindow[] SnapshotWindowsBoundTo(IPawnController controller)
        => [.. RuntimeEngine.Windows.Where(window => window.Viewports.Any(viewport => ReferenceEquals(viewport.AssociatedPlayer, controller)))];

    internal static void Rebind(XRWindow[] windows, IPawnController previousController, IPawnController replacementController)
    {
        foreach (XRWindow window in windows)
            window.RebindController(previousController, replacementController);
    }
}
