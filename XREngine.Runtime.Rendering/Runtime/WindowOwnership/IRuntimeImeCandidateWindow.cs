using System.Numerics;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering;

/// <summary>Optional desktop text-service capability; coordinates are client pixels.</summary>
public interface IRuntimeImeCandidateWindow
{
    bool IsImeAvailable { get; }
    void SetImeCandidatePosition(Vector2 clientPosition);
    void SetImeFocusTarget(UIElementIdentity identity, long focusEpoch);
}
