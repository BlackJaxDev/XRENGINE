#ifndef XRENGINE_GTAO_HALF_DEPTH_COMMON
#define XRENGINE_GTAO_HALF_DEPTH_COMMON
// Block rule for GTAO at half resolution. Each reduced texel stands for one 2x2 block of the
// full-resolution depth. The reduced texels pick the nearest and the farthest sample of the
// block in a checkerboard pattern, so that both surfaces at a depth edge keep AO samples. The
// prepare pass and the upsample pass use this one rule. Requires AOCommon.glsl (DepthMode).

// Texture coordinate of the block center. textureGather there returns the four block samples:
// x = (0,1), y = (1,1), z = (1,0), w = (0,0), as offsets from the block origin (2 * halfTexel).
vec2 XR_GTAO_HalfBlockGatherUV(ivec2 halfTexel, ivec2 fullSize)
{
    return (vec2(halfTexel * 2) + 1.0f) / vec2(fullSize);
}

// Index (0-3) of the chosen block sample in the textureGather result.
int XR_GTAO_HalfBlockPick(vec4 block, ivec2 halfTexel)
{
    // Reversed depth (DepthMode 1) stores larger values nearer to the camera.
    bool pickNearest = ((halfTexel.x + halfTexel.y) & 1) == 0;
    bool largerIsNearer = DepthMode == 1;
    bool pickLarger = pickNearest == largerIsNearer;
    int pick = 0;
    for (int i = 1; i < 4; ++i)
    {
        bool better = pickLarger ? block[i] > block[pick] : block[i] < block[pick];
        if (better)
            pick = i;
    }
    return pick;
}

// Full-resolution texel of a block sample, clamped to the image (odd extents repeat the edge).
ivec2 XR_GTAO_HalfBlockTexel(ivec2 halfTexel, int pick, ivec2 fullSize)
{
    const ivec2 offsets[4] = ivec2[4](ivec2(0, 1), ivec2(1, 1), ivec2(1, 0), ivec2(0, 0));
    return clamp(halfTexel * 2 + offsets[pick], ivec2(0), fullSize - 1);
}

#endif
