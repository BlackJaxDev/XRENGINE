namespace XREngine.Networking;

/// <summary>Why a received state-change frame was rejected before any payload decoding.</summary>
public enum EStateChangeFrameError : byte
{
    None = 0,
    /// <summary>The frame is shorter than its header or than the payload length it declares.</summary>
    Truncated = 1,
    /// <summary>The declared payload exceeds <see cref="StateChangeFrame.MaxPayloadBytes"/>.</summary>
    Oversized = 2,
    /// <summary>The state-change type is not a defined <see cref="EStateChangeType"/>.</summary>
    UnknownType = 3,
    /// <summary>The frame was written by a different realtime wire-protocol version.</summary>
    ProtocolMismatch = 4,
    /// <summary>The frame carries trailing bytes beyond its declared payload.</summary>
    LengthMismatch = 5,
}
