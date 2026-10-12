struct GTAOBlurUniforms_std140_0
{
    @align(16) outputSize_0 : vec2<f32>,
    @align(8) depthSize_0 : vec2<f32>,
    @align(16) texelSize_0 : vec2<f32>,
    @align(8) blurDirection_0 : vec2<f32>,
    @align(16) denoiseRadius_0 : i32,
    @align(4) denoiseSharpness_0 : f32,
    @align(8) denoiseEnabled_0 : i32,
    @align(4) useInputNormals_0 : i32,
    @align(16) useNormalWeightedBlur_0 : i32,
    @align(4) depthMode_0 : i32,
    @align(8) outputOrigin_0 : vec2<f32>,
};

@binding(0) @group(0) var<uniform> gtaoBlur_0 : GTAOBlurUniforms_std140_0;
@binding(0) @group(1) var gtaoInputTexture_0 : texture_2d<f32>;

@binding(1) @group(1) var gtaoInputSampler_0 : sampler;

@binding(0) @group(2) var depthView_0 : texture_depth_2d;

@binding(0) @group(3) var normalTexture_0 : texture_2d<f32>;

@binding(1) @group(3) var normalSampler_0 : sampler;

struct vertexOutput_0
{
    @builtin(position) output_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_0 : vec3<f32>,
};

@vertex
fn gtaoBlurVertex( _S1 : vertexInput_0) -> vertexOutput_0
{
    var _S2 : vertexOutput_0 = vertexOutput_0( vec4<f32>(_S1.position_0.xy, 0.0f, 1.0f) );
    return _S2;
}

fn edgeFade_0( uv_0 : vec2<f32>) -> f32
{
    var edges_0 : vec2<f32> = min(uv_0, vec2<f32>(1.0f) - uv_0) * gtaoBlur_0.depthSize_0;
    var t_0 : f32 = saturate(min(edges_0.x, edges_0.y) / clamp(f32(clamp(gtaoBlur_0.denoiseRadius_0, i32(1), i32(16))) * 0.5f, 1.0f, 4.0f));
    return t_0 * t_0 * (3.0f - 2.0f * t_0);
}

fn readDepth_0( uv_1 : vec2<f32>) -> f32
{
    var _S3 : vec3<i32> = vec3<i32>(vec2<i32>(clamp(uv_1 * gtaoBlur_0.depthSize_0, vec2<f32>(0.0f), gtaoBlur_0.depthSize_0 - vec2<f32>(1.0f))), i32(0));
    return (textureLoad((depthView_0), ((_S3)).xy, ((_S3)).z));
}

fn isFarDepth_0( depth_0 : f32) -> bool
{
    var _S4 : bool;
    if((gtaoBlur_0.depthMode_0) == i32(1))
    {
        _S4 = depth_0 <= 9.99999997475242708e-07f;
    }
    else
    {
        _S4 = depth_0 >= 0.99999898672103882f;
    }
    return _S4;
}

fn readNormal_0( uv_2 : vec2<f32>) -> vec3<f32>
{
    var f_0 : vec2<f32> = (textureSampleLevel((normalTexture_0), (normalSampler_0), (uv_2), (0.0f))).xy * vec2<f32>(2.0f) - vec2<f32>(1.0f);
    var _S5 : vec3<f32> = vec3<f32>(f_0, 1.0f - abs(f_0.x) - abs(f_0.y));
    var n_0 : vec3<f32> = _S5;
    var t_1 : f32 = saturate(- _S5.z);
    var _S6 : vec2<f32> = _S5.xy;
    var _S7 : f32;
    if((_S5.x) >= 0.0f)
    {
        _S7 = t_1;
    }
    else
    {
        _S7 = - t_1;
    }
    var _S8 : f32;
    if((n_0.y) >= 0.0f)
    {
        _S8 = t_1;
    }
    else
    {
        _S8 = - t_1;
    }
    var _S9 : vec2<f32> = _S6 - vec2<f32>(_S7, _S8);
    n_0.x = _S9.x;
    n_0.y = _S9.y;
    return normalize(n_0);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn gtaoBlurFragment(@builtin(position) position_1 : vec4<f32>) -> pixelOutput_0
{
    var _S10 : vec2<f32> = vec2<f32>(1.0f);
    var uv_3 : vec2<f32> = saturate((position_1.xy - gtaoBlur_0.outputOrigin_0) / max(gtaoBlur_0.outputSize_0, _S10));
    var centerAO_0 : f32 = (textureSampleLevel((gtaoInputTexture_0), (gtaoInputSampler_0), (uv_3), (0.0f))).x;
    var useNormals_0 : bool;
    if((gtaoBlur_0.denoiseEnabled_0) == i32(0))
    {
        useNormals_0 = true;
    }
    else
    {
        useNormals_0 = (gtaoBlur_0.denoiseRadius_0) <= i32(0);
    }
    if(useNormals_0)
    {
        var value_0 : f32 = mix(1.0f, centerAO_0, edgeFade_0(uv_3));
        var _S11 : pixelOutput_0 = pixelOutput_0( vec4<f32>(value_0, value_0, value_0, 1.0f) );
        return _S11;
    }
    var centerDepth_0 : f32 = readDepth_0(uv_3);
    if(isFarDepth_0(centerDepth_0))
    {
        var _S12 : pixelOutput_0 = pixelOutput_0( vec4<f32>(1.0f, 1.0f, 1.0f, 1.0f) );
        return _S12;
    }
    if((gtaoBlur_0.useInputNormals_0) != i32(0))
    {
        useNormals_0 = (gtaoBlur_0.useNormalWeightedBlur_0) != i32(0);
    }
    else
    {
        useNormals_0 = false;
    }
    var _S13 : vec3<f32> = vec3<f32>(0.0f);
    var centerNormal_0 : vec3<f32>;
    if(useNormals_0)
    {
        centerNormal_0 = readNormal_0(uv_3);
    }
    else
    {
        centerNormal_0 = _S13;
    }
    var normalWeight_0 : f32;
    var radius_0 : i32 = clamp(gtaoBlur_0.denoiseRadius_0, i32(1), i32(16));
    var _S14 : f32 = max(f32(radius_0) * 0.5f, 1.0f);
    var _S15 : f32 = max(gtaoBlur_0.denoiseSharpness_0, 0.00100000004749745f);
    var offset_0 : i32 = - radius_0;
    var result_0 : f32 = 0.0f;
    var weightSum_0 : f32 = 0.0f;
    for(;;)
    {
        if(offset_0 <= radius_0)
        {
        }
        else
        {
            break;
        }
        var sampleUV_0 : vec2<f32> = uv_3 + gtaoBlur_0.blurDirection_0 * gtaoBlur_0.texelSize_0 * vec2<f32>(f32(offset_0));
        var _S16 : bool;
        if((any((sampleUV_0 < vec2<f32>(0.0f)))))
        {
            _S16 = true;
        }
        else
        {
            _S16 = (any((sampleUV_0 > _S10)));
        }
        if(_S16)
        {
            offset_0 = offset_0 + i32(1);
            continue;
        }
        var depth_1 : f32 = readDepth_0(sampleUV_0);
        if(isFarDepth_0(depth_1))
        {
            offset_0 = offset_0 + i32(1);
            continue;
        }
        var spatialWeight_0 : f32 = exp(-0.5f * f32(offset_0 * offset_0) / (_S14 * _S14));
        var depthWeight_0 : f32 = exp(- abs(depth_1 - centerDepth_0) * 24.0f * _S15);
        if(useNormals_0)
        {
            normalWeight_0 = pow(max(dot(readNormal_0(sampleUV_0), centerNormal_0), 0.0f), 1.0f + _S15 * 2.0f);
        }
        else
        {
            normalWeight_0 = 1.0f;
        }
        var weight_0 : f32 = spatialWeight_0 * depthWeight_0 * normalWeight_0;
        var weightSum_1 : f32 = weightSum_0 + weight_0;
        result_0 = result_0 + (textureSampleLevel((gtaoInputTexture_0), (gtaoInputSampler_0), (sampleUV_0), (0.0f))).x * weight_0;
        weightSum_0 = weightSum_1;
        offset_0 = offset_0 + i32(1);
    }
    if(weightSum_0 > 0.0f)
    {
        normalWeight_0 = saturate(result_0 / weightSum_0);
    }
    else
    {
        normalWeight_0 = centerAO_0;
    }
    var value_1 : f32 = mix(1.0f, normalWeight_0, edgeFade_0(uv_3));
    var _S17 : pixelOutput_0 = pixelOutput_0( vec4<f32>(value_1, value_1, value_1, 1.0f) );
    return _S17;
}

