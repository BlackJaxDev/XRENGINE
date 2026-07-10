namespace XREngine;

public readonly record struct RenderOutputTargetDescriptor(
    ERenderOutputTargetClass TargetClass,
    ulong StableTargetId,
    ulong TargetGeneration,
    uint DisplayWidth,
    uint DisplayHeight,
    uint InternalWidth,
    uint InternalHeight,
    ulong FormatCompatibilityKey,
    uint SampleCount,
    uint ViewMask,
    int ExternalImageSlot)
{
    public bool IsSpecified => TargetClass != ERenderOutputTargetClass.Unknown;

    public ulong CompatibilityKey
    {
        get
        {
            ulong hash = 1469598103934665603UL;
            Add(ref hash, (ulong)TargetClass);
            Add(ref hash, StableTargetId);
            Add(ref hash, TargetGeneration);
            Add(ref hash, DisplayWidth);
            Add(ref hash, DisplayHeight);
            Add(ref hash, InternalWidth);
            Add(ref hash, InternalHeight);
            Add(ref hash, FormatCompatibilityKey);
            Add(ref hash, SampleCount);
            Add(ref hash, ViewMask);
            Add(ref hash, unchecked((ulong)(ExternalImageSlot + 1)));
            return hash;
        }
    }

    public static RenderOutputTargetDescriptor Unspecified => default;

    private static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
