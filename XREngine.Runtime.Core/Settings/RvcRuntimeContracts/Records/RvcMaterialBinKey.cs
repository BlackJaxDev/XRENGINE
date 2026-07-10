namespace XREngine;

public readonly record struct RvcMaterialBinKey(
    uint MaterialRowId,
    uint MaterialResourceGeneration,
    ERvcMaterialClass MaterialClass);
