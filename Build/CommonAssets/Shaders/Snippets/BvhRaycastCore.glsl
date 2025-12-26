#ifndef XR_BVH_STACK_MAX
#define XR_BVH_STACK_MAX 64u
#endif

#ifndef XR_FORCE_ANY_HIT
#define XR_FORCE_ANY_HIT 0u
#endif

#ifndef XR_FORCE_CLOSEST_HIT
#define XR_FORCE_CLOSEST_HIT 0u
#endif

// Shared BVH raycast helpers. Consumers must declare the following resources:
// layout(std430, binding = 0) buffer Rays      { RayInput gRays[];      };
// layout(std430, binding = 1) buffer Nodes     { BvhNode  gNodes[];     };
// layout(std430, binding = 2) buffer Triangles { PackedTriangle gTriangles[]; };
// layout(std430, binding = 3) buffer Hits      { HitRecord gHits[];     };
//
// Uniforms expected:
// uniform uint uRayCount;
// uniform uint uRootIndex;
// uniform uint uPacketWidth;
// uniform uint uUsePacketMode;
// uniform uint uAnyHitMode;
// uniform uint uMaxStackDepth;

const uint XR_BVH_LEAF_BIT = 0x80000000u;

struct RayInput
{
    vec4 origin;    // .w = tMin
    vec4 direction; // .w = tMax
};

struct Ray
{
    vec3 origin;
    float tMin;
    vec3 direction;
    float tMax;
};

struct BvhNode
{
    vec4 minBounds;
    vec4 maxBounds;
    uvec4 meta; // x = left child or first primitive, y = right child or primitive count, z = first primitive, w = flags/count (bit31 = leaf)
};

struct PackedTriangle
{
    vec4 v0;
    vec4 v1;
    vec4 v2;
    uvec4 extra; // x = object id, y = face index, z/w reserved
};

struct HitRecord
{
    float t;
    uint objectId;
    uint faceIndex;
    uint triangleIndex;
    vec3 barycentric;
    float padding;
};

Ray DecodeRay(RayInput input)
{
    Ray r;
    r.origin = input.origin.xyz;
    r.tMin = input.origin.w;
    r.direction = input.direction.xyz;
    r.tMax = input.direction.w;
    return r;
}

bool IntersectAabb(in Ray ray, in vec3 invDir, in BvhNode node, out float tEnter)
{
    vec3 t0 = (node.minBounds.xyz - ray.origin) * invDir;
    vec3 t1 = (node.maxBounds.xyz - ray.origin) * invDir;
    vec3 tMinVec = min(t0, t1);
    vec3 tMaxVec = max(t0, t1);

    float tmin = max(max(tMinVec.x, tMinVec.y), max(tMinVec.z, ray.tMin));
    float tmax = min(min(tMaxVec.x, tMaxVec.y), min(tMaxVec.z, ray.tMax));

    tEnter = tmin;
    return tmax >= tmin;
}

bool IntersectTriangle(in Ray ray, in PackedTriangle tri, out float t, out vec3 bary)
{
    const float epsilon = 1e-6;
    vec3 edge1 = tri.v1.xyz - tri.v0.xyz;
    vec3 edge2 = tri.v2.xyz - tri.v0.xyz;
    vec3 pvec = cross(ray.direction, edge2);
    float det = dot(edge1, pvec);

    if (abs(det) < epsilon)
        return false;

    float invDet = 1.0 / det;
    vec3 tvec = ray.origin - tri.v0.xyz;
    float u = dot(tvec, pvec) * invDet;
    if (u < 0.0 || u > 1.0)
        return false;

    vec3 qvec = cross(tvec, edge1);
    float v = dot(ray.direction, qvec) * invDet;
    if (v < 0.0 || u + v > 1.0)
        return false;

    float hitT = dot(edge2, qvec) * invDet;
    if (hitT < ray.tMin || hitT > ray.tMax)
        return false;

    t = hitT;
    bary = vec3(1.0 - u - v, u, v);
    return true;
}

HitRecord MakeMiss(in Ray ray)
{
    HitRecord miss;
    miss.t = ray.tMax;
    miss.objectId = 0u;
    miss.faceIndex = 0u;
    miss.triangleIndex = uint(-1);
    miss.barycentric = vec3(0.0);
    miss.padding = 0.0;
    return miss;
}

HitRecord TraceRay(in Ray ray, uint rootIndex, uint maxStackDepth, bool anyHit)
{
    vec3 invDir = 1.0 / ray.direction;
    HitRecord hit = MakeMiss(ray);

    uint stack[XR_BVH_STACK_MAX];
    uint stackPtr = 0u;
    stack[stackPtr++] = rootIndex;

    while (stackPtr > 0u)
    {
        uint nodeIndex = stack[--stackPtr];
        BvhNode node = gNodes[nodeIndex];

        float boxT;
        if (!IntersectAabb(ray, invDir, node, boxT) || boxT > hit.t)
            continue;

        bool isLeaf = (node.meta.w & XR_BVH_LEAF_BIT) != 0u;
        if (isLeaf)
        {
            uint first = node.meta.z;
            uint count = node.meta.w & ~XR_BVH_LEAF_BIT;
            for (uint i = 0u; i < count; ++i)
            {
                float triT;
                vec3 bary;
                PackedTriangle tri = gTriangles[first + i];
                if (!IntersectTriangle(ray, tri, triT, bary))
                    continue;

                if (triT >= hit.t || triT < ray.tMin)
                    continue;

                hit.t = triT;
                hit.objectId = tri.extra.x;
                hit.faceIndex = tri.extra.y;
                hit.triangleIndex = first + i;
                hit.barycentric = bary;

                if (anyHit)
                    return hit;
            }
        }
        else
        {
            uint left = node.meta.x;
            uint right = node.meta.y;

            if (stackPtr + 2u <= maxStackDepth && stackPtr + 2u <= XR_BVH_STACK_MAX)
            {
                stack[stackPtr++] = left;
                stack[stackPtr++] = right;
            }
        }
    }

    return hit;
}
