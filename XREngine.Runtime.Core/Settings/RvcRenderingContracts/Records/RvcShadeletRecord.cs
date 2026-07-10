namespace XREngine;

public readonly record struct RvcShadeletRecord(
    RvcShadeletKey Key,
    uint MaterialRowId,
    uint MaterialResourceGeneration,
    ERvcMaterialClass MaterialClass,
    ERvcShadeletDensity Density,
    bool AllowsStereoReuse,
    bool RequiresPerViewSpecular,
    byte RoughnessBucket);
