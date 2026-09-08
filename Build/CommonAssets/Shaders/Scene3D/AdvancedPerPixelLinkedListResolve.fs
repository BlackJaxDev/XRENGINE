#version 460

#pragma snippet "ScreenSpaceUtils"

layout(location = 0) out vec4 OutColor;
layout(location = 1) out float OutFragmentCount;

uniform usampler2D PpllHeadPointerTex;
uniform uint PpllMaxNodes;
uniform int PpllResolveFragmentLimit;
uniform bool PpllReversedDepth;

struct XRE_PpllNode
{
    vec4 Color;
    float Depth;
    uint Next;
    uint _Pad0;
    uint _Pad1;
};

layout(std430, binding = 24) readonly buffer PpllNodeBuffer
{
    XRE_PpllNode PpllNodes[];
};

layout(std430, binding = 25) buffer PpllCounterBuffer
{
    uint PpllEmittedNodeCount;
    uint PpllOverflowFragmentCount;
    uint PpllRejectedPixelCount;
    uint PpllStatusFlags;
};

const uint XRE_PPLL_INVALID_NODE = 0xFFFFFFFFu;
const uint XRE_PPLL_STATUS_INVALID_LINK = 1u << 2;
const uint XRE_PPLL_STATUS_TRAVERSAL_LIMIT = 1u << 3;
const uint XRE_PPLL_STATUS_FRAGMENT_LIMIT = 1u << 4;
const int XRE_PPLL_FRAGMENT_STORAGE = 16;
const int XRE_PPLL_TRAVERSAL_LIMIT = 256;

void XRE_RecordRejectedPpllPixel(uint status)
{
    atomicAdd(PpllRejectedPixelCount, 1u);
    atomicOr(PpllStatusFlags, status);
}

bool XRE_PpllDepthComesBefore(float candidate, float existing)
{
    return PpllReversedDepth
        ? candidate > existing
        : candidate < existing;
}

void main()
{
    OutColor = vec4(0.0);
    OutFragmentCount = 0.0;

    // Pool overflow is global because the producer has no spare node with
    // which to mark the affected pixel. Preserve the HDR destination rather
    // than composite an arbitrary subset of transparency.
    if (PpllOverflowFragmentCount != 0u)
        return;

    ivec2 pixel = XRENGINE_ScreenPixelLocal(
        gl_FragCoord.xy,
        vec2(0.0),
        vec2(textureSize(PpllHeadPointerTex, 0)));
    uint nodeIndex = texelFetch(PpllHeadPointerTex, pixel, 0).r;
    if (nodeIndex == XRE_PPLL_INVALID_NODE)
        return;

    uint safeNodeCount = min(
        PpllMaxNodes,
        min(PpllEmittedNodeCount, uint(PpllNodes.length())));
    int resolveLimit = clamp(
        PpllResolveFragmentLimit,
        0,
        XRE_PPLL_FRAGMENT_STORAGE);
    vec4 sortedColors[XRE_PPLL_FRAGMENT_STORAGE];
    float sortedDepths[XRE_PPLL_FRAGMENT_STORAGE];
    int storedCount = 0;
    int actualCount = 0;
    uint rejectionStatus = 0u;

    while (nodeIndex != XRE_PPLL_INVALID_NODE &&
           actualCount < XRE_PPLL_TRAVERSAL_LIMIT)
    {
        if (nodeIndex >= safeNodeCount)
        {
            rejectionStatus |= XRE_PPLL_STATUS_INVALID_LINK;
            break;
        }

        XRE_PpllNode node = PpllNodes[nodeIndex];
        actualCount++;
        if (storedCount < resolveLimit)
        {
            int insertIndex = storedCount;
            while (insertIndex > 0 &&
                   XRE_PpllDepthComesBefore(
                       node.Depth,
                       sortedDepths[insertIndex - 1]))
            {
                sortedDepths[insertIndex] = sortedDepths[insertIndex - 1];
                sortedColors[insertIndex] = sortedColors[insertIndex - 1];
                insertIndex--;
            }
            sortedDepths[insertIndex] = node.Depth;
            sortedColors[insertIndex] = node.Color;
            storedCount++;
        }
        else
        {
            rejectionStatus |= XRE_PPLL_STATUS_FRAGMENT_LIMIT;
        }

        nodeIndex = node.Next;
    }

    if (nodeIndex != XRE_PPLL_INVALID_NODE &&
        (rejectionStatus & XRE_PPLL_STATUS_INVALID_LINK) == 0u)
    {
        rejectionStatus |= XRE_PPLL_STATUS_TRAVERSAL_LIMIT;
    }

    OutFragmentCount = float(actualCount);
    if (rejectionStatus != 0u)
    {
        XRE_RecordRejectedPpllPixel(rejectionStatus);
        return;
    }

    vec4 composite = vec4(0.0);
    for (int fragmentIndex = storedCount - 1;
         fragmentIndex >= 0;
         fragmentIndex--)
    {
        vec4 source = sortedColors[fragmentIndex];
        composite.rgb = source.rgb * source.a +
            composite.rgb * (1.0 - source.a);
        composite.a = source.a + composite.a * (1.0 - source.a);
    }

    OutColor = composite;
}
