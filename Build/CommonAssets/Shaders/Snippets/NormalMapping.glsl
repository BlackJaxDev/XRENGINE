// Normal Mapping Utilities Snippet

// Decodes a tangent-space normal-map sample to [-1, 1]. A BC5 normal map samples with
// alpha 0 (view swizzle R, G, 0, 0) and stores only X and Y, so Z is rebuilt from them.
// Other samples keep their stored Z.
vec3 XRENGINE_DecodeTangentNormal(vec4 packedNormal)
{
    vec3 normal = packedNormal.xyz * 2.0 - 1.0;
    if (packedNormal.a < 0.5)
        normal.z = sqrt(clamp(1.0 - dot(normal.xy, normal.xy), 0.0, 1.0));
    return normal;
}

vec3 XRENGINE_UnpackNormal(vec4 packedNormal)
{
    vec3 normal = XRENGINE_DecodeTangentNormal(packedNormal);
    normal.y = -normal.y;
    return normal;
}

vec3 XRENGINE_UnpackNormalDXT5nm(vec4 packedNormal)
{
    vec3 normal;
    normal.xy = packedNormal.wy * 2.0 - 1.0;
    normal.y = -normal.y;
    normal.z = sqrt(1.0 - clamp(dot(normal.xy, normal.xy), 0.0, 1.0));
    return normal;
}

mat3 XRENGINE_CalculateTBN(vec3 normal, vec3 tangent, float bitangentSign)
{
    vec3 N = normalize(normal);
    vec3 T = normalize(tangent);
    T = normalize(T - dot(T, N) * N); // Gram-Schmidt orthogonalization
    vec3 B = cross(N, T) * bitangentSign;
    return mat3(T, B, N);
}

vec3 XRENGINE_PerturbNormal(mat3 TBN, vec3 normalMapSample)
{
    vec3 tangentNormal = XRENGINE_UnpackNormal(vec4(normalMapSample, 1.0));
    return normalize(TBN * tangentNormal);
}

// Use this overload with the full texture sample, so a BC5 normal map rebuilds Z.
vec3 XRENGINE_PerturbNormal(mat3 TBN, vec4 normalMapSample)
{
    vec3 tangentNormal = XRENGINE_UnpackNormal(normalMapSample);
    return normalize(TBN * tangentNormal);
}
