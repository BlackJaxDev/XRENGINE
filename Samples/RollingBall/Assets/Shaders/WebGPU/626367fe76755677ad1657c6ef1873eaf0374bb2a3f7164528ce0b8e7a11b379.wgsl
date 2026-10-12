struct BloomCopyUniforms_std140_0
{
    @align(16) outputArea_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> bloomCopy_0 : BloomCopyUniforms_std140_0;
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
fn bloomCopyVertex( _S1 : vertexInput_0) -> ScreenOutput_0
{
    var output_0 : ScreenOutput_0;
    output_0.position_0 = vec4<f32>(_S1.position_1.xy, 0.0f, 1.0f);
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn bloomCopyFragment(@builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var _S2 : pixelOutput_0 = pixelOutput_0( (textureSample((sourceTexture_0), (sourceSampler_0), (saturate((position_2.xy - bloomCopy_0.outputArea_0.xy) / max(bloomCopy_0.outputArea_0.zw, vec2<f32>(1.0f)))))) );
    return _S2;
}

