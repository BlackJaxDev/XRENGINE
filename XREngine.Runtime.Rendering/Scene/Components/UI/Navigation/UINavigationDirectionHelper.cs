using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Provides direction math, mask conversion, and opposite-direction lookups for UI navigation.
/// </summary>
public static class UINavigationDirectionHelper
{
    private const float DiagonalUnit = 0.70710678f;

    /// <summary>
    /// Returns the opposite navigation direction, or <see cref="EUINavigationDirection.None"/> when none applies.
    /// </summary>
    public static EUINavigationDirection GetOpposite(EUINavigationDirection direction)
        => direction switch
        {
            EUINavigationDirection.Up => EUINavigationDirection.Down,
            EUINavigationDirection.Down => EUINavigationDirection.Up,
            EUINavigationDirection.Left => EUINavigationDirection.Right,
            EUINavigationDirection.Right => EUINavigationDirection.Left,
            EUINavigationDirection.UpLeft => EUINavigationDirection.DownRight,
            EUINavigationDirection.UpRight => EUINavigationDirection.DownLeft,
            EUINavigationDirection.DownLeft => EUINavigationDirection.UpRight,
            EUINavigationDirection.DownRight => EUINavigationDirection.UpLeft,
            _ => EUINavigationDirection.None,
        };

    /// <summary>
    /// Converts a navigation direction to its corresponding <see cref="EUINavigationAllowedDirections"/> flag.
    /// </summary>
    public static EUINavigationAllowedDirections ToFlag(EUINavigationDirection direction)
        => direction switch
        {
            EUINavigationDirection.Up => EUINavigationAllowedDirections.Up,
            EUINavigationDirection.Down => EUINavigationAllowedDirections.Down,
            EUINavigationDirection.Left => EUINavigationAllowedDirections.Left,
            EUINavigationDirection.Right => EUINavigationAllowedDirections.Right,
            EUINavigationDirection.UpLeft => EUINavigationAllowedDirections.UpLeft,
            EUINavigationDirection.UpRight => EUINavigationAllowedDirections.UpRight,
            EUINavigationDirection.DownLeft => EUINavigationAllowedDirections.DownLeft,
            EUINavigationDirection.DownRight => EUINavigationAllowedDirections.DownRight,
            _ => EUINavigationAllowedDirections.None,
        };

    /// <summary>
    /// Returns true when <paramref name="direction"/> is permitted by <paramref name="allowed"/>.
    /// </summary>
    public static bool IsAllowed(EUINavigationDirection direction, EUINavigationAllowedDirections allowed)
    {
        EUINavigationAllowedDirections flag = ToFlag(direction);
        return flag != EUINavigationAllowedDirections.None && (allowed & flag) == flag;
    }

    /// <summary>
    /// Returns true when <paramref name="direction"/> is a diagonal direction.
    /// </summary>
    public static bool IsDiagonal(EUINavigationDirection direction)
        => direction is EUINavigationDirection.UpLeft
            or EUINavigationDirection.UpRight
            or EUINavigationDirection.DownLeft
            or EUINavigationDirection.DownRight;

    /// <summary>
    /// Returns the unit vector in UI canvas coordinates (+X right, +Y up) for <paramref name="direction"/>.
    /// </summary>
    public static Vector2 ToCanvasVector(EUINavigationDirection direction)
        => direction switch
        {
            EUINavigationDirection.Up => new Vector2(0.0f, 1.0f),
            EUINavigationDirection.Down => new Vector2(0.0f, -1.0f),
            EUINavigationDirection.Left => new Vector2(-1.0f, 0.0f),
            EUINavigationDirection.Right => new Vector2(1.0f, 0.0f),
            EUINavigationDirection.UpLeft => new Vector2(-DiagonalUnit, DiagonalUnit),
            EUINavigationDirection.UpRight => new Vector2(DiagonalUnit, DiagonalUnit),
            EUINavigationDirection.DownLeft => new Vector2(-DiagonalUnit, -DiagonalUnit),
            EUINavigationDirection.DownRight => new Vector2(DiagonalUnit, -DiagonalUnit),
            _ => Vector2.Zero,
        };
}
