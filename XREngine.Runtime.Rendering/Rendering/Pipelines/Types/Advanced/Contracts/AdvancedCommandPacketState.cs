namespace XREngine.Rendering;

/// <summary>
/// Complete state observed by command reuse: structural generations plus mutable frame data.
/// </summary>
public readonly record struct AdvancedCommandPacketState(
    AdvancedCommandPacketGeneration CommandPacket,
    AdvancedFrameDataGeneration FrameData);
