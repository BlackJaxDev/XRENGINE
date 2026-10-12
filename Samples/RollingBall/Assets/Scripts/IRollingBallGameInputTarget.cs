using System.Numerics;

namespace RollingBall;

/// <summary>
/// Receives normalized player intent from the cooked Rolling Ball pawn.
/// </summary>
public interface IRollingBallGameInputTarget
{
    void SetTilt(Vector2 tilt);

    void ResetRound();

    void TogglePause();
}
