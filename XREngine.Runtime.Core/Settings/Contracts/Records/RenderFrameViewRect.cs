namespace XREngine;

public readonly record struct RenderFrameViewRect(
    uint X,
    uint Y,
    uint Width,
    uint Height)
{
    public bool IsValid => Width != 0u && Height != 0u;

    public static RenderFrameViewRect FromSize(uint width, uint height)
        => new(0u, 0u, width, height);
}
