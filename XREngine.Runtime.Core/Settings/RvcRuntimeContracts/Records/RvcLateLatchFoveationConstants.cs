using System.Numerics;

namespace XREngine;

public readonly record struct RvcLateLatchFoveationConstants(
    Vector2 FoveationCenterUv,
    Vector2 GuardBandRadiiUv,
    ulong DeviceAddress,
    bool WrittenAtSubmitTime,
    bool RebuildsCommandBuffers)
{
    public static RvcLateLatchFoveationConstants Disabled => new(
        new Vector2(0.5f, 0.5f),
        Vector2.Zero,
        DeviceAddress: 0UL,
        WrittenAtSubmitTime: false,
        RebuildsCommandBuffers: false);
}
