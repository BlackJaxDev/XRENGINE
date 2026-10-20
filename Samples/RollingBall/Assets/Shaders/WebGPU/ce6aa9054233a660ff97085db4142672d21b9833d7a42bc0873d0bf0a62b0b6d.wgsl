@binding(0) @group(1) var sourceTexture_0 : texture_2d<f32>;

@binding(1) @group(1) var sourceSampler_0 : sampler;

struct TonemapUniforms_std140_0
{
    @align(16) parameters_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> tonemap_0 : TonemapUniforms_std140_0;
struct TonemapOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn tonemapVertex( _S1 : vertexInput_0) -> TonemapOutput_0
{
    var output_0 : TonemapOutput_0;
    var _S2 : vec2<f32> = _S1.position_1.xy;
    output_0.position_0 = vec4<f32>(_S2, 0.0f, 1.0f);
    output_0.uv_0 = _S2 * vec2<f32>(0.5f, -0.5f) + vec2<f32>(0.5f);
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_1 : vec2<f32>,
};

@fragment
fn tonemapFragment( _S3 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var source_0 : vec4<f32> = (textureSample((sourceTexture_0), (sourceSampler_0), (_S3.uv_1)));
    var exposed_0 : vec3<f32> = max(source_0.xyz * vec3<f32>(max(tonemap_0.parameters_0.x, 0.0f)), vec3<f32>(0.0f));
    var _S4 : f32 = max(tonemap_0.parameters_0.z, 0.00009999999747379f);
    var _S5 : pixelOutput_0 = pixelOutput_0( vec4<f32>(pow(saturate(exposed_0 * vec3<f32>((_S4 + 1.0f)) / (exposed_0 + vec3<f32>(_S4))), vec3<f32>((1.0f / max(tonemap_0.parameters_0.y, 0.00009999999747379f)))), source_0.w) );
    return _S5;
}

