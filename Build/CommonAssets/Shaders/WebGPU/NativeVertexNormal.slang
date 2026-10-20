// Canonical octahedral normal boundary shared by local-function raster and compute wrappers.
float2 nativeSignNotZero(float2 value)
{
    return float2(value.x >= 0.0 ? 1.0 : -1.0, value.y >= 0.0 ? 1.0 : -1.0);
}

float3 nativeNormalizeNormal(float3 value)
{
    float lengthSquared = dot(value, value);
    return all(isfinite(value)) && lengthSquared > 1e-20 ? value * rsqrt(lengthSquared) : float3(0.0, 1.0, 0.0);
}

float3 nativeDecodeNormal(uint packed)
{
    float2 value = float2(float(int(packed << 16u) >> 16u), float(int(packed) >> 16u)) / 32767.0;
    float3 normal = float3(value, 1.0 - abs(value.x) - abs(value.y));
    if (normal.z < 0.0) normal.xy = (1.0 - abs(normal.yx)) * nativeSignNotZero(normal.xy);
    return nativeNormalizeNormal(normal);
}

uint nativeEncodeNormal(float3 normal)
{
    // Match AdvancedPackedVertexCodec.Pack followed by EncodeOct, including
    // the UnitY fallback for zero, near-zero and nonfinite source normals.
    normal = nativeNormalizeNormal(nativeNormalizeNormal(normal));
    float inverseL1 = 1.0 / (abs(normal.x) + abs(normal.y) + abs(normal.z));
    float3 unit = normal * inverseL1;
    float2 encoded = unit.z >= 0.0 ? unit.xy : (1.0 - abs(unit.yx)) * nativeSignNotZero(unit.xy);
    int2 quantized = int2(round(clamp(encoded, -1.0, 1.0) * 32767.0));
    return (uint(quantized.x) & 0xffffu) | ((uint(quantized.y) & 0xffffu) << 16u);
}

