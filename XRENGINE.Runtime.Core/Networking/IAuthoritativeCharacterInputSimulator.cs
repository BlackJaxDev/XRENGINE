using System.Numerics;
using XREngine.Components;

namespace XREngine.Networking;

/// <summary>
/// Optional game-owned fixed-tick locomotion hook. Implementers mutate only
/// server-owned pawn/physics state and report the resulting velocity; network
/// code stamps and replicates the resulting authoritative transform.
/// </summary>
public interface IAuthoritativeCharacterInputSimulator
{
    bool TrySimulateAuthoritativeInput(
        PawnComponent pawn,
        CharacterPawnInputSnapshot input,
        float fixedDeltaSeconds,
        out Vector3 velocity);
}
