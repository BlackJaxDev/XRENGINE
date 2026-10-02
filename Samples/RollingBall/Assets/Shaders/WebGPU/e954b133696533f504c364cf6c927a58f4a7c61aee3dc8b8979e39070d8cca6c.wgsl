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
@binding(1) @group(1) var skyTexture_0 : texture_2d<f32>;

@binding(2) @group(1) var skySampler_0 : sampler;

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
fn skyVertex( _S1 : vertexInput_0) -> SkyVertexOutput_0
{
    var output_0 : SkyVertexOutput_0;
    var _S2 : vec4<f32> = vec4<f32>(_S1.position_1.xy, 1.0f, 1.0f);
    var viewPosition_0 : vec4<f32> = (((_S2) * (mat4x4<f32>(view_0.inverseProjection_0.data_0[i32(0)][i32(0)], view_0.inverseProjection_0.data_0[i32(1)][i32(0)], view_0.inverseProjection_0.data_0[i32(2)][i32(0)], view_0.inverseProjection_0.data_0[i32(3)][i32(0)], view_0.inverseProjection_0.data_0[i32(0)][i32(1)], view_0.inverseProjection_0.data_0[i32(1)][i32(1)], view_0.inverseProjection_0.data_0[i32(2)][i32(1)], view_0.inverseProjection_0.data_0[i32(3)][i32(1)], view_0.inverseProjection_0.data_0[i32(0)][i32(2)], view_0.inverseProjection_0.data_0[i32(1)][i32(2)], view_0.inverseProjection_0.data_0[i32(2)][i32(2)], view_0.inverseProjection_0.data_0[i32(3)][i32(2)], view_0.inverseProjection_0.data_0[i32(0)][i32(3)], view_0.inverseProjection_0.data_0[i32(1)][i32(3)], view_0.inverseProjection_0.data_0[i32(2)][i32(3)], view_0.inverseProjection_0.data_0[i32(3)][i32(3)]))));
    var _S3 : f32 = viewPosition_0.w;
    var invW_0 : f32;
    if((abs(_S3)) > 9.99999997475242708e-07f)
    {
        invW_0 = 1.0f / _S3;
    }
    else
    {
        invW_0 = 1.0f;
    }
    var direction_0 : vec3<f32> = (((vec4<f32>(viewPosition_0.xyz * vec3<f32>(invW_0), 0.0f)) * (mat4x4<f32>(view_0.inverseView_0.data_0[i32(0)][i32(0)], view_0.inverseView_0.data_0[i32(1)][i32(0)], view_0.inverseView_0.data_0[i32(2)][i32(0)], view_0.inverseView_0.data_0[i32(3)][i32(0)], view_0.inverseView_0.data_0[i32(0)][i32(1)], view_0.inverseView_0.data_0[i32(1)][i32(1)], view_0.inverseView_0.data_0[i32(2)][i32(1)], view_0.inverseView_0.data_0[i32(3)][i32(1)], view_0.inverseView_0.data_0[i32(0)][i32(2)], view_0.inverseView_0.data_0[i32(1)][i32(2)], view_0.inverseView_0.data_0[i32(2)][i32(2)], view_0.inverseView_0.data_0[i32(3)][i32(2)], view_0.inverseView_0.data_0[i32(0)][i32(3)], view_0.inverseView_0.data_0[i32(1)][i32(3)], view_0.inverseView_0.data_0[i32(2)][i32(3)], view_0.inverseView_0.data_0[i32(3)][i32(3)])))).xyz;
    var cosRotation_0 : f32 = cos(sky_0.rotation_0);
    var sinRotation_0 : f32 = sin(sky_0.rotation_0);
    var _S4 : f32 = direction_0.x;
    var _S5 : f32 = direction_0.z;
    output_0.worldDirection_0 = vec3<f32>(_S4 * cosRotation_0 - _S5 * sinRotation_0, direction_0.y, _S4 * sinRotation_0 + _S5 * cosRotation_0);
    output_0.position_0 = _S2;
    return output_0;
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
fn skyFragment( _S6 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var octDirection_0 : vec3<f32> = normalize(_S6.worldDirection_1).xzy;
    var octDirection_1 : vec3<f32> = octDirection_0 / vec3<f32>(max(abs(octDirection_0.x) + abs(octDirection_0.y) + abs(octDirection_0.z), 0.00000999999974738f));
    var uv_0 : vec2<f32> = octDirection_1.xy;
    var uv_1 : vec2<f32>;
    if((octDirection_1.z) < 0.0f)
    {
        var _S7 : f32;
        if((octDirection_1.x) >= 0.0f)
        {
            _S7 = 1.0f;
        }
        else
        {
            _S7 = -1.0f;
        }
        var _S8 : f32;
        if((octDirection_1.y) >= 0.0f)
        {
            _S8 = 1.0f;
        }
        else
        {
            _S8 = -1.0f;
        }
        uv_1 = (vec2<f32>(1.0f) - abs(uv_0.yx)) * vec2<f32>(_S7, _S8);
    }
    else
    {
        uv_1 = uv_0;
    }
    var _S9 : vec2<f32> = vec2<f32>(0.5f);
    var _S10 : pixelOutput_0 = pixelOutput_0( vec4<f32>((textureSample((skyTexture_0), (skySampler_0), (uv_1 * _S9 + _S9))).xyz * vec3<f32>(sky_0.intensity_0), 1.0f) );
    return _S10;
}

