using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;

namespace XREngine.Input;

/// <summary>
/// Host operations required by <see cref="PawnComponent"/> for concrete input, viewport,
/// camera, and UI integration.
/// </summary>
public interface IRuntimePawnHostServices
{
    object? GetLocalInput(PawnComponent pawn);
    object? GetGamepad(PawnComponent pawn);
    object? GetKeyboard(PawnComponent pawn);
    object? GetMouse(PawnComponent pawn);
    Vector2 GetCursorPositionScreen(PawnComponent pawn);
    Vector2 GetCursorPositionViewport(PawnComponent pawn);
    Vector2 GetCursorPositionInternalCoordinates(PawnComponent pawn);
    Segment GetCursorPositionWorld(PawnComponent pawn);
    void RegisterControllerInput(PawnComponent pawn, object inputInterface);
    void RegisterOptionalInputs(PawnComponent pawn, object inputInterface);
    XRComponent? ResolveCamera(PawnComponent pawn, XRComponent? configuredCamera);
}

/// <summary>Runtime accessor for the composed pawn host.</summary>
public static class RuntimePawnHostServices
{
    public static IRuntimePawnHostServices? Current { get; set; }
}
