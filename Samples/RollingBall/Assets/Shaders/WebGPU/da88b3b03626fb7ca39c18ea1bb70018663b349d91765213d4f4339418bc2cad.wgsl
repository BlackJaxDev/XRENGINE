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

struct LocalShadowUniforms_std140_0
{
    @align(16) spotViewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) spotControl_0 : vec4<f32>,
    @align(16) spotPosition_0 : vec4<f32>,
    @align(16) spotDirection_0 : vec4<f32>,
    @align(16) spotBias_0 : vec4<f32>,
    @align(16) spotFilter_0 : vec4<f32>,
    @align(16) spotSource_0 : vec4<f32>,
    @align(16) pointControl_0 : vec4<f32>,
    @align(16) pointPosition_0 : vec4<f32>,
    @align(16) pointBias_0 : vec4<f32>,
    @align(16) pointFilter_0 : vec4<f32>,
    @align(16) pointSource_0 : vec4<f32>,
};

@binding(3) @group(3) var<uniform> localShadow_0 : LocalShadowUniforms_std140_0;
@binding(6) @group(3) var pointShadowMap_0 : texture_cube<f32>;

@binding(7) @group(3) var pointShadowSampler_0 : sampler;

@binding(4) @group(3) var spotShadowMap_0 : texture_2d<f32>;

@binding(5) @group(3) var spotShadowSampler_0 : sampler;

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

fn vogelTap_0( index_0 : i32,  rotation_0 : f32) -> vec2<f32>
{
    var sampleIndex_0 : f32 = f32(index_0) + 0.5f;
    var angle_0 : f32 = sampleIndex_0 * 2.3999631404876709f + rotation_0;
    return vec2<f32>(sqrt(sampleIndex_0 / 8.0f)) * vec2<f32>(cos(angle_0), sin(angle_0));
}

fn sampleDirectionalShadow_0( position_2 : vec3<f32>,  normal_2 : vec3<f32>,  pixel_0 : vec2<f32>) -> f32
{
    var mapWidth_0 : u32;
    var mapHeight_0 : u32;
    {var dim = textureDimensions((directionalShadowMap_0));((mapWidth_0)) = dim.x;((mapHeight_0)) = dim.y;};
    var _S14 : f32 = f32(mapWidth_0);
    var _S15 : f32 = f32(mapHeight_0);
    var mapSize_0 : vec2<f32> = vec2<f32>(_S14, _S15);
    var _S16 : f32 = max(1.0f / _S14, 1.0f / _S15);
    var lightClip_0 : vec4<f32> = (((vec4<f32>(position_2 + normal_2 * vec3<f32>(max(directionalShadow_0.biasProjection_0.y, 0.0f)), 1.0f)) * (mat4x4<f32>(directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(0)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(1)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(2)], directionalShadow_0.lightViewProjection_0.data_0[i32(0)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(1)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(2)][i32(3)], directionalShadow_0.lightViewProjection_0.data_0[i32(3)][i32(3)]))));
    var ndc_0 : vec3<f32> = lightClip_0.xyz / vec3<f32>(lightClip_0.w);
    var _S17 : f32 = ndc_0.x * 0.5f + 0.5f;
    var _S18 : f32 = 0.5f - ndc_0.y * 0.5f;
    var _S19 : f32 = ndc_0.z;
    var shadowCoord_0 : vec3<f32> = vec3<f32>(_S17, _S18, _S19);
    var _S20 : vec2<f32> = vec2<f32>(_S17, _S18);
    var uvDx_0 : vec2<f32> = dpdx(_S20);
    var uvDy_0 : vec2<f32> = dpdy(_S20);
    var depthDx_0 : f32 = dpdx(_S19);
    var depthDy_0 : f32 = dpdy(_S19);
    var _S21 : f32 = uvDx_0.x;
    var _S22 : f32 = uvDy_0.y;
    var _S23 : f32 = uvDx_0.y;
    var _S24 : f32 = uvDy_0.x;
    var determinant_0 : f32 = _S21 * _S22 - _S23 * _S24;
    var receiverPlaneBias_0 : f32;
    if((abs(determinant_0)) > 9.99999993922529029e-09f)
    {
        var _S25 : f32 = max(directionalShadow_0.filterParams_0.y, _S16);
        receiverPlaneBias_0 = dot(abs(vec2<f32>((_S22 * depthDx_0 - _S23 * depthDy_0) / determinant_0, (- _S24 * depthDx_0 + _S21 * depthDy_0) / determinant_0)), vec2<f32>(_S25, _S25)) * max(directionalShadow_0.biasParams_0.y, 0.0f);
    }
    else
    {
        receiverPlaneBias_0 = 0.0f;
    }
    var receiverDepth_0 : f32 = _S19 - (max(directionalShadow_0.biasProjection_0.x, 0.0f) + max(receiverPlaneBias_0, 0.0f));
    var _S26 : bool;
    if((any((shadowCoord_0 < vec3<f32>(0.0f)))))
    {
        _S26 = true;
    }
    else
    {
        _S26 = (any((shadowCoord_0 > vec3<f32>(1.0f))));
    }
    if(_S26)
    {
        return 1.0f;
    }
    var _S27 : f32 = fract(52.98291778564453125f * fract(dot(pixel_0, vec2<f32>(0.06711056083440781f, 0.00583714991807938f)))) * 6.28318548202514648f;
    var _S28 : f32 = max(directionalShadow_0.filterParams_0.x, _S16);
    var i_1 : i32 = i32(0);
    var blockerSum_0 : f32 = 0.0f;
    var blockerCount_0 : i32 = i32(0);
    for(;;)
    {
        if(i_1 < i32(8))
        {
        }
        else
        {
            break;
        }
        var _S29 : vec3<i32> = vec3<i32>(vec2<i32>(clamp((_S20 + vogelTap_0(i_1, _S27) * vec2<f32>(_S28)) * mapSize_0, vec2<f32>(0.0f), mapSize_0 - vec2<f32>(1.0f))), i32(0));
        var sampleDepth_0 : f32 = (textureLoad((directionalShadowMap_0), ((_S29)).xy, ((_S29)).z));
        if(sampleDepth_0 < receiverDepth_0)
        {
            var _S30 : i32 = blockerCount_0 + i32(1);
            blockerSum_0 = blockerSum_0 + sampleDepth_0;
            blockerCount_0 = _S30;
        }
        i_1 = i_1 + i32(1);
    }
    if(blockerCount_0 == i32(0))
    {
        return 1.0f;
    }
    var averageBlocker_0 : f32 = blockerSum_0 / f32(blockerCount_0);
    var _S31 : f32 = max(directionalShadow_0.filterParams_0.z, _S16);
    var _S32 : f32 = max(clamp(abs(receiverDepth_0 - averageBlocker_0) / max(abs(averageBlocker_0), 0.00009999999747379f) * max(directionalShadow_0.sourceParams_0.x, 0.0f), _S31, max(directionalShadow_0.filterParams_0.w, _S31)), _S16);
    i_1 = i32(0);
    var visibility_0 : f32 = 0.0f;
    for(;;)
    {
        if(i_1 < i32(8))
        {
        }
        else
        {
            break;
        }
        var visibility_1 : f32 = visibility_0 + (textureSampleCompareLevel((directionalShadowMap_0), (directionalShadowSampler_0), (_S20 + vogelTap_0(i_1, _S27) * vec2<f32>(_S32)), (receiverDepth_0)));
        i_1 = i_1 + i32(1);
        visibility_0 = visibility_1;
    }
    return visibility_0 * 0.125f;
}

fn selectedDirectionalVisibility_0( index_1 : i32,  position_3 : vec3<f32>,  normal_3 : vec3<f32>,  pixel_1 : vec2<f32>) -> f32
{
    var _S33 : bool;
    if((directionalShadow_0.control_0.x) <= 0.5f)
    {
        _S33 = true;
    }
    else
    {
        _S33 = i32(directionalShadow_0.control_0.y) != index_1;
    }
    if(_S33)
    {
        return 1.0f;
    }
    return sampleDirectionalShadow_0(position_3, normal_3, pixel_1);
}

fn directPbr_0( color_0 : vec3<f32>,  intensity_0 : f32,  lightDirection_0 : vec3<f32>,  normal_4 : vec3<f32>,  viewDirection_0 : vec3<f32>,  albedo_0 : vec3<f32>,  rms_0 : vec3<f32>,  attenuation_0 : f32) -> vec3<f32>
{
    var _S34 : f32 = max(dot(normal_4, lightDirection_0), 0.0f);
    if(_S34 <= 0.0f)
    {
        return vec3<f32>(0.0f);
    }
    var halfVector_0 : vec3<f32> = safeNormalize_0(viewDirection_0 + lightDirection_0);
    var _S35 : f32 = max(dot(normal_4, halfVector_0), 0.0f);
    var _S36 : f32 = max(dot(normal_4, viewDirection_0), 0.0f);
    var _S37 : f32 = max(dot(halfVector_0, viewDirection_0), 0.0f);
    var _S38 : f32 = rms_0.x;
    var a_0 : f32 = _S38 * _S38;
    var a2_0 : f32 = a_0 * a_0;
    var distributionDenominator_0 : f32 = _S35 * _S35 * (a2_0 - 1.0f) + 1.0f;
    var _S39 : f32 = _S38 + 1.0f;
    var k_0 : f32 = _S39 * _S39 * 0.125f;
    var _S40 : f32 = 1.0f - k_0;
    var _S41 : f32 = rms_0.y;
    var f0_0 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), albedo_0, vec3<f32>(_S41));
    var _S42 : vec3<f32> = vec3<f32>(1.0f);
    var fresnel_0 : vec3<f32> = f0_0 + (_S42 - f0_0) * vec3<f32>(exp2((-5.55472993850708008f * _S37 - 6.98316001892089844f) * _S37));
    return ((_S42 - fresnel_0) * vec3<f32>((1.0f - _S41)) * albedo_0 / vec3<f32>(3.14159274101257324f) + vec3<f32>((rms_0.z * (a2_0 / max(3.14159274101257324f * distributionDenominator_0 * distributionDenominator_0, 9.99999968265522539e-21f)) * (_S36 / (_S36 * _S40 + k_0) * (_S34 / (_S34 * _S40 + k_0))))) * fresnel_0 / vec3<f32>((4.0f * _S36 * _S34 + 0.00009999999747379f))) * (vec3<f32>(attenuation_0) * color_0 * vec3<f32>(intensity_0)) * vec3<f32>(_S34);
}

fn directionalLight_0( direction_0 : vec4<f32>,  colorIntensity_0 : vec4<f32>,  normal_5 : vec3<f32>,  viewDirection_1 : vec3<f32>,  albedo_1 : vec3<f32>,  rms_1 : vec3<f32>,  visibility_2 : f32) -> vec3<f32>
{
    return directPbr_0(colorIntensity_0.xyz, colorIntensity_0.w, safeNormalize_0((vec3<f32>(0) - direction_0.xyz)), normal_5, viewDirection_1, albedo_1, rms_1, visibility_2);
}

fn localShadowRotation_0( pixel_2 : vec2<f32>) -> f32
{
    return fract(52.98291778564453125f * fract(dot(pixel_2, vec2<f32>(0.06711056083440781f, 0.00583714991807938f)))) * 6.28318548202514648f;
}

fn selectedPointVisibility_0( index_2 : i32,  position_4 : vec3<f32>,  normal_6 : vec3<f32>,  pixel_3 : vec2<f32>) -> f32
{
    var _S43 : bool;
    if((localShadow_0.pointControl_0.x) <= 0.5f)
    {
        _S43 = true;
    }
    else
    {
        _S43 = i32(localShadow_0.pointControl_0.y) != index_2;
    }
    if(_S43)
    {
        return 1.0f;
    }
    var width_0 : u32;
    var height_0 : u32;
    {var dim = textureDimensions((pointShadowMap_0));((width_0)) = dim.x;((height_0)) = dim.y;};
    var directionTexel_0 : f32 = 2.0f / max(f32(width_0), 1.0f);
    var lightToReceiver_0 : vec3<f32> = position_4 - localShadow_0.pointPosition_0.xyz;
    var receiverDistance_0 : f32 = length(lightToReceiver_0);
    var farZ_0 : f32 = localShadow_0.pointControl_0.w;
    var lightDirection_1 : vec3<f32>;
    if(receiverDistance_0 > 9.99999997475242708e-07f)
    {
        lightDirection_1 = (vec3<f32>(0) - lightToReceiver_0) / vec3<f32>(receiverDistance_0);
    }
    else
    {
        lightDirection_1 = vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    var _S44 : f32 = max(dot(normal_6, lightDirection_1), 0.0f);
    var normalOffset_0 : f32 = receiverDistance_0 * directionTexel_0 * max(localShadow_0.pointBias_0.z, 0.0f);
    var shadowDirection_0 : vec3<f32> = position_4 + normal_6 * vec3<f32>(normalOffset_0) - localShadow_0.pointPosition_0.xyz;
    var distance_0 : f32 = length(shadowDirection_0);
    if(receiverDistance_0 >= farZ_0)
    {
        _S43 = true;
    }
    else
    {
        _S43 = distance_0 >= farZ_0;
    }
    if(_S43)
    {
        _S43 = true;
    }
    else
    {
        _S43 = distance_0 <= (localShadow_0.pointControl_0.z + normalOffset_0);
    }
    if(_S43)
    {
        return 1.0f;
    }
    var _S45 : f32 = max(_S44, 0.05000000074505806f);
    var receiverDepth_1 : f32 = distance_0 * (1.0f - max(directionTexel_0 * (max(localShadow_0.pointBias_0.x, 0.0f) + max(localShadow_0.pointBias_0.y, 0.0f) * (sqrt(max(1.0f - _S45 * _S45, 0.0f)) / _S45) * max(1.0f, clamp(localShadow_0.pointFilter_0.y * 256.0f, 0.0f, 8.0f))), 0.001953125f));
    var _S46 : f32 = directionTexel_0 * max(1.0f, clamp(localShadow_0.pointFilter_0.x * 256.0f, 0.0f, 4.0f));
    var minRadius_0 : f32 = directionTexel_0 * max(1.0f, clamp(localShadow_0.pointFilter_0.z * 256.0f, 0.0f, 4.0f));
    var maxRadius_0 : f32 = directionTexel_0 * max(1.0f, clamp(localShadow_0.pointFilter_0.w * 256.0f, 0.0f, 4.0f));
    var baseDirection_0 : vec3<f32> = safeNormalize_0(shadowDirection_0);
    var up_0 : vec3<f32>;
    if((abs(baseDirection_0.z)) < 0.99900001287460327f)
    {
        up_0 = vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    else
    {
        up_0 = vec3<f32>(0.0f, 1.0f, 0.0f);
    }
    var tangent_2 : vec3<f32> = safeNormalize_0(cross(up_0, baseDirection_0));
    var _S47 : vec3<f32> = cross(baseDirection_0, tangent_2);
    var _S48 : f32 = localShadowRotation_0(pixel_3);
    var tap_0 : i32 = i32(0);
    var blockerSum_1 : f32 = 0.0f;
    var blockerCount_1 : i32 = i32(0);
    for(;;)
    {
        if(tap_0 < i32(8))
        {
        }
        else
        {
            break;
        }
        var disk_0 : vec2<f32> = vogelTap_0(tap_0, _S48) * vec2<f32>(max(_S46, 9.99999997475242708e-07f));
        var depth_0 : f32 = (textureSampleLevel((pointShadowMap_0), (pointShadowSampler_0), (safeNormalize_0(baseDirection_0 + tangent_2 * vec3<f32>(disk_0.x) + _S47 * vec3<f32>(disk_0.y))), (0.0f)).x) * farZ_0;
        if(depth_0 < receiverDepth_1)
        {
            var _S49 : i32 = blockerCount_1 + i32(1);
            blockerSum_1 = blockerSum_1 + depth_0;
            blockerCount_1 = _S49;
        }
        tap_0 = tap_0 + i32(1);
    }
    if(blockerCount_1 == i32(0))
    {
        return 1.0f;
    }
    var averageBlocker_1 : f32 = blockerSum_1 / f32(blockerCount_1);
    var _S50 : f32 = max(averageBlocker_1, 0.00009999999747379f);
    var _S51 : f32 = clamp((receiverDepth_1 - averageBlocker_1) / _S50 * (max(localShadow_0.pointSource_0.x, 0.0f) / _S50), max(minRadius_0, 9.99999997475242708e-07f), max(maxRadius_0, minRadius_0));
    tap_0 = i32(0);
    var visibility_3 : f32 = 0.0f;
    for(;;)
    {
        if(tap_0 < i32(8))
        {
        }
        else
        {
            break;
        }
        var disk_1 : vec2<f32> = vogelTap_0(tap_0, _S48) * vec2<f32>(_S51);
        if(receiverDepth_1 <= ((textureSampleLevel((pointShadowMap_0), (pointShadowSampler_0), (safeNormalize_0(baseDirection_0 + tangent_2 * vec3<f32>(disk_1.x) + _S47 * vec3<f32>(disk_1.y))), (0.0f)).x) * farZ_0))
        {
            blockerSum_1 = 1.0f;
        }
        else
        {
            blockerSum_1 = 0.0f;
        }
        var visibility_4 : f32 = visibility_3 + blockerSum_1;
        tap_0 = tap_0 + i32(1);
        visibility_3 = visibility_4;
    }
    return visibility_3 * 0.125f;
}

fn lightAttenuation_0( distance_1 : f32,  radius_0 : f32) -> f32
{
    var relativeDistance_0 : f32 = distance_1 / max(radius_0, 9.99999997475242708e-07f);
    var relativeDistance2_0 : f32 = relativeDistance_0 * relativeDistance_0;
    var falloff_0 : f32 = saturate(1.0f - relativeDistance2_0 * relativeDistance2_0);
    return falloff_0 * falloff_0 / (distance_1 * distance_1 + 1.0f);
}

fn pointLight_0( positionRadius_0 : vec4<f32>,  colorIntensity_1 : vec4<f32>,  brightness_0 : vec4<f32>,  position_5 : vec3<f32>,  normal_7 : vec3<f32>,  viewDirection_2 : vec3<f32>,  albedo_2 : vec3<f32>,  rms_2 : vec3<f32>) -> vec3<f32>
{
    var lightVector_0 : vec3<f32> = positionRadius_0.xyz - position_5;
    return directPbr_0(colorIntensity_1.xyz, colorIntensity_1.w, safeNormalize_0(lightVector_0), normal_7, viewDirection_2, albedo_2, rms_2, lightAttenuation_0(length(lightVector_0), positionRadius_0.w) * brightness_0.x);
}

fn selectedSpotVisibility_0( index_3 : i32,  position_6 : vec3<f32>,  normal_8 : vec3<f32>,  pixel_4 : vec2<f32>) -> f32
{
    var _S52 : bool;
    if((localShadow_0.spotControl_0.x) <= 0.5f)
    {
        _S52 = true;
    }
    else
    {
        _S52 = i32(localShadow_0.spotControl_0.y) != index_3;
    }
    if(_S52)
    {
        return 1.0f;
    }
    var width_1 : u32;
    var height_1 : u32;
    {var dim = textureDimensions((spotShadowMap_0));((width_1)) = dim.x;((height_1)) = dim.y;};
    var _S53 : f32 = f32(width_1);
    var _S54 : f32 = f32(height_1);
    var size_0 : vec2<f32> = vec2<f32>(_S53, _S54);
    var texel_0 : f32 = 1.0f / max(f32(width_1), 1.0f);
    var _S55 : f32 = max(dot(position_6 - localShadow_0.spotPosition_0.xyz, safeNormalize_0(localShadow_0.spotDirection_0.xyz)), 0.00009999999747379f);
    var cosOuter_0 : f32 = clamp(localShadow_0.spotDirection_0.w, 0.00100000004749745f, 0.99999898672103882f);
    var worldTexel_0 : f32 = 2.0f * _S55 * (sqrt(max(1.0f - cosOuter_0 * cosOuter_0, 0.0f)) / cosOuter_0) * max(texel_0, 1.00000001168609742e-07f);
    var clip_0 : vec4<f32> = (((vec4<f32>(position_6 + normal_8 * vec3<f32>(worldTexel_0) * vec3<f32>(max(localShadow_0.spotBias_0.z, 0.0f)), 1.0f)) * (mat4x4<f32>(localShadow_0.spotViewProjection_0.data_0[i32(0)][i32(0)], localShadow_0.spotViewProjection_0.data_0[i32(1)][i32(0)], localShadow_0.spotViewProjection_0.data_0[i32(2)][i32(0)], localShadow_0.spotViewProjection_0.data_0[i32(3)][i32(0)], localShadow_0.spotViewProjection_0.data_0[i32(0)][i32(1)], localShadow_0.spotViewProjection_0.data_0[i32(1)][i32(1)], localShadow_0.spotViewProjection_0.data_0[i32(2)][i32(1)], localShadow_0.spotViewProjection_0.data_0[i32(3)][i32(1)], localShadow_0.spotViewProjection_0.data_0[i32(0)][i32(2)], localShadow_0.spotViewProjection_0.data_0[i32(1)][i32(2)], localShadow_0.spotViewProjection_0.data_0[i32(2)][i32(2)], localShadow_0.spotViewProjection_0.data_0[i32(3)][i32(2)], localShadow_0.spotViewProjection_0.data_0[i32(0)][i32(3)], localShadow_0.spotViewProjection_0.data_0[i32(1)][i32(3)], localShadow_0.spotViewProjection_0.data_0[i32(2)][i32(3)], localShadow_0.spotViewProjection_0.data_0[i32(3)][i32(3)]))));
    var _S56 : f32 = clip_0.w;
    var ndc_1 : vec3<f32> = clip_0.xyz / vec3<f32>(_S56);
    var _S57 : f32 = ndc_1.x * 0.5f + 0.5f;
    var _S58 : f32 = 0.5f - ndc_1.y * 0.5f;
    var _S59 : f32 = ndc_1.z;
    var coord_0 : vec3<f32> = vec3<f32>(_S57, _S58, _S59);
    var _S60 : vec2<f32> = vec2<f32>(_S57, _S58);
    var uvDx_1 : vec2<f32> = dpdx(_S60);
    var uvDy_1 : vec2<f32> = dpdy(_S60);
    var depthDx_1 : f32 = dpdx(_S59);
    var depthDy_1 : f32 = dpdy(_S59);
    var _S61 : f32 = uvDx_1.x;
    var _S62 : f32 = uvDy_1.y;
    var _S63 : f32 = uvDx_1.y;
    var _S64 : f32 = uvDy_1.x;
    var determinant_1 : f32 = _S61 * _S62 - _S63 * _S64;
    var slopeBias_0 : f32;
    if((abs(determinant_1)) > 9.99999993922529029e-09f)
    {
        slopeBias_0 = dot(abs(vec2<f32>(_S62 * depthDx_1 - _S63 * depthDy_1, - _S64 * depthDx_1 + _S61 * depthDy_1) / vec2<f32>(determinant_1)), vec2<f32>(max(localShadow_0.spotFilter_0.y, texel_0))) * max(localShadow_0.spotBias_0.y, 0.0f);
    }
    else
    {
        slopeBias_0 = 0.0f;
    }
    var _S65 : f32 = max(localShadow_0.spotControl_0.z, 0.00009999999747379f);
    var _S66 : f32 = _S65 + 0.00009999999747379f;
    var _S67 : f32 = max(localShadow_0.spotControl_0.w, _S66);
    var _S68 : f32 = max(_S55, _S66);
    var receiverDepth_2 : f32 = _S59 - (max(worldTexel_0 * localShadow_0.spotBias_0.x, 0.0f) * _S65 * _S67 / max((_S67 - _S65) * _S68 * _S68, 9.99999993922529029e-09f) + max(slopeBias_0, 0.0f));
    if(_S56 <= 0.0f)
    {
        _S52 = true;
    }
    else
    {
        _S52 = _S55 >= _S67;
    }
    if(_S52)
    {
        _S52 = true;
    }
    else
    {
        _S52 = (any((coord_0 < vec3<f32>(0.0f))));
    }
    if(_S52)
    {
        _S52 = true;
    }
    else
    {
        _S52 = (any((coord_0 > vec3<f32>(1.0f))));
    }
    if(_S52)
    {
        return 1.0f;
    }
    var _S69 : f32 = localShadowRotation_0(pixel_4);
    var _S70 : f32 = max(localShadow_0.spotFilter_0.x, max(1.0f / _S53, 1.0f / _S54));
    var tap_1 : i32 = i32(0);
    var blockerSum_2 : f32 = 0.0f;
    var blockerCount_2 : i32 = i32(0);
    for(;;)
    {
        if(tap_1 < i32(8))
        {
        }
        else
        {
            break;
        }
        var _S71 : vec3<i32> = vec3<i32>(vec2<i32>(clamp((_S60 + vogelTap_0(tap_1, _S69) * vec2<f32>(_S70)) * size_0, vec2<f32>(0.0f), size_0 - vec2<f32>(1.0f))), i32(0));
        var depth_1 : f32 = (textureLoad((spotShadowMap_0), ((_S71)).xy, ((_S71)).z).x);
        if(depth_1 < receiverDepth_2)
        {
            var _S72 : i32 = blockerCount_2 + i32(1);
            blockerSum_2 = blockerSum_2 + depth_1;
            blockerCount_2 = _S72;
        }
        tap_1 = tap_1 + i32(1);
    }
    if(blockerCount_2 == i32(0))
    {
        return 1.0f;
    }
    var averageBlocker_2 : f32 = blockerSum_2 / f32(blockerCount_2);
    var _S73 : f32 = max(localShadow_0.spotFilter_0.z, max(1.0f / _S53, 1.0f / _S54));
    var _S74 : f32 = max(clamp(abs(receiverDepth_2 - averageBlocker_2) / max(abs(averageBlocker_2), 0.00009999999747379f) * max(localShadow_0.spotSource_0.x, 0.0f), _S73, max(localShadow_0.spotFilter_0.w, _S73)), max(1.0f / _S53, 1.0f / _S54));
    tap_1 = i32(0);
    var visibility_5 : f32 = 0.0f;
    for(;;)
    {
        if(tap_1 < i32(8))
        {
        }
        else
        {
            break;
        }
        if(receiverDepth_2 <= (textureSampleLevel((spotShadowMap_0), (spotShadowSampler_0), (_S60 + vogelTap_0(tap_1, _S69) * vec2<f32>(_S74)), (0.0f)).x))
        {
            slopeBias_0 = 1.0f;
        }
        else
        {
            slopeBias_0 = 0.0f;
        }
        var visibility_6 : f32 = visibility_5 + slopeBias_0;
        tap_1 = tap_1 + i32(1);
        visibility_5 = visibility_6;
    }
    return visibility_5 * 0.125f;
}

fn spotLight_0( positionRadius_1 : vec4<f32>,  colorIntensity_2 : vec4<f32>,  directionExponent_0 : vec4<f32>,  cutoffsBrightness_0 : vec4<f32>,  position_7 : vec3<f32>,  normal_9 : vec3<f32>,  viewDirection_3 : vec3<f32>,  albedo_3 : vec3<f32>,  rms_3 : vec3<f32>) -> vec3<f32>
{
    var lightVector_1 : vec3<f32> = positionRadius_1.xyz - position_7;
    var lightDirection_2 : vec3<f32> = safeNormalize_0(lightVector_1);
    var _S75 : f32 = max(0.0f, dot((vec3<f32>(0) - lightDirection_2), safeNormalize_0(directionExponent_0.xyz)));
    var _S76 : f32 = cutoffsBrightness_0.x;
    var _S77 : f32 = cutoffsBrightness_0.y;
    var cone_0 : f32;
    if(_S76 == _S77)
    {
        if(_S75 >= _S76)
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
        cone_0 = smoothstep(_S77, _S76, _S75);
    }
    return vec3<f32>((cone_0 * pow(_S75, directionExponent_0.w))) * directPbr_0(colorIntensity_2.xyz, colorIntensity_2.w, lightDirection_2, normal_9, viewDirection_3, albedo_3, rms_3, lightAttenuation_0(length(lightVector_1), positionRadius_1.w) * cutoffsBrightness_0.z);
}

fn ambientVisibility_0( framebufferPixel_0 : vec2<f32>,  albedo_4 : vec3<f32>) -> vec3<f32>
{
    if((lighting_0.ambientOcclusionControls_0.x) < 0.5f)
    {
        return vec3<f32>(1.0f);
    }
    var width_2 : u32;
    var height_2 : u32;
    {var dim = textureDimensions((ambientOcclusionTexture_0));((width_2)) = dim.x;((height_2)) = dim.y;};
    var visibility_7 : f32 = pow(saturate((textureSampleLevel((ambientOcclusionTexture_0), (ambientOcclusionSampler_0), (clamp(framebufferPixel_0 / max(vec2<f32>(f32(width_2), f32(height_2)), vec2<f32>(1.0f)), vec2<f32>(0.0f), vec2<f32>(0.99999898672103882f))), (0.0f))).x), max(lighting_0.ambientOcclusionControls_0.y, 0.00100000004749745f));
    if((lighting_0.ambientOcclusionControls_0.z) < 0.5f)
    {
        return vec3<f32>(visibility_7);
    }
    var _S78 : vec3<f32> = vec3<f32>(visibility_7);
    return max(_S78, (((vec3<f32>(2.04040002822875977f) * albedo_4 - vec3<f32>(0.33239999413490295f)) * _S78 + (vec3<f32>(-4.79510021209716797f) * albedo_4 + vec3<f32>(0.64170002937316895f))) * _S78 + (vec3<f32>(2.75519990921020508f) * albedo_4 + vec3<f32>(0.69029998779296875f))) * _S78);
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
fn standardLitFragment( _S79 : pixelInput_0, @builtin(position) position_8 : vec4<f32>) -> pixelOutput_0
{
    var normal_10 : vec3<f32> = safeNormalize_0(_S79.worldNormal_1);
    var viewDirection_4 : vec3<f32> = safeNormalize_0(view_0.cameraPosition_0.xyz - _S79.worldPosition_1);
    var albedo_5 : vec3<f32> = material_0.baseColorOpacity_0.xyz;
    var surface_0 : vec4<f32> = material_0.roughnessMetallicSpecularEmission_0;
    var albedo_6 : vec3<f32> = albedo_5 * (textureSample((baseColorTexture_0), (baseColorSampler_0), (_S79.uv_3))).xyz;
    if((material_0.textureControls_0.x) > 0.5f)
    {
        surface_0[i32(0)] = surface_0[i32(0)] * (textureSample((roughnessTexture_0), (roughnessSampler_0), (_S79.uv_3))).x;
    }
    if((material_0.textureControls_0.y) > 0.5f)
    {
        surface_0[i32(1)] = surface_0[i32(1)] * (textureSample((metallicTexture_0), (metallicSampler_0), (_S79.uv_3))).x;
    }
    var normal_11 : vec3<f32> = mappedTextureNormal_0(_S79.uv_3, normal_10, _S79.worldTangent_1, _S79.worldBitangent_1);
    var rms_4 : vec3<f32> = vec3<f32>(saturate(surface_0.x), saturate(surface_0.y), max(surface_0.z, 0.0f));
    var color_1 : vec3<f32> = vec3<f32>(0.0f);
    var color_2 : vec3<f32>;
    if((lighting_0.lightCounts_0.x) > 0.0f)
    {
        color_2 = directionalLight_0(lighting_0.directional0Direction_0, lighting_0.directional0ColorIntensity_0, normal_11, viewDirection_4, albedo_6, rms_4, selectedDirectionalVisibility_0(i32(0), _S79.worldPosition_1, normal_11, position_8.xy));
    }
    else
    {
        color_2 = color_1;
    }
    if((lighting_0.lightCounts_0.x) > 1.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional1Direction_0, lighting_0.directional1ColorIntensity_0, normal_11, viewDirection_4, albedo_6, rms_4, selectedDirectionalVisibility_0(i32(1), _S79.worldPosition_1, normal_11, position_8.xy));
    }
    if((lighting_0.lightCounts_0.x) > 2.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional2Direction_0, lighting_0.directional2ColorIntensity_0, normal_11, viewDirection_4, albedo_6, rms_4, selectedDirectionalVisibility_0(i32(2), _S79.worldPosition_1, normal_11, position_8.xy));
    }
    if((lighting_0.lightCounts_0.x) > 3.0f)
    {
        color_2 = color_2 + directionalLight_0(lighting_0.directional3Direction_0, lighting_0.directional3ColorIntensity_0, normal_11, viewDirection_4, albedo_6, rms_4, selectedDirectionalVisibility_0(i32(3), _S79.worldPosition_1, normal_11, position_8.xy));
    }
    if((lighting_0.lightCounts_0.y) > 0.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(0), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point0PositionRadius_0, lighting_0.point0ColorIntensity_0, lighting_0.point0Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 1.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(1), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point1PositionRadius_0, lighting_0.point1ColorIntensity_0, lighting_0.point1Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 2.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(2), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point2PositionRadius_0, lighting_0.point2ColorIntensity_0, lighting_0.point2Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 3.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(3), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point3PositionRadius_0, lighting_0.point3ColorIntensity_0, lighting_0.point3Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 4.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(4), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point4PositionRadius_0, lighting_0.point4ColorIntensity_0, lighting_0.point4Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 5.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(5), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point5PositionRadius_0, lighting_0.point5ColorIntensity_0, lighting_0.point5Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 6.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(6), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point6PositionRadius_0, lighting_0.point6ColorIntensity_0, lighting_0.point6Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.y) > 7.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedPointVisibility_0(i32(7), _S79.worldPosition_1, normal_11, position_8.xy)) * pointLight_0(lighting_0.point7PositionRadius_0, lighting_0.point7ColorIntensity_0, lighting_0.point7Brightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 0.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(0), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot0PositionRadius_0, lighting_0.spot0ColorIntensity_0, lighting_0.spot0DirectionExponent_0, lighting_0.spot0CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 1.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(1), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot1PositionRadius_0, lighting_0.spot1ColorIntensity_0, lighting_0.spot1DirectionExponent_0, lighting_0.spot1CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 2.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(2), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot2PositionRadius_0, lighting_0.spot2ColorIntensity_0, lighting_0.spot2DirectionExponent_0, lighting_0.spot2CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 3.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(3), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot3PositionRadius_0, lighting_0.spot3ColorIntensity_0, lighting_0.spot3DirectionExponent_0, lighting_0.spot3CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 4.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(4), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot4PositionRadius_0, lighting_0.spot4ColorIntensity_0, lighting_0.spot4DirectionExponent_0, lighting_0.spot4CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 5.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(5), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot5PositionRadius_0, lighting_0.spot5ColorIntensity_0, lighting_0.spot5DirectionExponent_0, lighting_0.spot5CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 6.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(6), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot6PositionRadius_0, lighting_0.spot6ColorIntensity_0, lighting_0.spot6DirectionExponent_0, lighting_0.spot6CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    if((lighting_0.lightCounts_0.z) > 7.0f)
    {
        color_2 = color_2 + vec3<f32>(selectedSpotVisibility_0(i32(7), _S79.worldPosition_1, normal_11, position_8.xy)) * spotLight_0(lighting_0.spot7PositionRadius_0, lighting_0.spot7ColorIntensity_0, lighting_0.spot7DirectionExponent_0, lighting_0.spot7CutoffsBrightness_0, _S79.worldPosition_1, normal_11, viewDirection_4, albedo_6, rms_4);
    }
    var ambient_0 : vec3<f32> = lighting_0.globalAmbient_0.xyz;
    var _S80 : f32 = max(ambient_0.x, max(ambient_0.y, ambient_0.z));
    var ambient_1 : vec3<f32>;
    if(_S80 <= 0.00009999999747379f)
    {
        ambient_1 = vec3<f32>(0.07999999821186066f);
    }
    else
    {
        ambient_1 = ambient_0 * vec3<f32>(max(1.0f, 0.07999999821186066f / _S80));
    }
    var _S81 : pixelOutput_0 = pixelOutput_0( vec4<f32>(color_2 + ambient_1 * ambientVisibility_0(position_8.xy, albedo_6) * albedo_6 + material_0.baseColorOpacity_0.xyz * vec3<f32>(surface_0.w), material_0.baseColorOpacity_0.w) );
    return _S81;
}

