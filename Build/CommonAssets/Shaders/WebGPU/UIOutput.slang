// Shared batch output contract. Screen UI retains display RGB; canvas textures
// accumulate linear premultiplied color before their world surface is shaded.
struct UIView
{
    float4x4 viewProjection;
    float4 outputMode;
};
[[vk::binding(0, 0)]] ConstantBuffer<UIView> view;

bool uiLinearOffscreenOutput()
{
    return view.outputMode.x > 0.5;
}

float3 uiDisplayToLinear(float3 color)
{
    float3 low = color / 12.92;
    float3 high = pow(max((color + 0.055) / 1.055, float3(0.0)), float3(2.4));
    return lerp(high, low, step(color, float3(0.04045)));
}

float4 uiSolidOutput(float4 color)
{
    if (!uiLinearOffscreenOutput())
        return color;
    float alpha = saturate(color.a);
    return float4(uiDisplayToLinear(color.rgb) * alpha, alpha);
}
