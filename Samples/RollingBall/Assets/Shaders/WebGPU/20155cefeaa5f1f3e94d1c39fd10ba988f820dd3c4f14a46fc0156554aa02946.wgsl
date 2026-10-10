struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct GTAOUniforms_std140_0
{
    @align(16) viewMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) inverseProjMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) projMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) outputSize_0 : vec2<f32>,
    @align(8) depthSize_0 : vec2<f32>,
    @align(16) radius_0 : f32,
    @align(4) bias_0 : f32,
    @align(8) falloffStartRatio_0 : f32,
    @align(4) thicknessHeuristic_0 : f32,
    @align(16) visibilityBitmaskThickness_0 : f32,
    @align(4) sliceCount_0 : i32,
    @align(8) stepsPerSlice_0 : i32,
    @align(4) useInputNormals_0 : i32,
    @align(16) useVisibilityBitmask_0 : i32,
    @align(4) depthMode_0 : i32,
    @align(8) outputOrigin_0 : vec2<f32>,
};

@binding(0) @group(0) var<uniform> gtao_0 : GTAOUniforms_std140_0;
@binding(0) @group(1) var depthView_0 : texture_depth_2d;

@binding(0) @group(2) var normalTexture_0 : texture_2d<f32>;

@binding(1) @group(2) var normalSampler_0 : sampler;

struct vertexOutput_0
{
    @builtin(position) output_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_0 : vec3<f32>,
};

@vertex
fn gtaoVertex( _S1 : vertexInput_0) -> vertexOutput_0
{
    var _S2 : vertexOutput_0 = vertexOutput_0( vec4<f32>(_S1.position_0.xy, 0.0f, 1.0f) );
    return _S2;
}

fn readDepth_0( uv_0 : vec2<f32>) -> f32
{
    var _S3 : vec3<i32> = vec3<i32>(vec2<i32>(clamp(uv_0 * gtao_0.depthSize_0, vec2<f32>(0.0f), gtao_0.depthSize_0 - vec2<f32>(1.0f))), i32(0));
    return (textureLoad((depthView_0), ((_S3)).xy, ((_S3)).z));
}

fn isFarDepth_0( depth_0 : f32) -> bool
{
    var _S4 : bool;
    if((gtao_0.depthMode_0) == i32(1))
    {
        _S4 = depth_0 <= 9.99999997475242708e-07f;
    }
    else
    {
        _S4 = depth_0 >= 0.99999898672103882f;
    }
    return _S4;
}

fn viewPosFromDepth_0( depth_1 : f32,  uv_1 : vec2<f32>) -> vec3<f32>
{
    var view_0 : vec4<f32> = (((vec4<f32>(uv_1.x * 2.0f - 1.0f, 1.0f - uv_1.y * 2.0f, depth_1, 1.0f)) * (mat4x4<f32>(gtao_0.inverseProjMatrix_0.data_0[i32(0)][i32(0)], gtao_0.inverseProjMatrix_0.data_0[i32(1)][i32(0)], gtao_0.inverseProjMatrix_0.data_0[i32(2)][i32(0)], gtao_0.inverseProjMatrix_0.data_0[i32(3)][i32(0)], gtao_0.inverseProjMatrix_0.data_0[i32(0)][i32(1)], gtao_0.inverseProjMatrix_0.data_0[i32(1)][i32(1)], gtao_0.inverseProjMatrix_0.data_0[i32(2)][i32(1)], gtao_0.inverseProjMatrix_0.data_0[i32(3)][i32(1)], gtao_0.inverseProjMatrix_0.data_0[i32(0)][i32(2)], gtao_0.inverseProjMatrix_0.data_0[i32(1)][i32(2)], gtao_0.inverseProjMatrix_0.data_0[i32(2)][i32(2)], gtao_0.inverseProjMatrix_0.data_0[i32(3)][i32(2)], gtao_0.inverseProjMatrix_0.data_0[i32(0)][i32(3)], gtao_0.inverseProjMatrix_0.data_0[i32(1)][i32(3)], gtao_0.inverseProjMatrix_0.data_0[i32(2)][i32(3)], gtao_0.inverseProjMatrix_0.data_0[i32(3)][i32(3)]))));
    return view_0.xyz / vec3<f32>(max(view_0.w, 0.00000999999974738f));
}

fn decodeNormal_0( encoded_0 : vec2<f32>) -> vec3<f32>
{
    var f_0 : vec2<f32> = encoded_0 * vec2<f32>(2.0f) - vec2<f32>(1.0f);
    var _S5 : vec3<f32> = vec3<f32>(f_0, 1.0f - abs(f_0.x) - abs(f_0.y));
    var n_0 : vec3<f32> = _S5;
    var t_0 : f32 = clamp(- _S5.z, 0.0f, 1.0f);
    var _S6 : vec2<f32> = _S5.xy;
    var _S7 : f32;
    if((_S5.x) >= 0.0f)
    {
        _S7 = t_0;
    }
    else
    {
        _S7 = - t_0;
    }
    var _S8 : f32;
    if((n_0.y) >= 0.0f)
    {
        _S8 = t_0;
    }
    else
    {
        _S8 = - t_0;
    }
    var _S9 : vec2<f32> = _S6 - vec2<f32>(_S7, _S8);
    n_0.x = _S9.x;
    n_0.y = _S9.y;
    return normalize(n_0);
}

fn getViewNormal_0( uv_2 : vec2<f32>,  centerPos_0 : vec3<f32>) -> vec3<f32>
{
    if((gtao_0.useInputNormals_0) != i32(0))
    {
        return normalize((((vec4<f32>(decodeNormal_0((textureSampleLevel((normalTexture_0), (normalSampler_0), (uv_2), (0.0f))).xy), 0.0f)) * (mat4x4<f32>(gtao_0.viewMatrix_0.data_0[i32(0)][i32(0)], gtao_0.viewMatrix_0.data_0[i32(1)][i32(0)], gtao_0.viewMatrix_0.data_0[i32(2)][i32(0)], gtao_0.viewMatrix_0.data_0[i32(3)][i32(0)], gtao_0.viewMatrix_0.data_0[i32(0)][i32(1)], gtao_0.viewMatrix_0.data_0[i32(1)][i32(1)], gtao_0.viewMatrix_0.data_0[i32(2)][i32(1)], gtao_0.viewMatrix_0.data_0[i32(3)][i32(1)], gtao_0.viewMatrix_0.data_0[i32(0)][i32(2)], gtao_0.viewMatrix_0.data_0[i32(1)][i32(2)], gtao_0.viewMatrix_0.data_0[i32(2)][i32(2)], gtao_0.viewMatrix_0.data_0[i32(3)][i32(2)], gtao_0.viewMatrix_0.data_0[i32(0)][i32(3)], gtao_0.viewMatrix_0.data_0[i32(1)][i32(3)], gtao_0.viewMatrix_0.data_0[i32(2)][i32(3)], gtao_0.viewMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    }
    var texelSize_0 : vec2<f32> = vec2<f32>(1.0f) / gtao_0.depthSize_0;
    var _S10 : vec2<f32> = vec2<f32>(0.0f);
    var _S11 : vec2<f32> = vec2<f32>(1.0f);
    var uvX_0 : vec2<f32> = clamp(uv_2 + vec2<f32>(texelSize_0.x, 0.0f), _S10, _S11);
    var uvY_0 : vec2<f32> = clamp(uv_2 + vec2<f32>(0.0f, texelSize_0.y), _S10, _S11);
    var depthX_0 : f32 = readDepth_0(uvX_0);
    var depthY_0 : f32 = readDepth_0(uvY_0);
    var _S12 : bool;
    if(isFarDepth_0(depthX_0))
    {
        _S12 = true;
    }
    else
    {
        _S12 = isFarDepth_0(depthY_0);
    }
    if(_S12)
    {
        return vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    return normalize(cross(viewPosFromDepth_0(depthX_0, uvX_0) - centerPos_0, centerPos_0 - viewPosFromDepth_0(depthY_0, uvY_0)));
}

fn interleavedGradientNoise_0( pixel_0 : vec2<f32>) -> f32
{
    return fract(52.98291778564453125f * fract(dot(pixel_0, vec2<f32>(0.06711056083440781f, 0.00583714991807938f))));
}

fn sampleFalloff_0( delta_0 : vec3<f32>,  dist_0 : f32,  viewDir_0 : vec3<f32>,  falloffStart_0 : f32,  radius_1 : f32,  thicknessLimit_0 : f32) -> f32
{
    var t_1 : f32 = saturate((dist_0 - falloffStart_0) / max(radius_1 - falloffStart_0, 0.00000999999974738f));
    var falloff_0 : f32 = 1.0f - t_1 * t_1 * (3.0f - 2.0f * t_1);
    var _S13 : f32 = max(0.0f, - dot(delta_0, viewDir_0));
    var falloff_1 : f32;
    if(thicknessLimit_0 > 0.0f)
    {
        var q_0 : f32 = saturate(_S13 / thicknessLimit_0);
        falloff_1 = falloff_0 * (1.0f - q_0 * q_0 * (3.0f - 2.0f * q_0));
    }
    else
    {
        falloff_1 = falloff_0;
    }
    return saturate(falloff_1);
}

fn fastAcos_0( x_0 : f32) -> f32
{
    var ax_0 : f32 = abs(x_0);
    var r_0 : f32 = (((-0.01872929930686951f * ax_0 + 0.07426100224256516f) * ax_0 - 0.21211439371109009f) * ax_0 + 1.57072877883911133f) * sqrt(max(1.0f - ax_0, 0.0f));
    var _S14 : f32;
    if(x_0 < 0.0f)
    {
        _S14 = 3.14159274101257324f - r_0;
    }
    else
    {
        _S14 = r_0;
    }
    return _S14;
}

fn updateSectors_0( minHorizon_0 : f32,  maxHorizon_0 : f32,  bits_0 : u32) -> u32
{
    var low_0 : f32 = clamp(min(minHorizon_0, maxHorizon_0), 0.0f, 1.0f);
    var high_0 : f32 = clamp(max(minHorizon_0, maxHorizon_0), 0.0f, 1.0f);
    if(high_0 <= low_0)
    {
        return bits_0;
    }
    var _S15 : u32 = min(u32(low_0 * 32.0f), u32(31));
    var _S16 : u32 = min(max(u32(ceil((high_0 - low_0) * 32.0f)), u32(1)), u32(32) - _S15);
    var range_0 : u32;
    if(_S16 >= u32(32))
    {
        range_0 = u32(4294967295);
    }
    else
    {
        range_0 = (u32(4294967295) >> ((u32(32) - _S16)));
    }
    return (bits_0 | (((range_0 << (_S15)))));
}

fn accumulateSectors_0( delta_1 : vec3<f32>,  viewDir_1 : vec3<f32>,  normalAngle_0 : f32,  direction_0 : f32,  thickness_0 : f32,  bits_1 : u32) -> u32
{
    if((dot(delta_1, delta_1)) <= 9.99999993922529029e-09f)
    {
        return bits_1;
    }
    var backface_0 : vec3<f32> = delta_1 - viewDir_1 * vec3<f32>(thickness_0);
    if((dot(backface_0, backface_0)) <= 9.99999993922529029e-09f)
    {
        return bits_1;
    }
    var horizon_0 : vec2<f32> = saturate((vec2<f32>(direction_0) * (vec2<f32>(0) - vec2<f32>(fastAcos_0(clamp(dot(normalize(delta_1), viewDir_1), -1.0f, 1.0f)), fastAcos_0(clamp(dot(normalize(backface_0), viewDir_1), -1.0f, 1.0f)))) - vec2<f32>(normalAngle_0) + vec2<f32>(1.57079637050628662f)) / vec2<f32>(3.14159274101257324f));
    var horizon_1 : vec2<f32>;
    if(direction_0 >= 0.0f)
    {
        horizon_1 = horizon_0.yx;
    }
    else
    {
        horizon_1 = horizon_0;
    }
    var biasOffset_0 : f32 = clamp(gtao_0.bias_0 / 3.14159274101257324f, 0.0f, 0.49900001287460327f);
    var _S17 : f32 = horizon_1.x;
    var _S18 : f32 = horizon_1.y;
    return updateSectors_0(min(_S17, _S18) + biasOffset_0, max(_S17, _S18) - biasOffset_0, bits_1);
}

fn accumulateSample_0( sampleUV_0 : vec2<f32>,  centerPos_1 : vec3<f32>,  viewDir_2 : vec3<f32>,  slicePlaneN_0 : vec3<f32>,  normalAngle_1 : f32,  direction_1 : f32,  radius_2 : f32,  falloffStart_1 : f32,  thicknessLimit_1 : f32,  bitmaskThickness_0 : f32,  occludedSectors_0 : ptr<function, u32>,  occludedWeight_0 : ptr<function, f32>,  horizon_2 : ptr<function, f32>)
{
    var _S19 : bool;
    if((any((sampleUV_0 <= vec2<f32>(0.0f)))))
    {
        _S19 = true;
    }
    else
    {
        _S19 = (any((sampleUV_0 >= vec2<f32>(1.0f))));
    }
    if(_S19)
    {
        return;
    }
    var depth_2 : f32 = readDepth_0(sampleUV_0);
    if(isFarDepth_0(depth_2))
    {
        return;
    }
    var delta_2 : vec3<f32> = viewPosFromDepth_0(depth_2, sampleUV_0) - centerPos_1;
    var dist_1 : f32 = length(delta_2);
    if(dist_1 <= 0.00009999999747379f)
    {
        _S19 = true;
    }
    else
    {
        _S19 = dist_1 > radius_2;
    }
    if(_S19)
    {
        return;
    }
    var falloff_2 : f32 = sampleFalloff_0(delta_2, dist_1, viewDir_2, falloffStart_1, radius_2, thicknessLimit_1);
    if((gtao_0.useVisibilityBitmask_0) != i32(0))
    {
        if(falloff_2 <= 0.00999999977648258f)
        {
            return;
        }
        var updated_0 : u32 = accumulateSectors_0(delta_2, viewDir_2, normalAngle_1, direction_1, bitmaskThickness_0 * falloff_2, (*occludedSectors_0));
        (*occludedWeight_0) = (*occludedWeight_0) + f32(countOneBits((updated_0 & ((~(*occludedSectors_0)))))) * falloff_2;
        (*occludedSectors_0) = updated_0;
    }
    else
    {
        var deltaDir_0 : vec3<f32> = delta_2 / vec3<f32>(dist_1);
        var projected_0 : vec3<f32> = deltaDir_0 - slicePlaneN_0 * vec3<f32>(dot(deltaDir_0, slicePlaneN_0));
        var projectedLength_0 : f32 = length(projected_0);
        if(projectedLength_0 <= 0.00000999999974738f)
        {
            return;
        }
        (*horizon_2) = min((*horizon_2), clamp(mix(1.57079637050628662f, fastAcos_0(clamp(dot(projected_0 / vec3<f32>(projectedLength_0), viewDir_2), 0.0f, 1.0f)), falloff_2) + gtao_0.bias_0, 0.0f, 1.57079637050628662f));
    }
    return;
}

fn integrateArc_0( h_0 : f32,  n_1 : f32) -> f32
{
    var nc_0 : f32 = clamp(n_1, -1.57079637050628662f, 1.57079637050628662f);
    var _S20 : f32 = 2.0f * clamp(h_0, 0.0f, 1.57079637050628662f);
    return 0.25f * (- cos(_S20 - nc_0) + cos(nc_0) + _S20 * sin(nc_0));
}

fn edgeFade_0( uv_3 : vec2<f32>,  radiusPixels_0 : f32) -> f32
{
    var edgePixels_0 : vec2<f32> = min(uv_3, vec2<f32>(1.0f) - uv_3) * gtao_0.depthSize_0;
    var t_2 : f32 = saturate(min(edgePixels_0.x, edgePixels_0.y) / clamp(radiusPixels_0 * 0.07999999821186066f, 2.0f, 8.0f));
    return t_2 * t_2 * (3.0f - 2.0f * t_2);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn gtaoFragment(@builtin(position) position_1 : vec4<f32>) -> pixelOutput_0
{
    var _S21 : vec2<f32> = position_1.xy;
    var uv_4 : vec2<f32> = saturate((_S21 - gtao_0.outputOrigin_0) / max(gtao_0.outputSize_0, vec2<f32>(1.0f)));
    var depth_3 : f32 = readDepth_0(uv_4);
    if(isFarDepth_0(depth_3))
    {
        var _S22 : pixelOutput_0 = pixelOutput_0( vec4<f32>(1.0f, 1.0f, 1.0f, 1.0f) );
        return _S22;
    }
    var centerPos_2 : vec3<f32> = viewPosFromDepth_0(depth_3, uv_4);
    var _S23 : vec3<f32> = getViewNormal_0(uv_4, centerPos_2);
    var _S24 : vec3<f32> = normalize((vec3<f32>(0) - centerPos_2));
    var _S25 : f32 = max(gtao_0.radius_0, 0.00100000004749745f);
    var radiusPixels_1 : f32 = clamp(_S25 * abs(gtao_0.projMatrix_0.data_0[i32(1)][i32(1)]) * 0.5f * gtao_0.depthSize_0.y / max(abs(centerPos_2.z), 0.00100000004749745f), 1.0f, 192.0f);
    var _S26 : f32 = saturate(gtao_0.falloffStartRatio_0) * _S25;
    var _S27 : f32 = _S25 * saturate(gtao_0.thicknessHeuristic_0);
    var _S28 : f32 = max(gtao_0.visibilityBitmaskThickness_0, 0.00009999999747379f);
    var slices_0 : i32 = clamp(gtao_0.sliceCount_0, i32(1), i32(8));
    var _S29 : i32 = clamp(gtao_0.stepsPerSlice_0, i32(1), i32(16));
    var noisePixel_0 : vec2<f32> = floor(_S21 - gtao_0.outputOrigin_0);
    var _S30 : f32 = f32(slices_0);
    var _S31 : f32 = (interleavedGradientNoise_0(noisePixel_0) - 0.5f) * 3.14159274101257324f / _S30;
    var _S32 : f32 = interleavedGradientNoise_0(noisePixel_0 + vec2<f32>(17.0f, 43.0f));
    var sliceIndex_0 : i32 = i32(0);
    var visibility_0 : f32 = 0.0f;
    for(;;)
    {
        if(sliceIndex_0 < slices_0)
        {
        }
        else
        {
            break;
        }
        var phi_0 : f32 = _S31 + 3.14159274101257324f * f32(sliceIndex_0) / _S30;
        var _S33 : f32 = cos(phi_0);
        var _S34 : f32 = sin(phi_0);
        var screenDir_0 : vec2<f32> = vec2<f32>(_S33, _S34);
        var tangent_0 : vec3<f32> = normalize(vec3<f32>(_S33 / gtao_0.projMatrix_0.data_0[i32(0)][i32(0)], - _S34 / gtao_0.projMatrix_0.data_0[i32(1)][i32(1)], 0.0f));
        var tangent_1 : vec3<f32> = normalize(tangent_0 - _S24 * vec3<f32>(dot(tangent_0, _S24)));
        var slicePlaneN_1 : vec3<f32> = normalize(cross(_S24, tangent_1));
        var projectedNormal_0 : vec3<f32> = _S23 - slicePlaneN_1 * vec3<f32>(dot(_S23, slicePlaneN_1));
        var projectedNormalLength_0 : f32 = length(projectedNormal_0);
        if(projectedNormalLength_0 <= 0.00000999999974738f)
        {
            visibility_0 = visibility_0 + 1.0f;
            sliceIndex_0 = sliceIndex_0 + i32(1);
            continue;
        }
        var projectedNormal_1 : vec3<f32> = projectedNormal_0 / vec3<f32>(projectedNormalLength_0);
        var gamma_0 : f32 = atan2(dot(projectedNormal_1, tangent_1), dot(projectedNormal_1, _S24));
        var _S35 : f32 = - gamma_0;
        var horizonForward_0 : f32 = 1.57079637050628662f;
        var horizonBackward_0 : f32 = 1.57079637050628662f;
        var sectors_0 : u32 = u32(0);
        var sectorWeight_0 : f32 = 0.0f;
        var step_0 : i32 = i32(0);
        for(;;)
        {
            if(step_0 < _S29)
            {
            }
            else
            {
                break;
            }
            var offset_0 : vec2<f32> = screenDir_0 * vec2<f32>(radiusPixels_1) * vec2<f32>(((f32(step_0) + _S32) / f32(_S29))) / gtao_0.depthSize_0;
            accumulateSample_0(uv_4 + offset_0, centerPos_2, _S24, slicePlaneN_1, _S35, 1.0f, _S25, _S26, _S27, _S28, &(sectors_0), &(sectorWeight_0), &(horizonForward_0));
            accumulateSample_0(uv_4 - offset_0, centerPos_2, _S24, slicePlaneN_1, _S35, -1.0f, _S25, _S26, _S27, _S28, &(sectors_0), &(sectorWeight_0), &(horizonBackward_0));
            step_0 = step_0 + i32(1);
        }
        var sliceVisibility_0 : f32;
        if((gtao_0.useVisibilityBitmask_0) != i32(0))
        {
            sliceVisibility_0 = 1.0f - sectorWeight_0 / 32.0f;
        }
        else
        {
            sliceVisibility_0 = projectedNormalLength_0 * (integrateArc_0(horizonForward_0, gamma_0) + integrateArc_0(horizonBackward_0, _S35));
        }
        visibility_0 = visibility_0 + saturate(sliceVisibility_0);
        sliceIndex_0 = sliceIndex_0 + i32(1);
    }
    var visibility_1 : f32 = mix(1.0f, saturate(visibility_0 / _S30), edgeFade_0(uv_4, radiusPixels_1));
    var _S36 : pixelOutput_0 = pixelOutput_0( vec4<f32>(visibility_1, visibility_1, visibility_1, 1.0f) );
    return _S36;
}

