struct BloomDownsampleUniforms_std140_0
{
    @align(16) outputArea_0 : vec4<f32>,
    @align(16) sourceTexelThreshold_0 : vec4<f32>,
    @align(16) controls_0 : vec4<f32>,
    @align(16) luminance_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> bloomDownsample_0 : BloomDownsampleUniforms_std140_0;
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
fn bloomDownsampleVertex( _S1 : vertexInput_0) -> ScreenOutput_0
{
    var output_0 : ScreenOutput_0;
    output_0.position_0 = vec4<f32>(_S1.position_1.xy, 0.0f, 1.0f);
    return output_0;
}

fn sampleSource_0( uv_0 : vec2<f32>) -> vec3<f32>
{
    return (textureSampleLevel((sourceTexture_0), (sourceSampler_0), (uv_0), (0.0f))).xyz;
}

fn karisWeight_0( color_0 : vec3<f32>) -> f32
{
    return 1.0f / (1.0f + dot(color_0, bloomDownsample_0.luminance_0.xyz));
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn bloomDownsampleFragment(@builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    if((bloomDownsample_0.controls_0.w) > 0.5f)
    {
        var _S2 : pixelOutput_0 = pixelOutput_0( vec4<f32>(1.0f, 0.0f, 1.0f, 1.0f) );
        return _S2;
    }
    var uv_1 : vec2<f32> = saturate((position_2.xy - bloomDownsample_0.outputArea_0.xy) / max(bloomDownsample_0.outputArea_0.zw, vec2<f32>(1.0f)));
    var texel_0 : vec2<f32> = bloomDownsample_0.sourceTexelThreshold_0.xy;
    var b_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.0f, -1.0f));
    var f_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, 0.0f));
    var g_0 : vec3<f32> = sampleSource_0(uv_1);
    var h_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(1.0f, 0.0f));
    var l_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.0f, 1.0f));
    var g0_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(-0.5f, -0.5f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.5f, -0.5f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(-0.5f, 0.5f)) + sampleSource_0(uv_1 + texel_0 * vec2<f32>(0.5f, 0.5f));
    var g1_0 : vec3<f32> = sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, -1.0f)) + b_0 + f_0 + g_0;
    var g2_0 : vec3<f32> = b_0 + sampleSource_0(uv_1 + texel_0 * vec2<f32>(1.0f, -1.0f)) + g_0 + h_0;
    var g3_0 : vec3<f32> = f_0 + g_0 + sampleSource_0(uv_1 + texel_0 * vec2<f32>(-1.0f, 1.0f)) + l_0;
    var g4_0 : vec3<f32> = g_0 + h_0 + l_0 + sampleSource_0(uv_1 + texel_0);
    var result_0 : vec3<f32>;
    if((bloomDownsample_0.controls_0.y) > 0.5f)
    {
        var _S3 : vec3<f32> = vec3<f32>(0.25f);
        var w0_0 : f32 = karisWeight_0(g0_0 * _S3);
        var w1_0 : f32 = karisWeight_0(g1_0 * _S3);
        var w2_0 : f32 = karisWeight_0(g2_0 * _S3);
        var w3_0 : f32 = karisWeight_0(g3_0 * _S3);
        var w4_0 : f32 = karisWeight_0(g4_0 * _S3);
        result_0 = (g0_0 * vec3<f32>((0.125f * w0_0)) + (g1_0 * vec3<f32>(w1_0) + g2_0 * vec3<f32>(w2_0) + g3_0 * vec3<f32>(w3_0) + g4_0 * vec3<f32>(w4_0)) * vec3<f32>(0.03125f)) / vec3<f32>((0.5f * w0_0 + 0.125f * (w1_0 + w2_0 + w3_0 + w4_0) + 0.00000999999974738f));
    }
    else
    {
        result_0 = g0_0 * vec3<f32>(0.125f) + (g1_0 + g2_0 + g3_0 + g4_0) * vec3<f32>(0.03125f);
    }
    if((bloomDownsample_0.controls_0.z) > 0.5f)
    {
        var brightness_0 : f32 = dot(result_0, bloomDownsample_0.luminance_0.xyz);
        if(brightness_0 <= 0.00000999999974738f)
        {
            result_0 = vec3<f32>(0.0f);
        }
        else
        {
            var threshold_0 : f32 = bloomDownsample_0.sourceTexelThreshold_0.z;
            var _S4 : f32 = max(threshold_0 * bloomDownsample_0.sourceTexelThreshold_0.w, 0.00000999999974738f);
            var _S5 : f32 = brightness_0 - threshold_0;
            var soft_0 : f32 = clamp(_S5 + _S4, 0.0f, 2.0f * _S4);
            result_0 = result_0 * vec3<f32>((max(soft_0 * soft_0 / (4.0f * _S4 + 0.00000999999974738f), _S5) / brightness_0 * bloomDownsample_0.controls_0.x));
        }
    }
    var _S6 : pixelOutput_0 = pixelOutput_0( vec4<f32>(max(result_0, vec3<f32>(0.0f)), 1.0f) );
    return _S6;
}

