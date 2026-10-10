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
};

@binding(0) @group(1) var<uniform> material_0 : LitMaterialUniforms_std140_0;
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
struct DirectionalShadowUniforms_std140_0
{
    @align(16) lightViewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) control_0 : vec4<f32>,
    @align(16) biasProjection_0 : vec4<f32>,
    @align(16) biasParams_0 : vec4<f32>,
    @align(16) filterParams_0 : vec4<f32>,
    @align(16) sourceParams_0 : vec4<f32>,
};

@binding(0) @group(3) var<uniform> directionalShadow_0 : DirectionalShadowUniforms_std140_0;
@binding(1) @group(3) var directionalShadowMap_0 : texture_depth_2d;

@binding(2) @group(3) var directionalShadowSampler_0 : sampler_comparison;

@binding(1) @group(2) var ambientOcclusionTexture_0 : texture_2d<f32>;

@binding(2) @group(2) var ambientOcclusionSampler_0 : sampler;

fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

struct LitVertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) worldPosition_0 : vec3<f32>,
    @location(1) worldNormal_0 : vec3<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) normal_0 : vec3<f32>,
};

@vertex
fn standardLitVertex( _S1 : vertexInput_0) -> LitVertexOutput_0
{
    var world_0 : vec4<f32> = (((vec4<f32>(_S1.position_1, 1.0f)) * (mat4x4<f32>(object_0.modelMatrix_0.data_0[i32(0)][i32(0)], object_0.modelMatrix_0.data_0[i32(1)][i32(0)], object_0.modelMatrix_0.data_0[i32(2)][i32(0)], object_0.modelMatrix_0.data_0[i32(3)][i32(0)], object_0.modelMatrix_0.data_0[i32(0)][i32(1)], object_0.modelMatrix_0.data_0[i32(1)][i32(1)], object_0.modelMatrix_0.data_0[i32(2)][i32(1)], object_0.modelMatrix_0.data_0[i32(3)][i32(1)], object_0.modelMatrix_0.data_0[i32(0)][i32(2)], object_0.modelMatrix_0.data_0[i32(1)][i32(2)], object_0.modelMatrix_0.data_0[i32(2)][i32(2)], object_0.modelMatrix_0.data_0[i32(3)][i32(2)], object_0.modelMatrix_0.data_0[i32(0)][i32(3)], object_0.modelMatrix_0.data_0[i32(1)][i32(3)], object_0.modelMatrix_0.data_0[i32(2)][i32(3)], object_0.modelMatrix_0.data_0[i32(3)][i32(3)]))));
    var output_0 : LitVertexOutput_0;
    output_0.position_0 = (((world_0) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.worldPosition_0 = world_0.xyz;
    output_0.worldNormal_0 = (((vec4<f32>(_S1.normal_0, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz;
    return output_0;
}

fn safeNormalize_0( value_0 : vec3<f32>) -> vec3<f32>
{
    return value_0 * vec3<f32>(rsqrt_0(max(dot(value_0, value_0), 9.99999968265522539e-21f)));
}

fn vogelTap_0( index_0 : i32,  rotation_0 : f32) -> vec2<f32>
{
    var sampleIndex_0 : f32 = f32(index_0) + 0.5f;
    var angle_0 : f32 = sampleIndex_0 * 2.3999631404876709f + rotation_0;
    return vec2<f32>(sqrt(sampleIndex_0 / 8.0f)) * vec2<f32>(cos(angle_0), sin(angle_0));
}

fn sampleDirectionalShadow_0( position_2 : vec3<f32>,  normal_1 : vec3<f32>,  pixel_0 : vec2<f32>) -> f32
{
    var mapWidth_0 : u32;
    var mapHeight_0 : u32;
    {var dim = textureDimensions((directionalShadowMap_0));((mapWidth_0)) = dim.x;((mapHeight_0)) = dim.y;};
    var _S2 : f32 = f32(mapWidth_0);
    var _S3 : f32 = f32(mapHeight_0);
    var mapSize_0 : vec2<f32> = vec2<f32>(_S2, _S3);
    var _S4 : f32 = max(1.0f / _S2, 1.0f / _S3);
    var lightClip_0 : vec4<f32> = (((vec4<f32>(position_2 + normal_1 * vec3<f32>(max(directionalShadow_0.biasProjection_0.y, 0.0f)), 1.0f)) * (mat4x4<f32>(directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(3)]))));
    var ndc_0 : vec3<f32> = lightClip_0.xyz / vec3<f32>(lightClip_0.w);
    var _S5 : f32 = ndc_0.x * 0.5f + 0.5f;
    var _S6 : f32 = 0.5f - ndc_0.y * 0.5f;
    var _S7 : f32 = ndc_0.z;
    var shadowCoord_0 : vec3<f32> = vec3<f32>(_S5, _S6, _S7);
    var _S8 : vec2<f32> = vec2<f32>(_S5, _S6);
    var uvDx_0 : vec2<f32> = dpdx(_S8);
    var uvDy_0 : vec2<f32> = dpdy(_S8);
    var depthDx_0 : f32 = dpdx(_S7);
    var depthDy_0 : f32 = dpdy(_S7);
    var _S9 : f32 = uvDx_0.x;
    var _S10 : f32 = uvDy_0.y;
    var _S11 : f32 = uvDx_0.y;
    var _S12 : f32 = uvDy_0.x;
    var determinant_0 : f32 = _S9 * _S10 - _S11 * _S12;
    var receiverPlaneBias_0 : f32;
    if((abs(determinant_0)) > 9.99999993922529029e-09f)
    {
        var _S13 : f32 = max(directionalShadow_0.filterParams_0.y, _S4);
        receiverPlaneBias_0 = dot(abs(vec2<f32>((_S10 * depthDx_0 - _S11 * depthDy_0) / determinant_0, (- _S12 * depthDx_0 + _S9 * depthDy_0) / determinant_0)), vec2<f32>(_S13, _S13)) * max(directionalShadow_0.biasParams_0.y, 0.0f);
    }
    else
    {
        receiverPlaneBias_0 = 0.0f;
    }
    var receiverDepth_0 : f32 = _S7 - (max(directionalShadow_0.biasProjection_0.x, 0.0f) + max(receiverPlaneBias_0, 0.0f));
    var _S14 : bool;
    if((any((shadowCoord_0 < vec3<f32>(0.0f)))))
    {
        _S14 = true;
    }
    else
    {
        _S14 = (any((shadowCoord_0 > vec3<f32>(1.0f))));
    }
    if(_S14)
    {
        return 1.0f;
    }
    var _S15 : f32 = fract(52.98291778564453125f * fract(dot(pixel_0, vec2<f32>(0.06711056083440781f, 0.00583714991807938f)))) * 6.28318548202514648f;
    var _S16 : f32 = max(directionalShadow_0.filterParams_0.x, _S4);
    var i_0 : i32 = i32(0);
    var blockerSum_0 : f32 = 0.0f;
    var blockerCount_0 : i32 = i32(0);
    for(;;)
    {
        if(i_0 < i32(8))
        {
        }
        else
        {
            break;
        }
        var _S17 : vec3<i32> = vec3<i32>(vec2<i32>(clamp((_S8 + vogelTap_0(i_0, _S15) * vec2<f32>(_S16)) * mapSize_0, vec2<f32>(0.0f), mapSize_0 - vec2<f32>(1.0f))), i32(0));
        var sampleDepth_0 : f32 = (textureLoad((directionalShadowMap_0), ((_S17)).xy, ((_S17)).z));
        if(sampleDepth_0 < receiverDepth_0)
        {
            var _S18 : i32 = blockerCount_0 + i32(1);
            blockerSum_0 = blockerSum_0 + sampleDepth_0;
            blockerCount_0 = _S18;
        }
        i_0 = i_0 + i32(1);
    }
    if(blockerCount_0 == i32(0))
    {
        return 1.0f;
    }
    var averageBlocker_0 : f32 = blockerSum_0 / f32(blockerCount_0);
    var _S19 : f32 = max(directionalShadow_0.filterParams_0.z, _S4);
    var _S20 : f32 = max(clamp(abs(receiverDepth_0 - averageBlocker_0) / max(abs(averageBlocker_0), 0.00009999999747379f) * max(directionalShadow_0.sourceParams_0.x, 0.0f), _S19, max(directionalShadow_0.filterParams_0.w, _S19)), _S4);
    i_0 = i32(0);
    var visibility_0 : f32 = 0.0f;
    for(;;)
    {
        if(i_0 < i32(8))
        {
        }
        else
        {
            break;
        }
        var visibility_1 : f32 = visibility_0 + (textureSampleCompareLevel((directionalShadowMap_0), (directionalShadowSampler_0), (_S8 + vogelTap_0(i_0, _S15) * vec2<f32>(_S20)), (receiverDepth_0)));
        i_0 = i_0 + i32(1);
        visibility_0 = visibility_1;
    }
    return visibility_0 * 0.125f;
}

fn selectedDirectionalVisibility_0( index_1 : i32,  position_3 : vec3<f32>,  normal_2 : vec3<f32>,  pixel_1 : vec2<f32>) -> f32
{
    var _S21 : bool;
    if((directionalShadow_0.control_0.x) <= 0.5f)
    {
        _S21 = true;
    }
    else
    {
        _S21 = i32(directionalShadow_0.control_0.y) != index_1;
    }
    if(_S21)
    {
        return 1.0f;
    }
    return sampleDirectionalShadow_0(position_3, normal_2, pixel_1);
}

fn directPbr_0( color_0 : vec3<f32>,  intensity_0 : f32,  lightDirection_0 : vec3<f32>,  normal_3 : vec3<f32>,  viewDirection_0 : vec3<f32>,  albedo_0 : vec3<f32>,  rms_0 : vec3<f32>,  attenuation_0 : f32) -> vec3<f32>
{
    var _S22 : f32 = max(dot(normal_3, lightDirection_0), 0.0f);
    if(_S22 <= 0.0f)
    {
        return vec3<f32>(0.0f);
    }
    var halfVector_0 : vec3<f32> = safeNormalize_0(viewDirection_0 + lightDirection_0);
    var _S23 : f32 = max(dot(normal_3, halfVector_0), 0.0f);
    var _S24 : f32 = max(dot(normal_3, viewDirection_0), 0.0f);
    var _S25 : f32 = max(dot(halfVector_0, viewDirection_0), 0.0f);
    var _S26 : f32 = rms_0.x;
    var a_0 : f32 = _S26 * _S26;
    var a2_0 : f32 = a_0 * a_0;
    var distributionDenominator_0 : f32 = _S23 * _S23 * (a2_0 - 1.0f) + 1.0f;
    var _S27 : f32 = _S26 + 1.0f;
    var k_0 : f32 = _S27 * _S27 * 0.125f;
    var _S28 : f32 = 1.0f - k_0;
    var _S29 : f32 = rms_0.y;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), albedo_0, vec3<f32>(_S29));
    var _S30 : vec3<f32> = vec3<f32>(1.0f);
    var fresnel_0 : vec3<f32> = f0_0 + (_S30 - f0_0) * vec3<f32>(exp2((-5.55472993850708008f * _S25 - 6.98316001892089844f) * _S25));
    return ((_S30 - fresnel_0) * vec3<f32>((1.0f - _S29)) * albedo_0 / vec3<f32>(3.14159274101257324f) + vec3<f32>((rms_0.z * (a2_0 / max(3.14159274101257324f * distributionDenominator_0 * distributionDenominator_0, 9.99999968265522539e-21f)) * (_S24 / (_S24 * _S28 + k_0) * (_S22 / (_S22 * _S28 + k_0))))) * fresnel_0 / vec3<f32>((4.0f * _S24 * _S22 + 0.00009999999747379f))) * (vec3<f32>(attenuation_0) * color_0 * vec3<f32>(intensity_0)) * vec3<f32>(_S22);
}

fn directionalLight_0( direction_0 : vec4<f32>,  colorIntensity_0 : vec4<f32>,  normal_4 : vec3<f32>,  viewDirection_1 : vec3<f32>,  albedo_1 : vec3<f32>,  rms_1 : vec3<f32>,  visibility_2 : f32) -> vec3<f32>
{
    return directPbr_0(colorIntensity_0.xyz, colorIntensity_0.w, safeNormalize_0((vec3<f32>(0) - direction_0.xyz)), normal_4, viewDirection_1, albedo_1, rms_1, visibility_2);
}

fn lightAttenuation_0( distance_0 : f32,  radius_0 : f32) -> f32
{
    var relativeDistance_0 : f32 = distance_0 / max(radius_0, 9.99999997475242708e-07f);
    var relativeDistance2_0 : f32 = relativeDistance_0 * relativeDistance_0;
    var falloff_0 : f32 = saturate(1.0f - relativeDistance2_0 * relativeDistance2_0);
    return falloff_0 * falloff_0 / (distance_0 * distance_0 + 1.0f);
}

fn pointLight_0( positionRadius_0 : vec4<f32>,  colorIntensity_1 : vec4<f32>,  brightness_0 : vec4<f32>,  position_4 : vec3<f32>,  normal_5 : vec3<f32>,  viewDirection_2 : vec3<f32>,  albedo_2 : vec3<f32>,  rms_2 : vec3<f32>) -> vec3<f32>
{
    var lightVector_0 : vec3<f32> = positionRadius_0.xyz - position_4;
    return directPbr_0(colorIntensity_1.xyz, colorIntensity_1.w, safeNormalize_0(lightVector_0), normal_5, viewDirection_2, albedo_2, rms_2, lightAttenuation_0(length(lightVector_0), positionRadius_0.w) * brightness_0.x);
}

fn spotLight_0( positionRadius_1 : vec4<f32>,  colorIntensity_2 : vec4<f32>,  directionExponent_0 : vec4<f32>,  cutoffsBrightness_0 : vec4<f32>,  position_5 : vec3<f32>,  normal_6 : vec3<f32>,  viewDirection_3 : vec3<f32>,  albedo_3 : vec3<f32>,  rms_3 : vec3<f32>) -> vec3<f32>
{
    var lightVector_1 : vec3<f32> = positionRadius_1.xyz - position_5;
    var lightDirection_1 : vec3<f32> = safeNormalize_0(lightVector_1);
    var _S31 : f32 = max(0.0f, dot((vec3<f32>(0) - lightDirection_1), safeNormalize_0(directionExponent_0.xyz)));
    var _S32 : f32 = cutoffsBrightness_0.x;
    var _S33 : f32 = cutoffsBrightness_0.y;
    var cone_0 : f32;
    if(_S32 == _S33)
    {
        if(_S31 >= _S32)
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
        cone_0 = smoothstep(_S33, _S32, _S31);
    }
    return vec3<f32>((cone_0 * pow(_S31, directionExponent_0.w))) * directPbr_0(colorIntensity_2.xyz, colorIntensity_2.w, lightDirection_1, normal_6, viewDirection_3, albedo_3, rms_3, lightAttenuation_0(length(lightVector_1), positionRadius_1.w) * cutoffsBrightness_0.z);
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
    var visibility_3 : f32 = pow(saturate((textureSampleLevel((ambientOcclusionTexture_0), (ambientOcclusionSampler_0), (clamp(framebufferPixel_0 / max(vec2<f32>(f32(width_0), f32(height_0)), vec2<f32>(1.0f)), vec2<f32>(0.0f), vec2<f32>(0.99999898672103882f))), (0.0f))).x), max(lighting_0.ambientOcclusionControls_0.y, 0.00100000004749745f));
    if((lighting_0.ambientOcclusionControls_0.z) < 0.5f)
    {
        return vec3<f32>(visibility_3);
    }
    var _S34 : vec3<f32> = vec3<f32>(visibility_3);
    return max(_S34, (((vec3<f32>(2.04040002822875977f) * albedo_4 - vec3<f32>(0.33239999413490295f)) * _S34 + (vec3<f32>(-4.79510021209716797f) * albedo_4 + vec3<f32>(0.64170002937316895f))) * _S34 + (vec3<f32>(2.75519990921020508f) * albedo_4 + vec3<f32>(0.69029998779296875f))) * _S34);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) worldPosition_1 : vec3<f32>,
    @location(1) worldNormal_1 : vec3<f32>,
};

@fragment
fn standardLitFragment( _S35 : pixelInput_0, @builtin(position) position_6 : vec4<f32>) -> pixelOutput_0
{
    var normal_7 : vec3<f32> = safeNormalize_0(_S35.worldNormal_1);
    var viewDirection_4 : vec3<f32> = safeNormalize_0(view_0.cameraPosition_0.xyz - _S35.worldPosition_1);
    var albedo_5 : vec3<f32> = material_0.baseColorOpacity_0.xyz;
    var surface_0 : vec4<f32> = material_0.roughnessMetallicSpecularEmission_0;
    var rms_4 : vec3<f32> = vec3<f32>(saturate(material_0.roughnessMetallicSpecularEmission_0.x), saturate(material_0.roughnessMetallicSpecularEmission_0.y), max(material_0.roughnessMetallicSpecularEmission_0.z, 0.0f));
    var color_1 : vec3<f32> = vec3<f32>(0.0f);
    var color_2 : vec3<f32>;
    if((lighting_0.lightCounts_0.x) > 0.0f)
    {
        color_2 = directionalLight_0(lighting_0.directional0Direction_0, lighting_0.directional0ColorIntensity_0, normal_7, viewDirection_4, albedo_5, rms_4, selectedDirectionalVisibility_0(i32(0), _S35.worldPosition_1, normal_7, position_6.xy));
    }
    else
    {
        color_2 = color_1;
    }
    if((lighting_0.lightCounts_0.x) > 1.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional1Direction_0, lighting_0.directional1ColorIntensity_0, normal_7, viewDirection_4, albedo_5, rms_4, selectedDirectionalVisibility_0(i32(1), _S35.worldPosition_1, normal_7, position_6.xy));
    }
    if((lighting_0.lightCounts_0.x) > 2.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional2Direction_0, lighting_0.directional2ColorIntensity_0, normal_7, viewDirection_4, albedo_5, rms_4, selectedDirectionalVisibility_0(i32(2), _S35.worldPosition_1, normal_7, position_6.xy));
    }
    if((lighting_0.lightCounts_0.x) > 3.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional3Direction_0, lighting_0.directional3ColorIntensity_0, normal_7, viewDirection_4, albedo_5, rms_4, selectedDirectionalVisibility_0(i32(3), _S35.worldPosition_1, normal_7, position_6.xy));
    }
    if((lighting_0.lightCounts_0.y) > 0.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point0PositionRadius_0, lighting_0.point0ColorIntensity_0, lighting_0.point0Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 1.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point1PositionRadius_0, lighting_0.point1ColorIntensity_0, lighting_0.point1Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 2.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point2PositionRadius_0, lighting_0.point2ColorIntensity_0, lighting_0.point2Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 3.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point3PositionRadius_0, lighting_0.point3ColorIntensity_0, lighting_0.point3Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 4.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point4PositionRadius_0, lighting_0.point4ColorIntensity_0, lighting_0.point4Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 5.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point5PositionRadius_0, lighting_0.point5ColorIntensity_0, lighting_0.point5Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 6.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point6PositionRadius_0, lighting_0.point6ColorIntensity_0, lighting_0.point6Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 7.0f)
    {
        color_2 = color_2 + pointLight_0(lighting_0.point7PositionRadius_0, lighting_0.point7ColorIntensity_0, lighting_0.point7Brightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 0.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot0PositionRadius_0, lighting_0.spot0ColorIntensity_0, lighting_0.spot0DirectionExponent_0, lighting_0.spot0CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 1.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot1PositionRadius_0, lighting_0.spot1ColorIntensity_0, lighting_0.spot1DirectionExponent_0, lighting_0.spot1CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 2.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot2PositionRadius_0, lighting_0.spot2ColorIntensity_0, lighting_0.spot2DirectionExponent_0, lighting_0.spot2CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 3.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot3PositionRadius_0, lighting_0.spot3ColorIntensity_0, lighting_0.spot3DirectionExponent_0, lighting_0.spot3CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 4.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot4PositionRadius_0, lighting_0.spot4ColorIntensity_0, lighting_0.spot4DirectionExponent_0, lighting_0.spot4CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 5.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot5PositionRadius_0, lighting_0.spot5ColorIntensity_0, lighting_0.spot5DirectionExponent_0, lighting_0.spot5CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 6.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot6PositionRadius_0, lighting_0.spot6ColorIntensity_0, lighting_0.spot6DirectionExponent_0, lighting_0.spot6CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 7.0f)
    {
        color_2 = color_2 + spotLight_0(lighting_0.spot7PositionRadius_0, lighting_0.spot7ColorIntensity_0, lighting_0.spot7DirectionExponent_0, lighting_0.spot7CutoffsBrightness_0, _S35.worldPosition_1, normal_7, viewDirection_4, albedo_5, rms_4);
    }
    var ambient_0 : vec3<f32> = lighting_0.globalAmbient_0.xyz;
    var _S36 : f32 = max(ambient_0.x, max(ambient_0.y, ambient_0.z));
    var ambient_1 : vec3<f32>;
    if(_S36 <= 0.00009999999747379f)
    {
        ambient_1 = vec3<f32>(0.07999999821186066f);
    }
    else
    {
        ambient_1 = ambient_0 * vec3<f32>(max(1.0f, 0.07999999821186066f / _S36));
    }
    var _S37 : pixelOutput_0 = pixelOutput_0( vec4<f32>(color_2 + ambient_1 * ambientVisibility_0(position_6.xy, albedo_5) * albedo_5 + albedo_5 * vec3<f32>(surface_0.w), material_0.baseColorOpacity_0.w) );
    return _S37;
}

