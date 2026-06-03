#ifndef XR_BVH_STACK_MAX
#define XR_BVH_STACK_MAX 64u
#endif

#ifndef XR_FORCE_ANY_HIT
#define XR_FORCE_ANY_HIT 0u
#endif

#ifndef XR_FORCE_CLOSEST_HIT
#define XR_FORCE_CLOSEST_HIT 0u
#endif

// Shared BVH raycast helpers and buffer contract.
//
// Uniforms expected:
// uniform uint uRayCount;
// uniform uint uRootIndex;
// uniform uint uPacketWidth;
// uniform uint uUsePacketMode;
// uniform uint uAnyHitMode;
// uniform uint uMaxStackDepth;

// Leaf flag lives in BvhNode.flags bit 0 (matches BVH_FLAG_LEAF in bvh_nodes.glslinc
// and GpuBvhLayout.LeafFlag on the engine side). The legacy XR_BVH_LEAF_BIT name is
// retained as an alias so existing shader-source contract tests keep resolving.
const uint XR_BVH_LEAF_FLAG = 1u;
const uint XR_BVH_LEAF_BIT = XR_BVH_LEAF_FLAG;
const uint XR_BVH_INVALID_INDEX = 0xFFFFFFFFu;

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

// Matches XREngine.Rendering.Compute.GpuBvhNode / bvh_nodes.glslinc:
// 80-byte stride (20 uint scalars), vec3 bounds with packed child indices,
// in-node primitive range, and a leaf flag in bit 0 of `flags`.
struct BvhNode
{
    vec3 minBounds;
    uint leftChild;
    vec3 maxBounds;
    uint rightChild;
    uvec2 primitiveRange; // x = first primitive, y = primitive count
    uint parentIndex;
    uint flags;           // bit 0 = leaf
    uvec4 _pad0;
    uvec4 _pad1;
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

layout(std430, binding = 0) readonly buffer Rays
{
    RayInput gRays[];
};

// The node SSBO is the raw GpuBvhTree buffer: a 4-scalar header precedes the
// node array. Declaring the header here keeps gNodes[] correctly aligned to the
// first real node and exposes the build-provided root index.
layout(std430, binding = 1) readonly buffer Nodes
{
    uint gNodeCount;
    uint gRootIndex;
    uint gNodeStrideScalars;
    uint gMaxLeafPrimitives;
    BvhNode gNodes[];
};

layout(std430, binding = 2) readonly buffer Triangles
{
    PackedTriangle gTriangles[];
};

layout(std430, binding = 3) writeonly buffer Hits
{
    HitRecord gHits[];
};

Ray DecodeRay(RayInput rayInput)
{
    Ray r;
    r.origin = rayInput.origin.xyz;
    r.tMin = rayInput.origin.w;
    r.direction = rayInput.direction.xyz;
    r.tMax = rayInput.direction.w;
    return r;
}

bool IntersectAabb(in Ray ray, in vec3 invDir, in BvhNode node, out float tEnter)
{
    vec3 t0 = (node.minBounds - ray.origin) * invDir;
    vec3 t1 = (node.maxBounds - ray.origin) * invDir;
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

    // Prefer the build-provided root from the node header; the caller's rootIndex
    // is only used as a fallback when the header has not been populated.
    uint nodeCount = gNodeCount;
    uint triCount = uint(gTriangles.length());
    uint start = (gRootIndex != XR_BVH_INVALID_INDEX) ? gRootIndex : rootIndex;
    if (nodeCount == 0u || start >= nodeCount)
        return hit;

    uint stack[XR_BVH_STACK_MAX];
    uint stackPtr = 0u;
    stack[stackPtr++] = start;

    while (stackPtr > 0u)
    {
        uint nodeIndex = stack[--stackPtr];
        if (nodeIndex >= nodeCount)
            continue;
        BvhNode node = gNodes[nodeIndex];

        float boxT;
        if (!IntersectAabb(ray, invDir, node, boxT) || boxT > hit.t)
            continue;

        bool isLeaf = (node.flags & XR_BVH_LEAF_FLAG) != 0u;
        if (isLeaf)
        {
            uint first = node.primitiveRange.x;
            uint count = node.primitiveRange.y;
            // Clamp the primitive range to the triangle buffer so a malformed
            // leaf (or a node misread as a leaf) cannot spin the inner loop over
            // billions of out-of-range entries and hang the GPU.
            if (first >= triCount)
                continue;
            count = min(count, triCount - first);
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
            uint left = node.leftChild;
            uint right = node.rightChild;

            if (stackPtr + 2u <= maxStackDepth && stackPtr + 2u <= XR_BVH_STACK_MAX)
            {
                if (left != XR_BVH_INVALID_INDEX && left < nodeCount)
                    stack[stackPtr++] = left;
                if (right != XR_BVH_INVALID_INDEX && right < nodeCount)
                    stack[stackPtr++] = right;
            }
        }
    }

    return hit;
}
