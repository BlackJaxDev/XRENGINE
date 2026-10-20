// Exact browser lowering of Scene3D/OctahedralImposterBillboard and its common sampler.
struct ImpostorView
{
    float4x4 viewProjection;
    float4x4 inverseView;
};
struct ImpostorObject
{
    float4x4 modelMatrix;
};
[[vk::binding(0, 0)]] ConstantBuffer<ImpostorView> impostorView;
[[vk::binding(1, 0)]] ConstantBuffer<ImpostorObject> impostorObject;
[[vk::binding(0, 1)]] Texture2DArray<float4> impostorViews;
[[vk::binding(1, 1)]] SamplerState impostorSampler;

struct ImpostorVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(4)]] float2 uv : TEXCOORD4;
};
struct ImpostorVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(4)]] float2 uv : TEXCOORD4;
};
float3 impostorColumn(float4x4 matrix, uint column)
{
    return float3(matrix[0][column], matrix[1][column], matrix[2][column]);
}
[shader("vertex")]
ImpostorVertexOutput impostorVertex(ImpostorVertexInput input)
{
    float3 right = normalize(impostorColumn(impostorView.inverseView, 0));
    float3 up = normalize(impostorColumn(impostorView.inverseView, 1));
    float sx = length(impostorColumn(impostorObject.modelMatrix, 0));
    float sy = length(impostorColumn(impostorObject.modelMatrix, 1));
    float3 center = impostorColumn(impostorObject.modelMatrix, 3);
    float3 position = center + right * input.position.x * sx + up * input.position.y * sy;
    ImpostorVertexOutput output;
    output.position = mul(impostorView.viewProjection, float4(position, 1.0));
    output.uv = input.uv;
    return output;
}
static const float INV_SQRT2 = 0.7071067811865476;
static const float INV_SQRT3 = 0.5773502691896258;
static const float3 captureDirections[26] = {
    float3(1,0,0), float3(-1,0,0), float3(0,1,0), float3(0,-1,0), float3(0,0,1), float3(0,0,-1),
    float3(INV_SQRT2,INV_SQRT2,0), float3(INV_SQRT2,-INV_SQRT2,0),
    float3(-INV_SQRT2,INV_SQRT2,0), float3(-INV_SQRT2,-INV_SQRT2,0),
    float3(INV_SQRT2,0,INV_SQRT2), float3(INV_SQRT2,0,-INV_SQRT2),
    float3(-INV_SQRT2,0,INV_SQRT2), float3(-INV_SQRT2,0,-INV_SQRT2),
    float3(0,INV_SQRT2,INV_SQRT2), float3(0,INV_SQRT2,-INV_SQRT2),
    float3(0,-INV_SQRT2,INV_SQRT2), float3(0,-INV_SQRT2,-INV_SQRT2),
    float3(-INV_SQRT3,-INV_SQRT3,-INV_SQRT3), float3(-INV_SQRT3,-INV_SQRT3,INV_SQRT3),
    float3(-INV_SQRT3,INV_SQRT3,-INV_SQRT3), float3(-INV_SQRT3,INV_SQRT3,INV_SQRT3),
    float3(INV_SQRT3,-INV_SQRT3,-INV_SQRT3), float3(INV_SQRT3,-INV_SQRT3,INV_SQRT3),
    float3(INV_SQRT3,INV_SQRT3,-INV_SQRT3), float3(INV_SQRT3,INV_SQRT3,INV_SQRT3)
};
[shader("fragment")]
float4 impostorFragment(ImpostorVertexOutput input) : SV_Target0
{
    float2 uv = clamp(input.uv, 0.0, 1.0);
    // Compute the derivatives before data-dependent selection. SampleGrad preserves
    // implicit desktop LOD while keeping WGSL derivative uniformity explicit.
    float2 dx = ddx(uv);
    float2 dy = ddy(uv);
    float3 direction = normalize(impostorColumn(impostorView.inverseView, 3) -
        impostorColumn(impostorObject.modelMatrix, 3));
    float w0 = -1.0; int i0 = -1;
    float w1 = -1.0; int i1 = -1;
    float w2 = -1.0; int i2 = -1;
    for (int i = 0; i < 26; ++i)
    {
        float weight = max(dot(direction, captureDirections[i]), 0.0);
        if (weight > w0)
        {
            w2 = w1; i2 = i1;
            w1 = w0; i1 = i0;
            w0 = weight; i0 = i;
        }
        else if (weight > w1)
        {
            w2 = w1; i2 = i1;
            w1 = weight; i1 = i;
        }
        else if (weight > w2)
        {
            w2 = weight; i2 = i;
        }
    }
    int3 indices = int3(i0, i1, i2);
    float3 weights = max(float3(w0, w1, w2), 0.0);
    float weightSum = weights.x + weights.y + weights.z;
    if (weightSum <= 1e-5)
        return impostorViews.SampleGrad(impostorSampler, float3(uv, float(max(indices.x, 0))), dx, dy);
    float4 blended = 0.0;
    for (int i = 0; i < 3; ++i)
    {
        int layer = indices[i];
        float weight = weights[i];
        if (layer < 0 || weight <= 0.0) continue;
        blended += impostorViews.SampleGrad(impostorSampler, float3(uv, float(layer)), dx, dy) * (weight / weightSum);
    }
    return blended;
}
