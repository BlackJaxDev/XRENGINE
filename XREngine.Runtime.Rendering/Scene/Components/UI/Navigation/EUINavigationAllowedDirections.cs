using System;

namespace XREngine.Rendering.UI;

/// <summary>
/// Bitmask of directional inputs that a <see cref="UINavigationFlowMap"/> accepts.
/// </summary>
[Flags]
public enum EUINavigationAllowedDirections
{
    None = 0,
    Up = 1 << 0,
    Down = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    UpLeft = 1 << 4,
    UpRight = 1 << 5,
    DownLeft = 1 << 6,
    DownRight = 1 << 7,
    Vertical = Up | Down,
    Horizontal = Left | Right,
    Cardinal = Vertical | Horizontal,
    Diagonals = UpLeft | UpRight | DownLeft | DownRight,
    All = Cardinal | Diagonals,
}
