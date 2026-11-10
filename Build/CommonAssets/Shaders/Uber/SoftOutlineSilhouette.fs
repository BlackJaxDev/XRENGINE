#version 450

layout(location = 24) in vec2 FinUv;
layout(location = 25) in vec2 SurfaceUv;
layout(location = 0) out vec4 OutColor;

uniform sampler2D _NoiseDistTexture;
uniform sampler2D _MaskTexture;
uniform vec4 _NoiseDistTexture_ST;
uniform vec4 _MaskTexture_ST;
uniform vec2 _TexturePanning;
uniform vec2 _MaskPanning;
uniform float RenderTime;

uniform vec4 _Col1;
uniform float _AttenuateEdge;
uniform float _EdgeMinValue;
uniform float _EdgePower;
uniform float _GradientOffset;
uniform vec4 _SmoothFinalEdge;
uniform int _InvertGradient;

// Samples a fin texture with the mesh UV, the authored scale and offset, and a time-based pan.
vec4 samplePanned(sampler2D tex, vec4 scaleOffset, vec2 panning)
{
    vec2 uv = SurfaceUv * scaleOffset.xy + RenderTime * panning + scaleOffset.zw;
    return texture(tex, uv);
}

void main()
{
    float edge = _InvertGradient != 0 ? 1.0 - FinUv.y : FinUv.y;
    float denominator = max(_EdgeMinValue, edge + _GradientOffset);
    float gradient = pow(_AttenuateEdge / max(denominator, 0.0001), _EdgePower);
    gradient *= mix(_SmoothFinalEdge.y, _SmoothFinalEdge.x, edge);

    vec4 noise = samplePanned(_NoiseDistTexture, _NoiseDistTexture_ST, _TexturePanning);
    vec4 mask = samplePanned(_MaskTexture, _MaskTexture_ST, _MaskPanning);
    vec4 tint = max(_Col1, vec4(0.0001));

    // The gradient scales colour and alpha together. The noise texture applies two times:
    // one time as distortion of the mask and one time as modulation of the tint.
    // AudioLink factors are neutral because this pass has no AudioLink input.
    vec4 masked = vec4(mask.rgb, 1.0) * noise * mask.a;
    OutColor = masked * (noise * tint) * max(gradient, 0.0);
}
