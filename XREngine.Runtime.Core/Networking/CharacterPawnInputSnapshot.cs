using System.Numerics;
using MemoryPack;

namespace XREngine.Networking;

/// <summary>
/// Network-serializable input state captured from a character pawn.
/// </summary>
[MemoryPackable]
public sealed partial class CharacterPawnInputSnapshot : IPawnInputSnapshot
{
    public Vector2 Movement { get; set; }
    public Vector2 ViewAngles { get; set; }
    public bool JumpPressed { get; set; }
    public bool JumpHeld { get; set; }
    public bool ToggleCrouch { get; set; }
    public bool ToggleProne { get; set; }
}
