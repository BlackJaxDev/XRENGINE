#ifndef XR_DDGI_SHADOWS_INCLUDED
#define XR_DDGI_SHADOWS_INCLUDED

const uint XR_DDGI_INVALID_INDEX = 0xffffffffu;
const uint XR_DDGI_BVH_LEAF_FLAG = 1u;
const uint XR_DDGI_TRIANGLE_CASTS_SHADOWS = 1u;
const uint XR_DDGI_SHADOW_STACK_MAX = 64u;
const uint XR_DDGI_TRANSMISSION_LAYERS = 16u;

struct DDGIBvhNode
{
    vec3 minBounds;
    uint leftChild;
    vec3 maxBounds;
    uint rightChild;
    uvec2 primitiveRange;
    uint parentIndex;
    uint flags;
};

layout(std430, XR_DDGI_STORAGE_BINDING(6)) readonly buffer GeometryNodes
{
    uint gGeometryNodeCount;
    uint gGeometryRootIndex;
    uint gGeometryNodeStride;
    uint gGeometryMaxLeafPrimitives;
    DDGIBvhNode gGeometryNodes[];
};

bool DDGIIntersectAabb(vec3 origin, vec3 inverseDirection, float tMin, float tMax, DDGIBvhNode node)
{
    vec3 t0 = (node.minBounds - origin) * inverseDirection;
    vec3 t1 = (node.maxBounds - origin) * inverseDirection;
    vec3 nearT = min(t0, t1);
    vec3 farT = max(t0, t1);
    return min(min(farT.x, farT.y), min(farT.z, tMax)) >= max(max(nearT.x, nearT.y), max(nearT.z, tMin));
}

bool DDGIIntersectTriangle(vec3 origin, vec3 direction, float tMin, float tMax, PackedTriangle triangle,
    out float t, out vec3 barycentric)
{
    vec3 edge1 = triangle.v1.xyz - triangle.v0.xyz;
    vec3 edge2 = triangle.v2.xyz - triangle.v0.xyz;
    vec3 p = cross(direction, edge2);
    float determinant = dot(edge1, p);
    if (abs(determinant) < 1e-6)
        return false;
    float inverseDeterminant = 1.0 / determinant;
    vec3 delta = origin - triangle.v0.xyz;
    float u = dot(delta, p) * inverseDeterminant;
    vec3 q = cross(delta, edge1);
    float v = dot(direction, q) * inverseDeterminant;
    t = dot(edge2, q) * inverseDeterminant;
    barycentric = vec3(1.0 - u - v, u, v);
    return u >= 0.0 && v >= 0.0 && u + v <= 1.0 && t >= tMin && t < tMax;
}

void DDGITraceRange(vec3 origin, vec3 direction, float tMin, uint first, uint count,
    uint triangleCount, bool shadowsOnly, inout HitRecord hit)
{
    if (first >= triangleCount)
        return;
    count = min(count, triangleCount - first);
    for (uint i = 0u; i < count; i++)
    {
        uint index = first + i;
        PackedTriangle triangle = gTriangles[index];
        if (shadowsOnly && (triangle.extra.y & XR_DDGI_TRIANGLE_CASTS_SHADOWS) == 0u)
            continue;
        float distance;
        vec3 barycentric;
        if (!DDGIIntersectTriangle(origin, direction, tMin, hit.t, triangle, distance, barycentric) ||
            !DDGIAcceptSurface(triangle, barycentric))
            continue;
        hit.t = distance;
        hit.objectId = triangle.extra.x;
        hit.faceIndex = triangle.extra.y;
        hit.triangleIndex = index;
        hit.barycentric = barycentric;
    }
}

HitRecord DDGITraceSurface(vec3 origin, vec3 direction, float tMin, float tMax, bool shadowsOnly)
{
    HitRecord hit;
    hit.t = tMax;
    hit.objectId = XR_DDGI_INVALID_INDEX;
    hit.faceIndex = XR_DDGI_INVALID_INDEX;
    hit.triangleIndex = XR_DDGI_INVALID_INDEX;
    hit.barycentric = vec3(0.0);
    hit.padding = 0.0;
    uint nodeCount = min(uNodeCount, min(gGeometryNodeCount, uint(gGeometryNodes.length())));
    uint triangleCount = min(uTriangleCount, uint(gTriangles.length()));
    uint root = gGeometryRootIndex != XR_DDGI_INVALID_INDEX ? gGeometryRootIndex : uRootIndex;
    if (nodeCount == 0u || triangleCount == 0u || root >= nodeCount)
        return hit;
    vec3 inverseDirection = mix(vec3(-1.0), vec3(1.0), greaterThanEqual(direction, vec3(0.0))) / max(abs(direction), vec3(1e-20));
    uint stack[XR_DDGI_SHADOW_STACK_MAX];
    uint count = 1u;
    stack[0] = root;
    // A valid tree visits each node at most once. Bound traversal even if a
    // malformed tree contains a cycle, and conservatively close it below.
    uint visited = 0u;
    while (count > 0u && visited++ < nodeCount)
    {
        uint index = stack[--count];
        if (index >= nodeCount)
            continue;
        DDGIBvhNode node = gGeometryNodes[index];
        if (!DDGIIntersectAabb(origin, inverseDirection, tMin, hit.t, node))
            continue;
        if ((node.flags & XR_DDGI_BVH_LEAF_FLAG) != 0u)
        {
            DDGITraceRange(origin, direction, tMin, node.primitiveRange.x, node.primitiveRange.y, triangleCount, shadowsOnly, hit);
            continue;
        }
        bool left = node.leftChild != XR_DDGI_INVALID_INDEX && node.leftChild < nodeCount;
        bool right = node.rightChild != XR_DDGI_INVALID_INDEX && node.rightChild < nodeCount;
        if (count + uint(left) + uint(right) > XR_DDGI_SHADOW_STACK_MAX)
        {
            // Interior primitive ranges cover the whole subtree. Evaluate those
            // primitives directly instead of dropping occluders at stack capacity.
            DDGITraceRange(origin, direction, tMin, node.primitiveRange.x, node.primitiveRange.y, triangleCount, shadowsOnly, hit);
            continue;
        }
        if (left)
            stack[count++] = node.leftChild;
        if (right)
            stack[count++] = node.rightChild;
    }
    if (count > 0u)
        DDGITraceRange(origin, direction, tMin, 0u, triangleCount, triangleCount, shadowsOnly, hit);
    return hit;
}

vec3 DDGIShadowTransmittance(vec3 origin, vec3 direction, float tMin, float tMax)
{
    vec3 throughput = vec3(1.0);
    for (uint layer = 0u; layer < XR_DDGI_TRANSMISSION_LAYERS; layer++)
    {
        HitRecord hit = DDGITraceSurface(origin, direction, tMin, tMax, true);
        if (hit.triangleIndex == XR_DDGI_INVALID_INDEX)
            return throughput;
        PackedTriangle triangle = gTriangles[hit.triangleIndex];
        DDGIMaterial material = gMaterials[hit.objectId];
        vec4 uv01 = DDGIHitUvs(triangle, hit.barycentric);
        throughput *= DDGITransmission(material, uv01, DDGICoverage(material, uv01));
        if (max(max(throughput.r, throughput.g), throughput.b) < 0.001)
            return vec3(0.0);
        tMin = hit.t + max(0.001, abs(hit.t) * 1e-5);
    }
    return vec3(0.0);
}

vec3 DDGISunTransmittance(vec3 origin, vec3 direction, float tMin)
{
    return DDGIShadowTransmittance(origin, direction, tMin, 1e20);
}

#endif
