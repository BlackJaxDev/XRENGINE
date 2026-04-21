using XREngine.Components;

namespace XREngine.Input;

/// <summary>
/// Lower-layer pawn contract used by controller implementations without depending on
/// the concrete PawnComponent type that still lives in a higher assembly.
/// </summary>
public interface IRuntimeInputControllablePawn
{
    IPawnController? Controller { get; set; }
    void RegisterControllerInput(object inputInterface);
}