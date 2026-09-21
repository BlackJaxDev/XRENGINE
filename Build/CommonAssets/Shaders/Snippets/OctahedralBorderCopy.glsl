// OctahedralBorderCopy.glsl
// Usage: #pragma snippet "OctahedralBorderCopy"
#ifndef XR_OCTAHEDRAL_BORDER_COPY_INCLUDED
#define XR_OCTAHEDRAL_BORDER_COPY_INCLUDED

ivec2 MapBorderToInterior(ivec2 localCoord, int S)
{
    int x = localCoord.x;
    int y = localCoord.y;

    // Corners: diagonally mirror to opposing interior corner
    if (x == 0 && y == 0) return ivec2(S - 2, S - 2);
    if (x == 0 && y == S - 1) return ivec2(S - 2, 1);
    if (x == S - 1 && y == 0) return ivec2(1, S - 2);
    if (x == S - 1 && y == S - 1) return ivec2(1, 1);

    // Edges: mirror across edge into opposing interior border
    if (y == 0) return ivec2(S - 1 - x, 1);
    if (y == S - 1) return ivec2(S - 1 - x, S - 2);
    if (x == 0) return ivec2(1, S - 1 - y);
    if (x == S - 1) return ivec2(S - 2, S - 1 - y);

    return localCoord;
}

ivec2 GetBorderLocalCoord(int t, int S)
{
    // Total perimeter texels = 4 * (S - 1)
    if (t < S)
        return ivec2(t, 0); // Bottom edge
    else if (t < 2 * S - 1)
        return ivec2(S - 1, t - (S - 1)); // Right edge
    else if (t < 3 * S - 2)
        return ivec2((S - 1) - (t - (2 * S - 2)), S - 1); // Top edge
    else
        return ivec2(0, (S - 1) - (t - (3 * S - 3))); // Left edge
}

#endif // XR_OCTAHEDRAL_BORDER_COPY_INCLUDED
