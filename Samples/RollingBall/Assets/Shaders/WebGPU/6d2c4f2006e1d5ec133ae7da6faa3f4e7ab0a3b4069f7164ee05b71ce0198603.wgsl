struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct LitObjectUniforms_std140_0
{
    @align(16) modelMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) normalMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(1) @group(0) var<uniform> object_0 : LitObjectUniforms_std140_0;
struct LitViewUniforms_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) cameraPosition_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> view_0 : LitViewUniforms_std140_0;
struct LitMaterialUniforms_std140_0
{
    @align(16) baseColorOpacity_0 : vec4<f32>,
    @align(16) roughnessMetallicSpecularEmission_0 : vec4<f32>,
    @align(16) textureControls_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> material_0 : LitMaterialUniforms_std140_0;
@binding(1) @group(1) var baseColorTexture_0 : texture_2d<f32>;

@binding(2) @group(1) var baseColorSampler_0 : sampler;

@binding(7) @group(1) var roughnessTexture_0 : texture_2d<f32>;

@binding(8) @group(1) var roughnessSampler_0 : sampler;

@binding(5) @group(1) var metallicTexture_0 : texture_2d<f32>;

@binding(6) @group(1) var metallicSampler_0 : sampler;

@binding(3) @group(1) var normalTexture_0 : texture_2d<f32>;

@binding(4) @group(1) var normalSampler_0 : sampler;

struct LitLightingUniforms_std140_0
{
    @align(16) lightCounts_0 : vec4<f32>,
    @align(16) globalAmbient_0 : vec4<f32>,
    @align(16) directional0Direction_0 : vec4<f32>,
    @align(16) directional0ColorIntensity_0 : vec4<f32>,
    @align(16) directional1Direction_0 : vec4<f32>,
    @align(16) directional1ColorIntensity_0 : vec4<f32>,
    @align(16) directional2Direction_0 : vec4<f32>,
    @align(16) directional2ColorIntensity_0 : vec4<f32>,
    @align(16) directional3Direction_0 : vec4<f32>,
    @align(16) directional3ColorIntensity_0 : vec4<f32>,
    @align(16) point0PositionRadius_0 : vec4<f32>,
    @align(16) point0ColorIntensity_0 : vec4<f32>,
    @align(16) point0Brightness_0 : vec4<f32>,
    @align(16) point1PositionRadius_0 : vec4<f32>,
    @align(16) point1ColorIntensity_0 : vec4<f32>,
    @align(16) point1Brightness_0 : vec4<f32>,
    @align(16) point2PositionRadius_0 : vec4<f32>,
    @align(16) point2ColorIntensity_0 : vec4<f32>,
    @align(16) point2Brightness_0 : vec4<f32>,
    @align(16) point3PositionRadius_0 : vec4<f32>,
    @align(16) point3ColorIntensity_0 : vec4<f32>,
    @align(16) point3Brightness_0 : vec4<f32>,
    @align(16) point4PositionRadius_0 : vec4<f32>,
    @align(16) point4ColorIntensity_0 : vec4<f32>,
    @align(16) point4Brightness_0 : vec4<f32>,
    @align(16) point5PositionRadius_0 : vec4<f32>,
    @align(16) point5ColorIntensity_0 : vec4<f32>,
    @align(16) point5Brightness_0 : vec4<f32>,
    @align(16) point6PositionRadius_0 : vec4<f32>,
    @align(16) point6ColorIntensity_0 : vec4<f32>,
    @align(16) point6Brightness_0 : vec4<f32>,
    @align(16) point7PositionRadius_0 : vec4<f32>,
    @align(16) point7ColorIntensity_0 : vec4<f32>,
    @align(16) point7Brightness_0 : vec4<f32>,
    @align(16) spot0PositionRadius_0 : vec4<f32>,
    @align(16) spot0ColorIntensity_0 : vec4<f32>,
    @align(16) spot0DirectionExponent_0 : vec4<f32>,
    @align(16) spot0CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot1PositionRadius_0 : vec4<f32>,
    @align(16) spot1ColorIntensity_0 : vec4<f32>,
    @align(16) spot1DirectionExponent_0 : vec4<f32>,
    @align(16) spot1CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot2PositionRadius_0 : vec4<f32>,
    @align(16) spot2ColorIntensity_0 : vec4<f32>,
    @align(16) spot2DirectionExponent_0 : vec4<f32>,
    @align(16) spot2CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot3PositionRadius_0 : vec4<f32>,
    @align(16) spot3ColorIntensity_0 : vec4<f32>,
    @align(16) spot3DirectionExponent_0 : vec4<f32>,
    @align(16) spot3CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot4PositionRadius_0 : vec4<f32>,
    @align(16) spot4ColorIntensity_0 : vec4<f32>,
    @align(16) spot4DirectionExponent_0 : vec4<f32>,
    @align(16) spot4CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot5PositionRadius_0 : vec4<f32>,
    @align(16) spot5ColorIntensity_0 : vec4<f32>,
    @align(16) spot5DirectionExponent_0 : vec4<f32>,
    @align(16) spot5CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot6PositionRadius_0 : vec4<f32>,
    @align(16) spot6ColorIntensity_0 : vec4<f32>,
    @align(16) spot6DirectionExponent_0 : vec4<f32>,
    @align(16) spot6CutoffsBrightness_0 : vec4<f32>,
    @align(16) spot7PositionRadius_0 : vec4<f32>,
    @align(16) spot7ColorIntensity_0 : vec4<f32>,
    @align(16) spot7DirectionExponent_0 : vec4<f32>,
    @align(16) spot7CutoffsBrightness_0 : vec4<f32>,
    @align(16) ambientOcclusionControls_0 : vec4<f32>,
};

@binding(0) @group(2) var<uniform> lighting_0 : LitLightingUniforms_std140_0;
@binding(1) @group(2) var ambientOcclusionTexture_0 : texture_2d<f32>;

@binding(2) @group(2) var ambientOcclusionSampler_0 : sampler;

fn isnan_0( x_0 : f32) -> bool
{
    var _S1 : u32 = (bitcast<u32>((x_0)));
    var _S2 : u32 = (_S1 & (u32(8388607)));
    var _S3 : bool;
    if(((((_S1 >> (u32(23)))) & (u32(255)))) == u32(255))
    {
        _S3 = _S2 != u32(0);
    }
    else
    {
        _S3 = false;
    }
    return _S3;
}

fn isinf_0( x_1 : f32) -> bool
{
    var _S4 : u32 = (bitcast<u32>((x_1)));
    var _S5 : u32 = (_S4 & (u32(8388607)));
    var _S6 : bool;
    if(((((_S4 >> (u32(23)))) & (u32(255)))) == u32(255))
    {
        _S6 = _S5 == u32(0);
    }
    else
    {
        _S6 = false;
    }
    return _S6;
}

fn isfinite_0( x_2 : f32) -> bool
{
    var _S7 : bool;
    if(isinf_0(x_2))
    {
        _S7 = true;
    }
    else
    {
        _S7 = isnan_0(x_2);
    }
    return !_S7;
}

fn isfinite_1( x_3 : vec3<f32>) -> vec3<bool>
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
        result_0[i_0] = isfinite_0(x_3[i_0]);
        i_0 = i_0 + i32(1);
    }
    return result_0;
}

fn rsqrt_0( x_4 : f32) -> f32
{
    return 1.0f / sqrt(x_4);
}

struct LitVertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) worldPosition_0 : vec3<f32>,
    @location(1) worldNormal_0 : vec3<f32>,
    @location(2) uv_0 : vec2<f32>,
    @location(3) worldTangent_0 : vec3<f32>,
    @location(4) worldBitangent_0 : vec3<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) normal_0 : vec3<f32>,
    @location(2) uv_1 : vec2<f32>,
    @location(3) tangent_0 : vec4<f32>,
};

@vertex
fn standardLitVertex( _S8 : vertexInput_0) -> LitVertexOutput_0
{
    var world_0 : vec4<f32> = (((vec4<f32>(_S8.position_1, 1.0f)) * (mat4x4<f32>(object_0.modelMatrix_0.data_0[i32(0)][i32(0)], object_0.modelMatrix_0.data_0[i32(1)][i32(0)], object_0.modelMatrix_0.data_0[i32(2)][i32(0)], object_0.modelMatrix_0.data_0[i32(3)][i32(0)], object_0.modelMatrix_0.data_0[i32(0)][i32(1)], object_0.modelMatrix_0.data_0[i32(1)][i32(1)], object_0.modelMatrix_0.data_0[i32(2)][i32(1)], object_0.modelMatrix_0.data_0[i32(3)][i32(1)], object_0.modelMatrix_0.data_0[i32(0)][i32(2)], object_0.modelMatrix_0.data_0[i32(1)][i32(2)], object_0.modelMatrix_0.data_0[i32(2)][i32(2)], object_0.modelMatrix_0.data_0[i32(3)][i32(2)], object_0.modelMatrix_0.data_0[i32(0)][i32(3)], object_0.modelMatrix_0.data_0[i32(1)][i32(3)], object_0.modelMatrix_0.data_0[i32(2)][i32(3)], object_0.modelMatrix_0.data_0[i32(3)][i32(3)]))));
    var output_0 : LitVertexOutput_0;
    output_0.position_0 = (((world_0) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.worldPosition_0 = world_0.xyz;
    output_0.worldNormal_0 = normalize((((vec4<f32>(_S8.normal_0, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    output_0.uv_0 = _S8.uv_1;
    var _S9 : vec3<f32> = _S8.tangent_0.xyz;
    output_0.worldTangent_0 = normalize((((vec4<f32>(_S9, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    output_0.worldBitangent_0 = normalize((((vec4<f32>(cross(_S8.normal_0, _S9) * vec3<f32>(_S8.tangent_0.w), 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    return output_0;
}

fn safeNormalize_0( value_0 : vec3<f32>) -> vec3<f32>
{
    return value_0 * vec3<f32>(rsqrt_0(max(dot(value_0, value_0), 9.99999968265522539e-21f)));
}

fn mappedTextureNormal_0( uv_2 : vec2<f32>,  normal_1 : vec3<f32>,  tangent_1 : vec3<f32>,  bitangent_0 : vec3<f32>) -> vec3<f32>
{
    var n_0 : vec3<f32> = normalize(normal_1);
    if(!(all((isfinite_1(n_0)))))
    {
        return vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    var t_0 : vec3<f32> = tangent_1 - n_0 * vec3<f32>(dot(n_0, tangent_1));
    var b_0 : vec3<f32> = bitangent_0 - n_0 * vec3<f32>(dot(n_0, bitangent_0));
    var _S10 : bool;
    if(!(all((isfinite_1(t_0)))))
    {
        _S10 = true;
    }
    else
    {
        _S10 = !(all((isfinite_1(b_0))));
    }
    if(_S10)
    {
        _S10 = true;
    }
    else
    {
        _S10 = (dot(t_0, t_0)) < 1.00000001335143196e-10f;
    }
    if(_S10)
    {
        _S10 = true;
    }
    else
    {
        _S10 = (dot(b_0, b_0)) < 1.00000001335143196e-10f;
    }
    if(_S10)
    {
        return n_0;
    }
    var _S11 : vec3<f32> = (textureSample((normalTexture_0), (normalSampler_0), (uv_2))).xyz * vec3<f32>(2.0f) - vec3<f32>(1.0f);
    var sample_0 : vec3<f32> = _S11;
    sample_0[i32(1)] = - _S11.y;
    if((dot(sample_0, sample_0)) <= 9.99999997475242708e-07f)
    {
        return n_0;
    }
    var _S12 : vec3<f32> = normalize(sample_0);
    sample_0 = _S12;
    sample_0[i32(2)] = max(_S12.z, 0.00100000004749745f);
    var _S13 : vec3<f32> = normalize(sample_0);
    sample_0 = _S13;
    return normalize(normalize(t_0) * vec3<f32>(_S13.x) + normalize(b_0) * vec3<f32>(_S13.y) + n_0 * vec3<f32>(_S13.z));
}

fn directPbr_0( color_0 : vec3<f32>,  intensity_0 : f32,  lightDirection_0 : vec3<f32>,  normal_2 : vec3<f32>,  viewDirection_0 : vec3<f32>,  albedo_0 : vec3<f32>,  rms_0 : vec3<f32>,  attenuation_0 : f32) -> vec3<f32>
{
    var _S14 : f32 = max(dot(normal_2, lightDirection_0), 0.0f);
    if(_S14 <= 0.0f)
    {
        return vec3<f32>(0.0f);
    }
    var halfVector_0 : vec3<f32> = safeNormalize_0(viewDirection_0 + lightDirection_0);
    var _S15 : f32 = max(dot(normal_2, halfVector_0), 0.0f);
    var _S16 : f32 = max(dot(normal_2, viewDirection_0), 0.0f);
    var _S17 : f32 = max(dot(halfVector_0, viewDirection_0), 0.0f);
    var _S18 : f32 = rms_0.x;
    var a_0 : f32 = _S18 * _S18;
    var a2_0 : f32 = a_0 * a_0;
    var distributionDenominator_0 : f32 = _S15 * _S15 * (a2_0 - 1.0f) + 1.0f;
    var _S19 : f32 = _S18 + 1.0f;
    var k_0 : f32 = _S19 * _S19 * 0.125f;
    var _S20 : f32 = 1.0f - k_0;
    var _S21 : f32 = rms_0.y;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), albedo_0, vec3<f32>(_S21));
    var _S22 : vec3<f32> = vec3<f32>(1.0f);
    var fresnel_0 : vec3<f32> = f0_0 + (_S22 - f0_0) * vec3<f32>(exp2((-5.55472993850708008f * _S17 - 6.98316001892089844f) * _S17));
    return ((_S22 - fresnel_0) * vec3<f32>((1.0f - _S21)) * albedo_0 / vec3<f32>(3.14159274101257324f) + vec3<f32>((rms_0.z * (a2_0 / max(3.14159274101257324f * distributionDenominator_0 * distributionDenominator_0, 9.99999968265522539e-21f)) * (_S16 / (_S16 * _S20 + k_0) * (_S14 / (_S14 * _S20 + k_0))))) * fresnel_0 / vec3<f32>((4.0f * _S16 * _S14 + 0.00009999999747379f))) * (vec3<f32>(attenuation_0) * color_0 * vec3<f32>(intensity_0)) * vec3<f32>(_S14);
}

fn directionalLight_0( direction_0 : vec4<f32>,  colorIntensity_0 : vec4<f32>,  normal_3 : vec3<f32>,  viewDirection_1 : vec3<f32>,  albedo_1 : vec3<f32>,  rms_1 : vec3<f32>) -> vec3<f32>
{
    return directPbr_0(colorIntensity_0.xyz, colorIntensity_0.w, safeNormalize_0((vec3<f32>(0) - direction_0.xyz)), normal_3, viewDirection_1, albedo_1, rms_1, 1.0f);
}

fn lightAttenuation_0( distance_0 : f32,  radius_0 : f32) -> f32
{
    var relativeDistance_0 : f32 = distance_0 / max(radius_0, 9.99999997475242708e-07f);
    var relativeDistance2_0 : f32 = relativeDistance_0 * relativeDistance_0;
    var falloff_0 : f32 = saturate(1.0f - relativeDistance2_0 * relativeDistance2_0);
    return falloff_0 * falloff_0 / (distance_0 * distance_0 + 1.0f);
}

fn pointLight_0( positionRadius_0 : vec4<f32>,  colorIntensity_1 : vec4<f32>,  brightness_0 : vec4<f32>,  position_2 : vec3<f32>,  normal_4 : vec3<f32>,  viewDirection_2 : vec3<f32>,  albedo_2 : vec3<f32>,  rms_2 : vec3<f32>) -> vec3<f32>
{
    var lightVector_0 : vec3<f32> = positionRadius_0.xyz - position_2;
    return directPbr_0(colorIntensity_1.xyz, colorIntensity_1.w, safeNormalize_0(lightVector_0), normal_4, viewDirection_2, albedo_2, rms_2, lightAttenuation_0(length(lightVector_0), positionRadius_0.w) * brightness_0.x);
}

fn spotLight_0( positionRadius_1 : vec4<f32>,  colorIntensity_2 : vec4<f32>,  directionExponent_0 : vec4<f32>,  cutoffsBrightness_0 : vec4<f32>,  position_3 : vec3<f32>,  normal_5 : vec3<f32>,  viewDirection_3 : vec3<f32>,  albedo_3 : vec3<f32>,  rms_3 : vec3<f32>) -> vec3<f32>
{
    var lightVector_1 : vec3<f32> = positionRadius_1.xyz - position_3;
    var lightDirection_1 : vec3<f32> = safeNormalize_0(lightVector_1);
    var _S23 : f32 = max(0.0f, dot((vec3<f32>(0) - lightDirection_1), safeNormalize_0(directionExponent_0.xyz)));
    var _S24 : f32 = cutoffsBrightness_0.x;
    var _S25 : f32 = cutoffsBrightness_0.y;
    var cone_0 : f32;
    if(_S24 == _S25)
    {
        if(_S23 >= _S24)
        {
            cone_0 = 1.0f;
        }
        else
        {
            cone_0 = 0.0f;
        }
    }
    else
    {
        cone_0 = smoothstep(_S25, _S24, _S23);
    }
    return vec3<f32>((cone_0 * pow(_S23, directionExponent_0.w))) * directPbr_0(colorIntensity_2.xyz, colorIntensity_2.w, lightDirection_1, normal_5, viewDirection_3, albedo_3, rms_3, lightAttenuation_0(length(lightVector_1), positionRadius_1.w) * cutoffsBrightness_0.z);
}

fn ambientVisibility_0( framebufferPixel_0 : vec2<f32>,  albedo_4 : vec3<f32>) -> vec3<f32>
{
    if((lighting_0.ambientOcclusionControls_0.x) < 0.5f)
    {
        return vec3<f32>(1.0f);
    }
    var width_0 : u32;
    var height_0 : u32;
    {var dim = textureDimensions((ambientOcclusionTexture_0));((width_0)) = dim.x;((height_0)) = dim.y;};
    var visibility_0 : f32 = pow(saturate((textureSampleLevel((ambientOcclusionTexture_0), (ambientOcclusionSampler_0), (clamp(framebufferPixel_0 / max(vec2<f32>(f32(width_0), f32(height_0)), vec2<f32>(1.0f)), vec2<f32>(0.0f), vec2<f32>(0.99999898672103882f))), (0.0f))).x), max(lighting_0.ambientOcclusionControls_0.y, 0.00100000004749745f));
    if((lighting_0.ambientOcclusionControls_0.z) < 0.5f)
    {
        return vec3<f32>(visibility_0);
    }
    var _S26 : vec3<f32> = vec3<f32>(visibility_0);
    return max(_S26, (((vec3<f32>(2.04040002822875977f) * albedo_4 - vec3<f32>(0.33239999413490295f)) * _S26 + (vec3<f32>(-4.79510021209716797f) * albedo_4 + vec3<f32>(0.64170002937316895f))) * _S26 + (vec3<f32>(2.75519990921020508f) * albedo_4 + vec3<f32>(0.69029998779296875f))) * _S26);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) worldPosition_1 : vec3<f32>,
    @location(1) worldNormal_1 : vec3<f32>,
    @location(2) uv_3 : vec2<f32>,
    @location(3) worldTangent_1 : vec3<f32>,
    @location(4) worldBitangent_1 : vec3<f32>,
};

@fragment
fn standardLitFragment( _S27 : pixelInput_0, @builtin(position) position_4 : vec4<f32>) -> pixelOutput_0
{
    var normal_6 : vec3<f32> = safeNormalize_0(_S27.worldNormal_1);
    var viewDirection_4 : vec3<f32> = safeNormalize_0(view_0.cameraPosition_0.xyz - _S27.worldPosition_1);
    var albedo_5 : vec3<f32> = material_0.baseColorOpacity_0.xyz;
    var surface_0 : vec4<f32> = material_0.roughnessMetallicSpecularEmission_0;
    var albedo_6 : vec3<f32> = albedo_5 * (textureSample((baseColorTexture_0), (baseColorSampler_0), (_S27.uv_3))).xyz;
    if((material_0.textureControls_0.x) > 0.5f)
    {
        surface_0[i32(0)] = surface_0[i32(0)] * (textureSample((roughnessTexture_0), (roughnessSampler_0), (_S27.uv_3))).x;
    }
    if((material_0.textureControls_0.y) > 0.5f)
    {
        surface_0[i32(1)] = surface_0[i32(1)] * (textureSample((metallicTexture_0), (metallicSampler_0), (_S27.uv_3))).x;
    }
    var normal_7 : vec3<f32> = mappedTextureNormal_0(_S27.uv_3, normal_6, _S27.worldTangent_1, _S27.worldBitangent_1);
    var rms_4 : vec3<f32> = vec3<f32>(saturate(surface_0.x), saturate(surface_0.y), max(surface_0.z, 0.0f));
    var color_1 : vec3<f32> = vec3<f32>(0.0f);
    var color_2 : vec3<f32>;
    if((lighting_0.lightCounts_0.x) > 0.0f)
    {
        color_2 = directionalLight_0(lighting_0.directional0Direction_0, lighting_0.directional0ColorIntensity_0, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    else
    {
        color_2 = color_1;
    }
    if((lighting_0.lightCounts_0.x) > 1.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional1Direction_0, lighting_0.directional1ColorIntensity_0, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.x) > 2.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional2Direction_0, lighting_0.directional2ColorIntensity_0, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.x) > 3.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional3Direction_0, lighting_0.directional3ColorIntensity_0, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 0.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point0PositionRadius_0, lighting_0.point0ColorIntensity_0, lighting_0.point0Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 1.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point1PositionRadius_0, lighting_0.point1ColorIntensity_0, lighting_0.point1Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 2.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point2PositionRadius_0, lighting_0.point2ColorIntensity_0, lighting_0.point2Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 3.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point3PositionRadius_0, lighting_0.point3ColorIntensity_0, lighting_0.point3Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 4.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point4PositionRadius_0, lighting_0.point4ColorIntensity_0, lighting_0.point4Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 5.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point5PositionRadius_0, lighting_0.point5ColorIntensity_0, lighting_0.point5Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 6.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point6PositionRadius_0, lighting_0.point6ColorIntensity_0, lighting_0.point6Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 7.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point7PositionRadius_0, lighting_0.point7ColorIntensity_0, lighting_0.point7Brightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 0.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot0PositionRadius_0, lighting_0.spot0ColorIntensity_0, lighting_0.spot0DirectionExponent_0, lighting_0.spot0CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 1.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot1PositionRadius_0, lighting_0.spot1ColorIntensity_0, lighting_0.spot1DirectionExponent_0, lighting_0.spot1CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 2.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot2PositionRadius_0, lighting_0.spot2ColorIntensity_0, lighting_0.spot2DirectionExponent_0, lighting_0.spot2CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 3.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot3PositionRadius_0, lighting_0.spot3ColorIntensity_0, lighting_0.spot3DirectionExponent_0, lighting_0.spot3CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 4.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot4PositionRadius_0, lighting_0.spot4ColorIntensity_0, lighting_0.spot4DirectionExponent_0, lighting_0.spot4CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 5.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot5PositionRadius_0, lighting_0.spot5ColorIntensity_0, lighting_0.spot5DirectionExponent_0, lighting_0.spot5CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 6.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot6PositionRadius_0, lighting_0.spot6ColorIntensity_0, lighting_0.spot6DirectionExponent_0, lighting_0.spot6CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 7.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot7PositionRadius_0, lighting_0.spot7ColorIntensity_0, lighting_0.spot7DirectionExponent_0, lighting_0.spot7CutoffsBrightness_0, _S27.worldPosition_1, normal_7, viewDirection_4, albedo_6, rms_4);
    }
    var ambient_0 : vec3<f32> = lighting_0.globalAmbient_0.xyz;
    var _S28 : f32 = max(ambient_0.x, max(ambient_0.y, ambient_0.z));
    var ambient_1 : vec3<f32>;
    if(_S28 <= 0.00009999999747379f)
    {
        ambient_1 = vec3<f32>(0.07999999821186066f);
    }
    else
    {
        ambient_1 = ambient_0 * vec3<f32>(max(1.0f, 0.07999999821186066f / _S28));
    }
    var _S29 : pixelOutput_0 = pixelOutput_0( vec4<f32>(color_2 + ambient_1 * ambientVisibility_0(position_4.xy, albedo_6) * albedo_6 + material_0.baseColorOpacity_0.xyz * vec3<f32>(surface_0.w), material_0.baseColorOpacity_0.w) );
    return _S29;
}

