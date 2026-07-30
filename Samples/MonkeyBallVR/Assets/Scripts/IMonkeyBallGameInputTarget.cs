using System.Numerics;

namespace MonkeyBallVR;

/// <summary>
/// Receives normalized player intent from the cooked MonkeyBall pawn.
/// </summary>
public interface IMonkeyBallGameInputTarget
{
    void SetTilt(Vector2 tilt);

    void ResetRound();

    void TogglePause();
}
