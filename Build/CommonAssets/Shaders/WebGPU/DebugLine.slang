// Vertex expansion of packed desktop debug lines. Width is in NDC units
// relative to the smaller viewport dimension, matching DebugLineQuad.glsl.
struct DebugLineView
{
    float4x4 viewProjection;
    float4 viewport; // xy = screen width and height in pixels.
};
struct DebugLineMaterial
{
    float4 parameters; // x = desktop LineWidth.
};
[[vk::binding(0, 0)]] ConstantBuffer<DebugLineView> view;
[[vk::binding(0, 1)]] ConstantBuffer<DebugLineMaterial> material;
[[vk::binding(0, 2)]] StructuredBuffer<uint> lines;
struct DebugLineInput
{
    [[vk::location(0)]] float3 position : POSITION;
    uint instanceId : SV_InstanceID;
};
struct DebugLineOutput
{
    float4 position : SV_Position;
    nointerpolation float4 color : TEXCOORD0;
    noperspective float edgeCoord : TEXCOORD1;
    nointerpolation float halfWidthPixels : TEXCOORD2;
};
float4 unpackColor(uint packed)
{
    return float4(float(packed & 255u), float((packed >> 8u) & 255u),
        float((packed >> 16u) & 255u), float((packed >> 24u) & 255u)) / 255.0;
}
bool clipNear(inout float4 a, inout float4 b)
{
    float da = a.z;
    float db = b.z;
    if (da < 0.0 && db < 0.0)
        return false;
    if (da < 0.0 || db < 0.0)
    {
        float denominator = da - db;
        float t = abs(denominator) < 1e-6 ? 0.0 : da / denominator;
        float4 crossing = lerp(a, b, saturate(t));
        if (da < 0.0) a = crossing;
        else b = crossing;
    }
    return a.w > 1e-6 && b.w > 1e-6;
}
[shader("vertex")]
DebugLineOutput debugLineVertex(DebugLineInput input)
{
    uint baseWord = input.instanceId * 7u;
    float3 p0 = float3(asfloat(lines[baseWord]), asfloat(lines[baseWord + 1u]),
        asfloat(lines[baseWord + 2u]));
    float3 p1 = float3(asfloat(lines[baseWord + 3u]), asfloat(lines[baseWord + 4u]),
        asfloat(lines[baseWord + 5u]));
    float4 start = mul(view.viewProjection, float4(p0, 1.0));
    float4 end = mul(view.viewProjection, float4(p1, 1.0));
    DebugLineOutput output;
    output.color = unpackColor(lines[baseWord + 6u]);
    output.edgeCoord = 0.0;
    output.halfWidthPixels = 0.0;
    if (!clipNear(start, end))
    {
        // A fully clipped indexed quad must produce no rasterized fragments.
        output.position = float4(2.0, 2.0, 0.0, 1.0);
        return output;
    }
    float2 viewport = max(view.viewport.xy, float2(1.0));
    float2 screenStart = (start.xy / start.w) * viewport * 0.5;
    float2 screenEnd = (end.xy / end.w) * viewport * 0.5;
    float2 delta = screenEnd - screenStart;
    float squaredLength = dot(delta, delta);
    float2 direction = squaredLength < 1e-6 ? float2(1.0, 0.0) : delta * rsqrt(squaredLength);
    float2 perpendicular = float2(-direction.y, direction.x);
    float halfWidth = max(0.5, material.parameters.x * min(viewport.x, viewport.y) * 0.5);
    float rasterHalfWidth = halfWidth + 2.0;
    float2 offsetNdc = perpendicular * rasterHalfWidth * (2.0 / viewport);
    bool atEnd = input.position.y > 0.0;
    float side = input.position.x;
    float4 endpoint = atEnd ? end : start;
    output.position = endpoint + float4(offsetNdc * endpoint.w * side, 0.0, 0.0);
    output.edgeCoord = rasterHalfWidth * side;
    output.halfWidthPixels = halfWidth;
    return output;
}
[shader("fragment")]
float4 debugLineFragment(DebugLineOutput input) : SV_Target0
{
    float distancePixels = abs(input.edgeCoord);
    float aaWidth = max(fwidth(input.edgeCoord) * 1.25, 0.75);
    float edgeAlpha = 1.0 - smoothstep(input.halfWidthPixels, input.halfWidthPixels + aaWidth, distancePixels);
    float alpha = input.color.w * edgeAlpha;
    if (alpha <= 0.001)
        discard;
    return float4(input.color.xyz, alpha);
}
