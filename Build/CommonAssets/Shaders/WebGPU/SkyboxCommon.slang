// Shared sky vertex contract: reconstruct the camera ray at the frozen far depth.
// Transform its direction with w=0 so camera translation never moves the sky.
struct SkyViewUniforms
{
    float4x4 inverseView;
    float4x4 inverseProjection;
    float4 depthRange;
};

struct SkyMaterialUniforms
{
    float intensity;
    float rotation;
    float timeOfDay;
    float cloudCoverage;
    float3 topColor;
    float cloudScale;
    float3 bottomColor;
    float cloudSpeed;
    float cloudSharpness;
    float starIntensity;
    float horizonHaze;
    float sunDiscSize;
    float moonDiscSize;
    float cameraTwinkle;
    float twinklePhase;
};

[[vk::binding(0, 0)]] ConstantBuffer<SkyViewUniforms> view;
[[vk::binding(0, 1)]] ConstantBuffer<SkyMaterialUniforms> sky;

struct SkyVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
};

struct SkyVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldDirection : TEXCOORD0;
};

[shader("vertex")]
SkyVertexOutput skyVertex(SkyVertexInput input)
{
    SkyVertexOutput output;
    float farDepth = view.depthRange.y;
    float4 viewPosition = mul(view.inverseProjection, float4(input.position.xy, farDepth, 1.0));
    float invW = abs(viewPosition.w) > 1e-6 ? 1.0 / viewPosition.w : 1.0;
    float3 direction = mul(view.inverseView, float4(viewPosition.xyz * invW, 0.0)).xyz;
    float cosRotation = cos(sky.rotation);
    float sinRotation = sin(sky.rotation);
    output.worldDirection = float3(direction.x * cosRotation - direction.z * sinRotation,
        direction.y, direction.x * sinRotation + direction.z * cosRotation);
    output.position = float4(input.position.xy, farDepth, 1.0);
    return output;
}
