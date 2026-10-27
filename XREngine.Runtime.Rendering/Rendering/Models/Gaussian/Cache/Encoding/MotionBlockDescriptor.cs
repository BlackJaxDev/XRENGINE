namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Describes one block of motion residuals. The frames are the frames of one temporal level, in ascending order, from FirstFrame.</summary>
public readonly record struct MotionBlockDescriptor(
    byte DriverLevel,
    byte TemporalLevel,
    int FirstVertex,
    int VertexCount,
    int FirstFrame,
    int FrameCount,
    bool WideResiduals);
