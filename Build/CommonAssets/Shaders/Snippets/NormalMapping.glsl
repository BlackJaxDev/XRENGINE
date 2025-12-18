// Normal Mapping Utilities Snippet

vec3 XRENGINE_UnpackNormal(vec4 packedNormal)
{
    return packedNormal.xyz * 2.0 - 1.0;
}

vec3 XRENGINE_UnpackNormalDXT5nm(vec4 packedNormal)
{
    vec3 normal;
    normal.xy = packedNormal.wy * 2.0 - 1.0;
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
