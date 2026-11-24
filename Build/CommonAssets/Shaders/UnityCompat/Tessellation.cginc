// Engine-authored compatibility include for translated Unity programs.
// It gives the tessellation factor functions of Unity's Tessellation.cginc.
// The engine has no unity_CameraWorldClipPlanes, so the frustum test gets the side planes
// from the view-projection matrix.
#ifndef TESSELLATION_CGINC_INCLUDED
#define TESSELLATION_CGINC_INCLUDED

#include "UnityShaderVariables.cginc"

// Returns the signed distance from a point to a plane. The plane normal must have unit length.
float UnityDistanceFromPlane(float3 pos, float4 plane)
{
    return dot(float4(pos, 1.0), plane);
}

// Returns true when the triangle is fully outside one side plane of the view frustum.
// cullEps moves the planes outward by that world distance.
bool UnityWorldViewFrustumCull(float3 wpos0, float3 wpos1, float3 wpos2, float cullEps)
{
    float4x4 viewProjection = UNITY_MATRIX_VP;
    float4 planes[4];
    planes[0] = viewProjection[3] + viewProjection[0];
    planes[1] = viewProjection[3] - viewProjection[0];
    planes[2] = viewProjection[3] + viewProjection[1];
    planes[3] = viewProjection[3] - viewProjection[1];

    [unroll]
    for (int index = 0; index < 4; index++)
    {
        float4 plane = planes[index] / max(length(planes[index].xyz), 1e-6);
        if (UnityDistanceFromPlane(wpos0, plane) < -cullEps &&
            UnityDistanceFromPlane(wpos1, plane) < -cullEps &&
            UnityDistanceFromPlane(wpos2, plane) < -cullEps)
            return true;
    }

    return false;
}

// Converts three per-vertex factors to the edge factors and the inside factor of a triangle.
// Each edge uses the mean of its two end vertices.
float4 UnityCalcTriEdgeTessFactors(float3 triVertexFactors)
{
    float4 tess;
    tess.x = 0.5 * (triVertexFactors.y + triVertexFactors.z);
    tess.y = 0.5 * (triVertexFactors.x + triVertexFactors.z);
    tess.z = 0.5 * (triVertexFactors.x + triVertexFactors.y);
    tess.w = (triVertexFactors.x + triVertexFactors.y + triVertexFactors.z) / 3.0;
    return tess;
}

// Returns a factor that falls from tess at minDist to 1% of tess at maxDist from the camera.
float UnityCalcDistanceTessFactor(float4 vertex, float minDist, float maxDist, float tess)
{
    float3 wpos = mul(unity_ObjectToWorld, vertex).xyz;
    float dist = distance(wpos, _WorldSpaceCameraPos);
    return clamp(1.0 - (dist - minDist) / (maxDist - minDist), 0.01, 1.0) * tess;
}

// Returns the triangle factors for tessellation that decreases with the camera distance.
float4 UnityDistanceBasedTess(float4 v0, float4 v1, float4 v2, float minDist, float maxDist, float tess)
{
    float3 factors;
    factors.x = UnityCalcDistanceTessFactor(v0, minDist, maxDist, tess);
    factors.y = UnityCalcDistanceTessFactor(v1, minDist, maxDist, tess);
    factors.z = UnityCalcDistanceTessFactor(v2, minDist, maxDist, tess);
    return UnityCalcTriEdgeTessFactors(factors);
}

// Returns the factor that divides an edge into parts of about edgeLen screen pixels.
float UnityCalcEdgeTessFactor(float3 wpos0, float3 wpos1, float edgeLen)
{
    float dist = distance(0.5 * (wpos0 + wpos1), _WorldSpaceCameraPos);
    float len = distance(wpos0, wpos1);
    return max(len * _ScreenParams.y / (edgeLen * dist), 1.0);
}

// Returns the triangle factors for edges of about edgeLength screen pixels.
float4 UnityEdgeLengthBasedTess(float4 v0, float4 v1, float4 v2, float edgeLength)
{
    float3 pos0 = mul(unity_ObjectToWorld, v0).xyz;
    float3 pos1 = mul(unity_ObjectToWorld, v1).xyz;
    float3 pos2 = mul(unity_ObjectToWorld, v2).xyz;
    float4 tess;
    tess.x = UnityCalcEdgeTessFactor(pos1, pos2, edgeLength);
    tess.y = UnityCalcEdgeTessFactor(pos2, pos0, edgeLength);
    tess.z = UnityCalcEdgeTessFactor(pos0, pos1, edgeLength);
    tess.w = (tess.x + tess.y + tess.z) / 3.0;
    return tess;
}

// Returns the factors of UnityEdgeLengthBasedTess, or zero for a triangle outside the view frustum.
// maxDisplacement is the largest world distance that displacement can move a vertex.
float4 UnityEdgeLengthBasedTessCull(float4 v0, float4 v1, float4 v2, float edgeLength, float maxDisplacement)
{
    float3 pos0 = mul(unity_ObjectToWorld, v0).xyz;
    float3 pos1 = mul(unity_ObjectToWorld, v1).xyz;
    float3 pos2 = mul(unity_ObjectToWorld, v2).xyz;
    if (UnityWorldViewFrustumCull(pos0, pos1, pos2, maxDisplacement))
        return float4(0.0, 0.0, 0.0, 0.0);

    float4 tess;
    tess.x = UnityCalcEdgeTessFactor(pos1, pos2, edgeLength);
    tess.y = UnityCalcEdgeTessFactor(pos2, pos0, edgeLength);
    tess.z = UnityCalcEdgeTessFactor(pos0, pos1, edgeLength);
    tess.w = (tess.x + tess.y + tess.z) / 3.0;
    return tess;
}

#endif
