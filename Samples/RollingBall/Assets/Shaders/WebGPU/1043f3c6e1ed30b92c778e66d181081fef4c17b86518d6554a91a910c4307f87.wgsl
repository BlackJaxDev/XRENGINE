struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct SkyViewUniforms_std140_0
{
    @align(16) inverseView_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) inverseProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : SkyViewUniforms_std140_0;
struct SkyMaterialUniforms_std140_0
{
    @align(16) intensity_0 : f32,
    @align(4) rotation_0 : f32,
    @align(8) timeOfDay_0 : f32,
    @align(4) cloudCoverage_0 : f32,
    @align(16) topColor_0 : vec3<f32>,
    @align(4) cloudScale_0 : f32,
    @align(16) bottomColor_0 : vec3<f32>,
    @align(4) cloudSpeed_0 : f32,
    @align(16) cloudSharpness_0 : f32,
    @align(4) starIntensity_0 : f32,
    @align(8) horizonHaze_0 : f32,
    @align(4) sunDiscSize_0 : f32,
    @align(16) moonDiscSize_0 : f32,
    @align(4) cameraTwinkle_0 : f32,
    @align(8) twinklePhase_0 : f32,
};

@binding(0) @group(1) var<uniform> sky_0 : SkyMaterialUniforms_std140_0;
fn isinf_0( x_0 : f32) -> bool
{
    var _S1 : u32 = (bitcast<u32>((x_0)));
    var _S2 : u32 = (_S1 & (u32(8388607)));
    var _S3 : bool;
    if(((((_S1 >> (u32(23)))) & (u32(255)))) == u32(255))
    {
        _S3 = _S2 == u32(0);
    }
    else
    {
        _S3 = false;
    }
    return _S3;
}

fn isnan_0( x_1 : f32) -> bool
{
    var _S4 : u32 = (bitcast<u32>((x_1)));
    var _S5 : u32 = (_S4 & (u32(8388607)));
    var _S6 : bool;
    if(((((_S4 >> (u32(23)))) & (u32(255)))) == u32(255))
    {
        _S6 = _S5 != u32(0);
    }
    else
    {
        _S6 = false;
    }
    return _S6;
}

fn isinf_1( x_2 : vec3<f32>) -> vec3<bool>
{
    var result_0 : vec3<bool>;
    var i_0 : i32 = i32(0);
    for(;;)
    {
        if(i_0 < i32(3))
        {
        }
        else
        {
            break;
        }
        result_0[i_0] = isinf_0(x_2[i_0]);
        i_0 = i_0 + i32(1);
    }
    return result_0;
}

fn isnan_1( x_3 : vec3<f32>) -> vec3<bool>
{
    var result_1 : vec3<bool>;
    var i_1 : i32 = i32(0);
    for(;;)
    {
        if(i_1 < i32(3))
        {
        }
        else
        {
            break;
        }
        result_1[i_1] = isnan_0(x_3[i_1]);
        i_1 = i_1 + i32(1);
    }
    return result_1;
}

fn rsqrt_0( x_4 : f32) -> f32
{
    return 1.0f / sqrt(x_4);
}

struct SkyVertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) worldDirection_0 : vec3<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn skyVertex( _S7 : vertexInput_0) -> SkyVertexOutput_0
{
    var output_0 : SkyVertexOutput_0;
    var _S8 : vec4<f32> = vec4<f32>(_S7.position_1.xy, 1.0f, 1.0f);
    var viewPosition_0 : vec4<f32> = (((_S8) * (mat4x4<f32>(view_0.inverseProjection_0.data_0[i32(0)][i32(0)], view_0.inverseProjection_0.data_0[i32(1)][i32(0)], view_0.inverseProjection_0.data_0[i32(2)][i32(0)], view_0.inverseProjection_0.data_0[i32(3)][i32(0)], view_0.inverseProjection_0.data_0[i32(0)][i32(1)], view_0.inverseProjection_0.data_0[i32(1)][i32(1)], view_0.inverseProjection_0.data_0[i32(2)][i32(1)], view_0.inverseProjection_0.data_0[i32(3)][i32(1)], view_0.inverseProjection_0.data_0[i32(0)][i32(2)], view_0.inverseProjection_0.data_0[i32(1)][i32(2)], view_0.inverseProjection_0.data_0[i32(2)][i32(2)], view_0.inverseProjection_0.data_0[i32(3)][i32(2)], view_0.inverseProjection_0.data_0[i32(0)][i32(3)], view_0.inverseProjection_0.data_0[i32(1)][i32(3)], view_0.inverseProjection_0.data_0[i32(2)][i32(3)], view_0.inverseProjection_0.data_0[i32(3)][i32(3)]))));
    var _S9 : f32 = viewPosition_0.w;
    var invW_0 : f32;
    if((abs(_S9)) > 9.99999997475242708e-07f)
    {
        invW_0 = 1.0f / _S9;
    }
    else
    {
        invW_0 = 1.0f;
    }
    var direction_0 : vec3<f32> = (((vec4<f32>(viewPosition_0.xyz * vec3<f32>(invW_0), 0.0f)) * (mat4x4<f32>(view_0.inverseView_0.data_0[i32(0)][i32(0)], view_0.inverseView_0.data_0[i32(1)][i32(0)], view_0.inverseView_0.data_0[i32(2)][i32(0)], view_0.inverseView_0.data_0[i32(3)][i32(0)], view_0.inverseView_0.data_0[i32(0)][i32(1)], view_0.inverseView_0.data_0[i32(1)][i32(1)], view_0.inverseView_0.data_0[i32(2)][i32(1)], view_0.inverseView_0.data_0[i32(3)][i32(1)], view_0.inverseView_0.data_0[i32(0)][i32(2)], view_0.inverseView_0.data_0[i32(1)][i32(2)], view_0.inverseView_0.data_0[i32(2)][i32(2)], view_0.inverseView_0.data_0[i32(3)][i32(2)], view_0.inverseView_0.data_0[i32(0)][i32(3)], view_0.inverseView_0.data_0[i32(1)][i32(3)], view_0.inverseView_0.data_0[i32(2)][i32(3)], view_0.inverseView_0.data_0[i32(3)][i32(3)])))).xyz;
    var cosRotation_0 : f32 = cos(sky_0.rotation_0);
    var sinRotation_0 : f32 = sin(sky_0.rotation_0);
    var _S10 : f32 = direction_0.x;
    var _S11 : f32 = direction_0.z;
    output_0.worldDirection_0 = vec3<f32>(_S10 * cosRotation_0 - _S11 * sinRotation_0, direction_0.y, _S10 * sinRotation_0 + _S11 * cosRotation_0);
    output_0.position_0 = _S8;
    return output_0;
}

fn SafeNormalize3_0( v_0 : vec3<f32>) -> vec3<f32>
{
    var lenSq_0 : f32 = dot(v_0, v_0);
    var _S12 : vec3<f32>;
    if(lenSq_0 > 9.99999993922529029e-09f)
    {
        _S12 = v_0 * vec3<f32>(rsqrt_0(lenSq_0));
    }
    else
    {
        _S12 = vec3<f32>(0.0f, 1.0f, 0.0f);
    }
    return _S12;
}

fn Atmosphere_0( viewDir_0 : vec3<f32>,  sunDir_0 : vec3<f32>) -> vec3<f32>
{
    var _S13 : f32 = sunDir_0.y;
    var mu_0 : f32 = dot(viewDir_0, sunDir_0);
    const rayleighCoeff_0 : vec3<f32> = vec3<f32>(0.57999998331069946f, 1.35000002384185791f, 3.30999994277954102f);
    var mieCoeff_0 : vec3<f32> = vec3<f32>(2.09999990463256836f);
    var _S14 : vec3<f32> = (vec3<f32>(0) - rayleighCoeff_0);
    var _S15 : vec3<f32> = vec3<f32>((1.0f / (max(_S13, -0.10000000149011612f) + 0.15000000596046448f)));
    var sunExt_0 : vec3<f32> = exp(_S14 * _S15 * vec3<f32>(0.2199999988079071f) - mieCoeff_0 * _S15 * vec3<f32>(0.07999999821186066f));
    var _S16 : vec3<f32> = vec3<f32>((1.0f / (max(viewDir_0.y, -0.10000000149011612f) + 0.15000000596046448f)));
    var _S17 : vec3<f32> = vec3<f32>(1.0f) - exp(_S14 * _S16 * vec3<f32>(0.10000000149011612f) - mieCoeff_0 * _S16 * vec3<f32>(0.05999999865889549f));
    return (rayleighCoeff_0 * vec3<f32>((0.75f * (1.0f + mu_0 * mu_0))) * sunExt_0 * _S17 + mieCoeff_0 * vec3<f32>((0.42239999771118164f / pow(max(1.57760000228881836f - 1.51999998092651367f * mu_0, 0.00009999999747379f), 1.5f) * 0.05970000103116035f)) * sunExt_0 * _S17 * vec3<f32>(0.34999999403953552f)) * vec3<f32>(smoothstep(-0.14000000059604645f, 0.25f, _S13));
}

fn SafeNormalize2_0( v_1 : vec2<f32>) -> vec2<f32>
{
    var lenSq_1 : f32 = dot(v_1, v_1);
    var _S18 : vec2<f32>;
    if(lenSq_1 > 9.99999993922529029e-09f)
    {
        _S18 = v_1 * vec2<f32>(rsqrt_0(lenSq_1));
    }
    else
    {
        _S18 = vec2<f32>(0.0f, 1.0f);
    }
    return _S18;
}

fn Hash3_0( p_0 : vec3<f32>) -> f32
{
    var _S19 : vec3<f32> = fract(p_0 * vec3<f32>(443.897491455078125f, 397.29730224609375f, 491.187103271484375f));
    var _S20 : vec3<f32> = _S19 + vec3<f32>(dot(_S19, _S19.yzx + vec3<f32>(19.19000053405761719f)));
    return fract((_S20.x + _S20.y) * _S20.z);
}

fn MotionStarTwinkle_0( cell_0 : vec3<f32>,  seed_0 : f32,  amount_0 : f32,  speed_0 : f32) -> f32
{
    var motion_0 : f32 = clamp(sky_0.cameraTwinkle_0, 0.0f, 1.0f);
    if(motion_0 <= 0.00100000004749745f)
    {
        return 1.0f;
    }
    var phase_0 : f32 = sky_0.twinklePhase_0 * speed_0 + seed_0 * 13.36999988555908203f;
    var _S21 : vec3<f32> = cell_0 + vec3<f32>(seed_0 * 31.0f + 7.0f, seed_0 * 17.0f + 3.0f, floor(phase_0));
    return 1.0f + (mix(Hash3_0(_S21), Hash3_0(_S21 + vec3<f32>(0.0f, 0.0f, 1.0f)), smoothstep(0.0f, 1.0f, fract(phase_0))) - 0.5f) * amount_0 * motion_0;
}

fn Noise3_0( p_1 : vec3<f32>) -> f32
{
    var i_2 : vec3<f32> = floor(p_1);
    var f_0 : vec3<f32> = fract(p_1);
    var u_0 : vec3<f32> = f_0 * f_0 * (vec3<f32>(3.0f) - vec3<f32>(2.0f) * f_0);
    var _S22 : f32 = u_0.x;
    var _S23 : f32 = u_0.y;
    return mix(mix(mix(Hash3_0(i_2), Hash3_0(i_2 + vec3<f32>(1.0f, 0.0f, 0.0f)), _S22), mix(Hash3_0(i_2 + vec3<f32>(0.0f, 1.0f, 0.0f)), Hash3_0(i_2 + vec3<f32>(1.0f, 1.0f, 0.0f)), _S22), _S23), mix(mix(Hash3_0(i_2 + vec3<f32>(0.0f, 0.0f, 1.0f)), Hash3_0(i_2 + vec3<f32>(1.0f, 0.0f, 1.0f)), _S22), mix(Hash3_0(i_2 + vec3<f32>(0.0f, 1.0f, 1.0f)), Hash3_0(i_2 + vec3<f32>(1.0f, 1.0f, 1.0f)), _S22), _S23), u_0.z);
}

fn Fbm3_0( p_2 : vec3<f32>) -> f32
{
    var _S24 : vec3<f32> = p_2;
    var i_3 : i32 = i32(0);
    var a_0 : f32 = 0.5f;
    var v_2 : f32 = 0.0f;
    for(;;)
    {
        if(i_3 < i32(5))
        {
        }
        else
        {
            break;
        }
        var v_3 : f32 = v_2 + a_0 * Noise3_0(_S24);
        var _S25 : vec3<f32> = _S24 * vec3<f32>(2.02999997138977051f) + vec3<f32>(17.0f, 11.0f, 5.30000019073486328f);
        var a_1 : f32 = a_0 * 0.5f;
        var i_4 : i32 = i_3 + i32(1);
        _S24 = _S25;
        i_3 = i_4;
        a_0 = a_1;
        v_2 = v_3;
    }
    return v_2;
}

fn Fbm3_6_0( p_3 : vec3<f32>) -> f32
{
    var _S26 : vec3<f32> = p_3;
    var i_5 : i32 = i32(0);
    var a_2 : f32 = 0.5f;
    var v_4 : f32 = 0.0f;
    for(;;)
    {
        if(i_5 < i32(6))
        {
        }
        else
        {
            break;
        }
        var v_5 : f32 = v_4 + a_2 * Noise3_0(_S26);
        var _S27 : vec3<f32> = _S26 * vec3<f32>(2.17000007629394531f) + vec3<f32>(4.30000019073486328f, 9.10000038146972656f, 2.70000004768371582f);
        var a_3 : f32 = a_2 * 0.55000001192092896f;
        var i_6 : i32 = i_5 + i32(1);
        _S26 = _S27;
        i_5 = i_6;
        a_2 = a_3;
        v_4 = v_5;
    }
    return v_4;
}

fn Hash_0( p_4 : vec2<f32>) -> f32
{
    return fract(sin(dot(p_4, vec2<f32>(127.09999847412109375f, 311.70001220703125f))) * 43758.546875f);
}

fn Noise_0( p_5 : vec2<f32>) -> f32
{
    var i_7 : vec2<f32> = floor(p_5);
    var f_1 : vec2<f32> = fract(p_5);
    var u_1 : vec2<f32> = f_1 * f_1 * (vec2<f32>(3.0f) - vec2<f32>(2.0f) * f_1);
    var _S28 : f32 = u_1.x;
    return mix(mix(Hash_0(i_7), Hash_0(i_7 + vec2<f32>(1.0f, 0.0f)), _S28), mix(Hash_0(i_7 + vec2<f32>(0.0f, 1.0f)), Hash_0(i_7 + vec2<f32>(1.0f, 1.0f)), _S28), u_1.y);
}

fn Fbm_0( p_6 : vec2<f32>) -> f32
{
    var _S29 : vec2<f32> = p_6;
    var i_8 : i32 = i32(0);
    var a_4 : f32 = 0.5f;
    var v_6 : f32 = 0.0f;
    for(;;)
    {
        if(i_8 < i32(5))
        {
        }
        else
        {
            break;
        }
        var v_7 : f32 = v_6 + a_4 * Noise_0(_S29);
        var _S30 : vec2<f32> = _S29 * vec2<f32>(2.02999997138977051f) + vec2<f32>(17.0f, 11.0f);
        var a_5 : f32 = a_4 * 0.5f;
        var i_9 : i32 = i_8 + i32(1);
        _S29 = _S30;
        i_8 = i_9;
        a_4 = a_5;
        v_6 = v_7;
    }
    return v_6;
}

fn Fbm6_0( p_7 : vec2<f32>) -> f32
{
    var _S31 : vec2<f32> = p_7;
    var i_10 : i32 = i32(0);
    var a_6 : f32 = 0.5f;
    var v_8 : f32 = 0.0f;
    for(;;)
    {
        if(i_10 < i32(6))
        {
        }
        else
        {
            break;
        }
        var v_9 : f32 = v_8 + a_6 * Noise_0(_S31);
        var _S32 : vec2<f32> = _S31 * vec2<f32>(2.17000007629394531f) + vec2<f32>(4.30000019073486328f, 9.10000038146972656f);
        var a_7 : f32 = a_6 * 0.55000001192092896f;
        var i_11 : i32 = i_10 + i32(1);
        _S31 = _S32;
        i_10 = i_11;
        a_6 = a_7;
        v_8 = v_9;
    }
    return v_8;
}

fn ShadeMoon_0( dir_0 : vec3<f32>,  moonDir_0 : vec3<f32>,  sunDir_1 : vec3<f32>,  nightFactor_0 : f32) -> vec3<f32>
{
    var up_0 : vec3<f32>;
    if((abs(moonDir_0.y)) < 0.94999998807907104f)
    {
        up_0 = vec3<f32>(0.0f, 1.0f, 0.0f);
    }
    else
    {
        up_0 = vec3<f32>(1.0f, 0.0f, 0.0f);
    }
    var tangent_0 : vec3<f32> = SafeNormalize3_0(cross(up_0, moonDir_0));
    var bitangent_0 : vec3<f32> = cross(moonDir_0, tangent_0);
    var d_0 : f32 = dot(dir_0, moonDir_0);
    if(d_0 <= (sky_0.moonDiscSize_0))
    {
        var _S33 : f32 = max(d_0, 0.0f);
        return vec3<f32>(0.74000000953674316f, 0.79000002145767212f, 0.93999999761581421f) * vec3<f32>((pow(_S33, 96.0f) * 0.34999999403953552f + pow(_S33, 24.0f) * 0.05000000074505806f)) * vec3<f32>(nightFactor_0);
    }
    var _S34 : f32 = max(d_0, 0.00100000004749745f);
    var t_0 : f32 = dot(dir_0, tangent_0) / _S34;
    var b_0 : f32 = dot(dir_0, bitangent_0) / _S34;
    var discRadius_0 : f32 = sqrt(max(1.0f - sky_0.moonDiscSize_0 * sky_0.moonDiscSize_0, 9.99999997475242708e-07f)) * 1.14999997615814209f;
    var local_0 : vec2<f32> = vec2<f32>(t_0, b_0);
    var rn_0 : f32 = clamp(dot(local_0, local_0) / (discRadius_0 * discRadius_0), 0.0f, 1.0f);
    var surfUv_0 : vec2<f32> = local_0 / vec2<f32>(discRadius_0);
    var _S35 : f32 = max(d_0, 0.0f);
    return (mix(vec3<f32>(0.81999999284744263f, 0.82999998331069946f, 0.86000001430511475f), vec3<f32>(0.44999998807907104f, 0.49000000953674316f, 0.57999998331069946f), vec3<f32>(smoothstep(0.40000000596046448f, 0.57999998331069946f, Fbm_0(surfUv_0 * vec2<f32>(2.09999990463256836f) + vec2<f32>(7.30000019073486328f, 1.89999997615814209f))))) * vec3<f32>(mix(0.72000002861022949f, 1.0f, Fbm6_0(surfUv_0 * vec2<f32>(5.5f)) * 0.5f + 0.5f)) * vec3<f32>((0.05999999865889549f + clamp(dot(SafeNormalize3_0(tangent_0 * vec3<f32>((t_0 / discRadius_0)) + bitangent_0 * vec3<f32>((b_0 / discRadius_0)) + moonDir_0 * vec3<f32>(sqrt(max(1.0f - rn_0, 0.0f)))), sunDir_1), 0.0f, 1.0f) * 1.14999997615814209f)) * vec3<f32>(smoothstep(1.0f, 0.85000002384185791f, rn_0)) + vec3<f32>(0.74000000953674316f, 0.79000002145767212f, 0.93999999761581421f) * vec3<f32>((pow(_S35, 96.0f) * 0.40000000596046448f + pow(_S35, 24.0f) * 0.05999999865889549f))) * vec3<f32>(nightFactor_0);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) worldDirection_1 : vec3<f32>,
};

@fragment
fn skyFragment( _S36 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var dir_1 : vec3<f32> = normalize(_S36.worldDirection_1);
    var angle_0 : f32 = sky_0.timeOfDay_0 * 6.28318548202514648f;
    var sunDir_2 : vec3<f32> = SafeNormalize3_0(vec3<f32>(cos(angle_0), sin(angle_0), 0.18000000715255737f));
    var moonAngle_0 : f32 = angle_0 + 3.14159274101257324f + 0.25f * sin(sky_0.timeOfDay_0 * 6.28318548202514648f * 0.30000001192092896f);
    var moonDir_1 : vec3<f32> = SafeNormalize3_0(vec3<f32>(cos(moonAngle_0), sin(moonAngle_0), -0.2199999988079071f));
    var _S37 : f32 = sunDir_2.y;
    var dayFactor_0 : f32 = smoothstep(-0.18000000715255737f, 0.11999999731779099f, _S37);
    var nightFactor_1 : f32 = 1.0f - dayFactor_0;
    var duskFactor_0 : f32 = clamp(exp(- _S37 * _S37 * 38.0f), 0.0f, 1.0f);
    var _S38 : f32 = dir_1.y;
    var horizonT_0 : f32 = 1.0f - clamp(abs(_S38), 0.0f, 1.0f);
    var _S39 : vec3<f32> = vec3<f32>(duskFactor_0);
    var color_0 : vec3<f32> = (Atmosphere_0(dir_1, sunDir_2) + vec3<f32>(1.14999997615814209f, 0.55000001192092896f, 0.2199999988079071f) * vec3<f32>(pow(horizonT_0, 3.0f)) * _S39 * vec3<f32>(sky_0.horizonHaze_0) + vec3<f32>(0.77999997138977051f, 0.55000001192092896f, 0.68000000715255737f) * vec3<f32>(pow(horizonT_0, 2.20000004768371582f)) * vec3<f32>(max(- dot(SafeNormalize2_0(vec2<f32>(dir_1.x, dir_1.z)), SafeNormalize2_0(vec2<f32>(sunDir_2.x, sunDir_2.z))), 0.0f)) * _S39 * vec3<f32>(0.44999998807907104f)) * mix(vec3<f32>(1.0f), vec3<f32>(1.20000004768371582f, 0.87999999523162842f, 0.77999997138977051f), vec3<f32>((duskFactor_0 * 0.55000001192092896f)));
    var _S40 : vec3<f32> = vec3<f32>(0.0f);
    var _S41 : bool;
    if(nightFactor_1 > 0.00100000004749745f)
    {
        _S41 = (sky_0.starIntensity_0) > 0.00100000004749745f;
    }
    else
    {
        _S41 = false;
    }
    var starField_0 : vec3<f32>;
    if(_S41)
    {
        var starCell_0 : vec3<f32> = floor(dir_1 * vec3<f32>(256.0f));
        var starHash_0 : f32 = Hash3_0(starCell_0);
        var bigP_0 : vec3<f32> = dir_1 * vec3<f32>(64.0f);
        var bigCell_0 : vec3<f32> = floor(bigP_0);
        var bigHash_0 : f32 = Hash3_0(bigCell_0);
        var bigOffset_0 : vec3<f32> = fract(bigP_0) - vec3<f32>(0.5f);
        var galacticUp_0 : vec3<f32> = SafeNormalize3_0(vec3<f32>(0.34999999403953552f, 0.2199999988079071f, 0.9100000262260437f));
        var bandCoord_0 : f32 = dot(dir_1, galacticUp_0);
        var _S42 : vec3<f32> = vec3<f32>(smoothstep(0.34999999403953552f, 0.94999998807907104f, Fbm3_0(dir_1 * vec3<f32>(5.19999980926513672f) + galacticUp_0 * vec3<f32>(3.09999990463256836f) + vec3<f32>(3.09999990463256836f, 7.40000009536743164f, 1.89999997615814209f))));
        starField_0 = (vec3<f32>((step(0.99750000238418579f, starHash_0) * (0.55000001192092896f + 0.44999998807907104f * fract(starHash_0 * 17.29999923706054688f)) * MotionStarTwinkle_0(starCell_0, starHash_0, 0.15999999642372131f, 1.0f) + step(0.99699997901916504f, bigHash_0) * exp(- dot(bigOffset_0, bigOffset_0) * 60.0f) * MotionStarTwinkle_0(bigCell_0, bigHash_0, 0.10000000149011612f, 0.73000001907348633f) * 2.40000009536743164f)) * mix(vec3<f32>(0.85000002384185791f, 0.89999997615814209f, 1.10000002384185791f), vec3<f32>(1.10000002384185791f, 0.94999998807907104f, 0.77999997138977051f), vec3<f32>(Hash3_0(starCell_0 + vec3<f32>(11.0f, 23.0f, 7.0f)))) + mix(vec3<f32>(0.05999999865889549f, 0.07999999821186066f, 0.15000000596046448f), vec3<f32>(0.2199999988079071f, 0.18000000715255737f, 0.2800000011920929f), _S42) * vec3<f32>(exp(- bandCoord_0 * bandCoord_0 * 38.0f)) * _S42 * vec3<f32>(0.34999999403953552f)) * vec3<f32>(sky_0.starIntensity_0) * vec3<f32>(smoothstep(-0.05000000074505806f, 0.2199999988079071f, _S38));
    }
    else
    {
        starField_0 = _S40;
    }
    var night_0 : vec3<f32> = vec3<f32>(0.00600000005215406f, 0.00899999961256981f, 0.02199999988079071f) * vec3<f32>((0.40000000596046448f + 0.60000002384185791f * smoothstep(-0.11999999731779099f, 0.40000000596046448f, _S38))) + starField_0 * vec3<f32>(nightFactor_1);
    var color_1 : vec3<f32> = mix(night_0, color_0 + night_0 * vec3<f32>(0.15000000596046448f), vec3<f32>(clamp(dayFactor_0 + duskFactor_0 * 0.34999999403953552f, 0.0f, 1.0f)));
    var flowAngle_0 : f32 = sky_0.timeOfDay_0 * 0.34999999403953552f;
    var _S43 : vec3<f32> = SafeNormalize3_0(vec3<f32>(cos(flowAngle_0), 0.11999999731779099f, sin(flowAngle_0))) * vec3<f32>((sky_0.cloudSpeed_0 * sky_0.timeOfDay_0 * 240.0f));
    var warped_0 : vec3<f32> = dir_1 * vec3<f32>(sky_0.cloudScale_0) * vec3<f32>(3.0f) + _S43 * vec3<f32>(0.25f);
    var cloud_0 : f32 = smoothstep(1.0f - sky_0.cloudCoverage_0, 1.0f, pow(Fbm3_6_0(warped_0) * 0.81999999284744263f + Noise3_0(warped_0 * vec3<f32>(6.5f) - _S43 * vec3<f32>(0.34999999403953552f)) * 0.18000000715255737f, sky_0.cloudSharpness_0)) * smoothstep(-0.07999999821186066f, 0.11999999731779099f, _S38);
    var muSun_0 : f32 = dot(dir_1, sunDir_2);
    var _S44 : f32 = max(muSun_0, 0.0f);
    var _S45 : vec3<f32> = vec3<f32>(dayFactor_0);
    var color_2 : vec3<f32> = mix(color_1, mix(mix(mix(vec3<f32>(0.03999999910593033f, 0.05000000074505806f, 0.09000000357627869f), vec3<f32>(0.57999998331069946f, 0.62999999523162842f, 0.75f), _S45), mix(vec3<f32>(0.44999998807907104f, 0.47999998927116394f, 0.55000001192092896f), vec3<f32>(1.0f, 0.98000001907348633f, 0.93000000715255737f), _S45), vec3<f32>((1.0f - exp(- cloud_0 * 2.5f)))) + vec3<f32>(1.29999995231628418f, 0.55000001192092896f, 0.2199999988079071f) * vec3<f32>(pow(_S44, 8.0f)) * _S39 * vec3<f32>(1.20000004768371582f), color_1, vec3<f32>((smoothstep(0.5f, 0.02999999932944775f, _S38) * 0.55000001192092896f))), vec3<f32>(cloud_0)) + mix(vec3<f32>(1.39999997615814209f, 0.55000001192092896f, 0.18000000715255737f), vec3<f32>(1.0f, 0.95999997854232788f, 0.87999999523162842f), vec3<f32>(smoothstep(-0.05000000074505806f, 0.34999999403953552f, _S37))) * vec3<f32>((smoothstep(sky_0.sunDiscSize_0, 1.0f, muSun_0) * 18.0f * mix(0.55000001192092896f, 1.0f, pow(clamp((muSun_0 - sky_0.sunDiscSize_0) / max(1.0f - sky_0.sunDiscSize_0, 0.00009999999747379f), 0.0f, 1.0f), 0.5f)) + (pow(_S44, 96.0f) * 0.80000001192092896f + pow(_S44, 32.0f) * 0.11999999731779099f))) * _S45 * vec3<f32>((1.0f - cloud_0 * 0.85000002384185791f)) + ShadeMoon_0(dir_1, moonDir_1, sunDir_2, nightFactor_1) * vec3<f32>((1.0f - cloud_0 * 0.89999997615814209f));
    if((any((isnan_1(color_2)))))
    {
        _S41 = true;
    }
    else
    {
        _S41 = (any((isinf_1(color_2))));
    }
    var color_3 : vec3<f32>;
    if(_S41)
    {
        color_3 = mix(vec3<f32>(0.05000000074505806f, 0.07999999821186066f, 0.15000000596046448f), vec3<f32>(0.30000001192092896f, 0.5f, 0.85000002384185791f), vec3<f32>(clamp(_S38 * 0.5f + 0.5f, 0.0f, 1.0f)));
    }
    else
    {
        color_3 = color_2;
    }
    var _S46 : pixelOutput_0 = pixelOutput_0( vec4<f32>(max(color_3, _S40) * vec3<f32>(sky_0.intensity_0), 1.0f) );
    return _S46;
}

