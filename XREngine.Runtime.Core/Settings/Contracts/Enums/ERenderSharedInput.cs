namespace XREngine;

[Flags]
public enum ERenderSharedInput : ushort
{
    None = 0,
    MaterialPublication = 1 << 0,
    ShadowResults = 1 << 1,
    BrdfLut = 1 << 2,
    GpuScene = 1 << 3,
    Visibility = 1 << 4,
    HiZ = 1 << 5,
    ViewConstants = 1 << 6,
}
