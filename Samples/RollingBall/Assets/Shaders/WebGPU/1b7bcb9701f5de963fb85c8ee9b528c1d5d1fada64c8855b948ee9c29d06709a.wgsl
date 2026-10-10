struct BloomUpsampleUniforms_std140_0
{
    @align(16) outputArea_0 : vec4<f32>,
    @align(16) sourceTexel_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> bloomUpsample_0 : BloomUpsampleUniforms_std140_0;
@binding(0) @group(1) var sourceTexture_0 : texture_2d<f32>;

@binding(1) @group(1) var sourceSampler_0 : sampler;

struct ScreenOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn bloomUpsampleVertex( _S1 : vertexInput_0) -> ScreenOutput_0
{
    var output_0 : ScreenOutput_0;
    output_0.position_0 = vec4<f32>(_S1.position_1.xy, 0.0f, 1.0f);
    return output_0;
}

fn sampleSource_0( uv_0 : vec2<f32>) -> vec3<f32>
{
    return (textureSampleLevel((sourceTexture_0), (sourceSampler_0), (uv_0), (0.0f))).xyz;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn bloomUpsampleFragment(@builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var uv_1 : vec2<f32> = saturate((position_2.xy - bloomUpsample_0.outputArea_0.xy) / max(bloomUpsample_0.outputArea_0.zw, vec2<f32>(1.0f)));
    var texel_0 : vec2<f32> = bloomUpsample_0.sourceTexel_0.xy * vec2<f32>(bloomUpsample_0.sourceTexel_0.z);
    var _S2 : vec3<f32> = vec3<f32>(2.0f);
    var _S3 : pixelOutput_0 = pixelOutput_0( vec4<f32>(max((sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, -1.0f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.0f, -1.0f)) * _S2 + sampleSource_0(uv_1 + texel_0 * vec2<f32>(1.0f, -1.0f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, 0.0f)) * _S2 + sampleSource_0(uv_1) * vec3<f32>(4.0f) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(1.0f, 0.0f)) * _S2 + sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, 1.0f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.0f, 1.0f)) * _S2 + sampleSource_0(uv_1 + texel_0)) / vec3<f32>(16.0f), vec3<f32>(0.0f)) * vec3<f32>(bloomUpsample_0.sourceTexel_0.w), 1.0f) );
    return _S3;
}

