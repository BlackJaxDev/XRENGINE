namespace XREngine;

public readonly record struct RvcSurfaceKey(
    ushort QuantizedU,
    ushort QuantizedV,
    ushort RoughnessBucket,
    byte LodBucket,
    ERvcFoveationRegion Region);
