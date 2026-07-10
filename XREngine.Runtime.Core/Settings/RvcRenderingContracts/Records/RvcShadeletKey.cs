namespace XREngine;

public readonly record struct RvcShadeletKey(ulong A, ulong B)
{
    public static RvcShadeletKey Create(
        in RvcVisibilityPayload visibility,
        ERvcMaterialClass materialClass,
        in RvcSurfaceKey surface,
        uint materialRowId,
        uint deformationVersion)
    {
        ulong b =
            ((ulong)materialRowId & 0xFFFFFu) |
            (((ulong)surface.QuantizedU & 0x3FFFu) << 20) |
            (((ulong)surface.QuantizedV & 0x3FFFu) << 34) |
            (((ulong)surface.LodBucket & 0xFFu) << 48) |
            (((ulong)surface.RoughnessBucket & 0xFFu) << 56);

        ulong classRegionDeform =
            (((ulong)materialClass & 0xFu) << 4) |
            (((ulong)surface.Region & 0xFu)) |
            (((ulong)deformationVersion & 0xFFFFu) << 8);

        return new RvcShadeletKey(visibility.Packed ^ (classRegionDeform << 40), b);
    }
}
