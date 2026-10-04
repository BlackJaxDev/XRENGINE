// Shared exact SurfaceDetailNormalMapping math. Raster and compute supply the
// same source derivatives, independent authored tangent and bitangent, and sample.
bool authoredTexturedFinite(float3 value)
{
    return !any(isnan(value)) && !any(isinf(value));
}
float3 authoredTexturedMappedNormal(float3 sampledNormal, float3 normalWS,
    float3 tangentWS, float3 bitangentWS, float3 positionDx, float3 positionDy,
    float2 uvDx, float2 uvDy)
{
    float3 N = normalize(normalWS);
    if (!authoredTexturedFinite(N) || dot(N, N) <= 0.0)
        return float3(0.0, 0.0, 1.0);
    float3 T;
    float3 B;
    if (dot(tangentWS, tangentWS) < 1e-10 || dot(bitangentWS, bitangentWS) < 1e-10)
    {
        float determinant = uvDx.x * uvDy.y - uvDx.y * uvDy.x;
        if (abs(determinant) < 1e-10)
            return N;
        T = (positionDx * uvDy.y - positionDy * uvDx.y) / determinant;
        B = (positionDy * uvDx.x - positionDx * uvDy.x) / determinant;
        T -= N * dot(N, T);
        if (!authoredTexturedFinite(T) || !authoredTexturedFinite(B) || dot(T, T) < 1e-10)
            return N;
        T = normalize(T);
        float handedness = dot(cross(N, T), B) < 0.0 ? -1.0 : 1.0;
        B = normalize(cross(N, T)) * handedness;
    }
    else
    {
        T = tangentWS - N * dot(N, tangentWS);
        B = bitangentWS - N * dot(N, bitangentWS);
        if (dot(T, T) < 1e-10 || dot(B, B) < 1e-10)
            return N;
        T = normalize(T);
        B = normalize(B);
        if (!authoredTexturedFinite(T) || !authoredTexturedFinite(B))
            return N;
    }
    if (!authoredTexturedFinite(sampledNormal) || dot(sampledNormal, sampledNormal) <= 1e-6)
        return N;
    float3 tangentNormal = normalize(sampledNormal);
    tangentNormal.z = max(tangentNormal.z, 0.001);
    tangentNormal = normalize(tangentNormal);
    float3 mapped = T * tangentNormal.x + B * tangentNormal.y + N * tangentNormal.z;
    if (!authoredTexturedFinite(mapped) || dot(mapped, mapped) <= 1e-6)
        return N;
    return normalize(mapped);
}
float3 authoredTexturedRgbNormal(float3 sample)
{
    float3 normal = sample * 2.0 - 1.0;
    normal.y = -normal.y;
    return normal;
}
float3 authoredTexturedSobelNormal(float tl, float t, float tr, float l,
    float r, float bl, float b, float br, float scale)
{
    float slopeX = ((tl + 2.0 * l + bl) - (tr + 2.0 * r + br)) * 0.25;
    float slopeY = ((tl + 2.0 * t + tr) - (bl + 2.0 * b + br)) * 0.25;
    return normalize(float3(float2(slopeX, slopeY) * scale, 1.0));
}
