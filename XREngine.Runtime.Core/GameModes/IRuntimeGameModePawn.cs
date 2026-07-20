using XREngine.Components;
using XREngine.Input;

namespace XREngine;

/// <summary>
/// Minimal pawn surface required by host-independent game-mode possession policy.
/// </summary>
public interface IRuntimeGameModePawn
{
    IPawnController? Controller { get; }
    event Action<XRComponent>? RuntimePreUnpossessed;
}
