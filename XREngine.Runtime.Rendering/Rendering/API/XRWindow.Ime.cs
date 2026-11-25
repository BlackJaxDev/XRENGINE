using System.Numerics;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering;

public sealed partial class XRWindow
{
    /// <summary>Describes composition support of the active native window.</summary>
    public string ImeSupportStatus => _desktopBackend is IRuntimeImeCandidateWindow { IsImeAvailable: true }
        ? "Win32 IMM32 composition"
        : "Native IME composition is unsupported by this window backend.";

    /// <summary>Updates a native IME candidate location when the window supports composition.</summary>
    public bool TrySetImeCandidatePosition(Vector2 clientPosition)
    {
        if (!float.IsFinite(clientPosition.X) || !float.IsFinite(clientPosition.Y) ||
            _desktopBackend is not IRuntimeImeCandidateWindow { IsImeAvailable: true } window)
            return false;
        window.SetImeCandidatePosition(clientPosition);
        return true;
    }

    /// <summary>Freezes the focused text identity for native IME event stamping.</summary>
    public bool TrySetImeFocusTarget(UIElementIdentity identity, long focusEpoch)
    {
        if (_desktopBackend is not IRuntimeImeCandidateWindow { IsImeAvailable: true } window)
            return false;
        window.SetImeFocusTarget(identity, focusEpoch);
        return true;
    }
}
