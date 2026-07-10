namespace XREngine;

public readonly record struct RvcMaterialResourceRow(
    uint MaterialRowId,
    uint ResourceGeneration,
    uint BaseColorResourceIndex,
    uint NormalResourceIndex,
    uint RoughnessMetallicResourceIndex,
    uint SamplerIndex)
{
    public bool IsSameResourceGeneration(in RvcShadeletRecord shadelet)
        => shadelet.MaterialRowId == MaterialRowId &&
           shadelet.MaterialResourceGeneration == ResourceGeneration;
}
