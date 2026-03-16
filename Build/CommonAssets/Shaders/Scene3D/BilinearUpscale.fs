#version 450 core

// Simple bilinear upscale pass for TSR.
// Reads the internal-resolution post-processed image and writes to the full-resolution output.
// GPU bilinear filtering provides the interpolation.

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;

uniform sampler2D PostProcessOutputTexture;

void main()
{
    vec2 uv = FragPos.xy;
    if (uv.x > 1.0 || uv.y > 1.0)
        discard;

    uv = uv * 0.5 + 0.5;
    OutColor = texture(PostProcessOutputTexture, uv);
}
