uniform int FramebufferTextureYDirection;

// Cube samplers use texture rows, independent of the raster clip convention.
vec2 XR_ProbeRasterFacePlane(vec2 clipPlane)
{
    if (FramebufferTextureYDirection == 1)
        clipPlane.y = -clipPlane.y;
    return clipPlane;
}

// Cube face coordinates follow the samplerCube face order.
vec3 XR_ProbeFaceDirection(int face, vec2 plane)
{
    if (face == 0) return vec3(1.0, -plane.y, -plane.x);
    if (face == 1) return vec3(-1.0, -plane.y, plane.x);
    if (face == 2) return vec3(plane.x, 1.0, plane.y);
    if (face == 3) return vec3(plane.x, -1.0, -plane.y);
    if (face == 4) return vec3(plane.x, -plane.y, 1.0);
    return vec3(-plane.x, -plane.y, -1.0);
}
