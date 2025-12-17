#version 450

layout (location = 0) out vec3 OutColor;
layout (location = 0) in vec3 FragClipPos;

// Debug shader: draws a simple gradient to confirm the fullscreen triangle is rendering.
void main()
{
    // FragClipPos comes from the fullscreen triangle vertices: (-1,-1), (3,-1), (-1,3)
    // Remap and clamp to [0,1] so we get a visible gradient across the screen.
    float x = clamp(FragClipPos.x * 0.25 + 0.5, 0.0, 1.0);
    float y = clamp(FragClipPos.y * 0.25 + 0.5, 0.0, 1.0);
    OutColor = vec3(x, y, 1.0);
}
