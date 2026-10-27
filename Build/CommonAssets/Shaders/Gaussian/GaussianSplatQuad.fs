#version 460

// Evaluates one gaussian splat and writes premultiplied color for "over" blending
// (source factor One, destination factor OneMinusSrcAlpha).

layout(location = 0) in vec2 FragEllipseOffset;
layout(location = 1) flat in vec4 FragColorOpacity;

layout(location = 0) out vec4 OutColor;

const float MinimumAlpha = 1.0 / 255.0;

void main()
{
    // FragEllipseOffset is in sigma units along the eigenvectors, so its squared length
    // is d^T * inverse(covariance) * d.
    float alpha = FragColorOpacity.a * exp(-0.5 * dot(FragEllipseOffset, FragEllipseOffset));
    if (alpha < MinimumAlpha)
        discard;

    OutColor = vec4(FragColorOpacity.rgb * alpha, alpha);
}
