struct SceneView
{
    float4x4 viewProjection;
};

struct SceneObject
{
    float4x4 modelMatrix;
    float4x4 normalMatrix;
};

struct SceneMaterial
{
    float4 matColor;
};

[[vk::binding(0, 0)]] ConstantBuffer<SceneView> view;
[[vk::binding(1, 0)]] ConstantBuffer<SceneObject> object;
[[vk::binding(0, 1)]] ConstantBuffer<SceneMaterial> material;

struct SceneVertexInput
{
    [[vk::location(0)]] float3 position : POSITION;
    [[vk::location(1)]] float3 normal : NORMAL;
};

struct SceneVertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 worldNormal : TEXCOORD0;
};

[shader("vertex")]
SceneVertexOutput customMsaaSceneVertex(SceneVertexInput input)
{
    SceneVertexOutput output;
    float4 worldPosition = mul(object.modelMatrix, float4(input.position, 1.0));
    output.position = mul(view.viewProjection, worldPosition);
    output.worldNormal = mul(object.normalMatrix, float4(input.normal, 0.0)).xyz;
    return output;
}

[shader("fragment")]
float4 customMsaaSceneFragment(SceneVertexOutput input) : SV_Target0
{
    // Front-facing XY quads receive unit lighting, keeping the expected linear
    // color composite exact while still exercising a normal-bearing mesh ABI.
    float facing = max(dot(normalize(input.worldNormal), float3(0.0, 0.0, 1.0)), 0.0);
    float light = 0.35 + 0.65 * facing;
    return float4(material.matColor.rgb * light, material.matColor.a);
}
