struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct DepthNormalView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : DepthNormalView_std140_0;
struct DepthNormalObject_std140_0
{
    @align(16) modelMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) normalMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(1) @group(0) var<uniform> object_0 : DepthNormalObject_std140_0;
struct CoverageNormalMaterial_std140_0
{
    @align(16) baseColorOpacity_0 : vec4<f32>,
    @align(16) coverage_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> material_0 : CoverageNormalMaterial_std140_0;
fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

struct DepthNormalOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) worldNormal_0 : vec3<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) normal_0 : vec3<f32>,
};

@vertex
fn depthNormalVertex( _S1 : vertexInput_0) -> DepthNormalOutput_0
{
    var output_0 : DepthNormalOutput_0;
    output_0.position_0 = ((((((vec4<f32>(_S1.position_1, 1.0f)) * (mat4x4<f32>(object_0.modelMatrix_0.data_0[i32(0)][i32(0)], object_0.modelMatrix_0.data_0[i32(1)][i32(0)], object_0.modelMatrix_0.data_0[i32(2)][i32(0)], object_0.modelMatrix_0.data_0[i32(3)][i32(0)], object_0.modelMatrix_0.data_0[i32(0)][i32(1)], object_0.modelMatrix_0.data_0[i32(1)][i32(1)], object_0.modelMatrix_0.data_0[i32(2)][i32(1)], object_0.modelMatrix_0.data_0[i32(3)][i32(1)], object_0.modelMatrix_0.data_0[i32(0)][i32(2)], object_0.modelMatrix_0.data_0[i32(1)][i32(2)], object_0.modelMatrix_0.data_0[i32(2)][i32(2)], object_0.modelMatrix_0.data_0[i32(3)][i32(2)], object_0.modelMatrix_0.data_0[i32(0)][i32(3)], object_0.modelMatrix_0.data_0[i32(1)][i32(3)], object_0.modelMatrix_0.data_0[i32(2)][i32(3)], object_0.modelMatrix_0.data_0[i32(3)][i32(3)]))))) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.worldNormal_0 = (((vec4<f32>(_S1.normal_0, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz;
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) worldNormal_1 : vec3<f32>,
};

@fragment
fn depthNormalFragment( _S2 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var _S3 : bool;
    if((material_0.coverage_0.x) > 0.5f)
    {
        _S3 = (material_0.baseColorOpacity_0.w) < (material_0.coverage_0.y);
    }
    else
    {
        _S3 = false;
    }
    if(_S3)
    {
        discard;
    }
    var normal_1 : vec3<f32> = _S2.worldNormal_1 * vec3<f32>(rsqrt_0(max(dot(_S2.worldNormal_1, _S2.worldNormal_1), 9.99999968265522539e-21f)));
    var n_0 : vec3<f32> = normal_1 / vec3<f32>(max(abs(normal_1.x) + abs(normal_1.y) + abs(normal_1.z), 9.99999997475242708e-07f));
    var oct_0 : vec2<f32> = n_0.xy;
    var oct_1 : vec2<f32>;
    if((n_0.z) < 0.0f)
    {
        var _S4 : vec2<f32> = vec2<f32>(1.0f) - abs(oct_0.yx);
        var _S5 : f32;
        if((n_0.x) >= 0.0f)
        {
            _S5 = 1.0f;
        }
        else
        {
            _S5 = -1.0f;
        }
        var _S6 : f32;
        if((n_0.y) >= 0.0f)
        {
            _S6 = 1.0f;
        }
        else
        {
            _S6 = -1.0f;
        }
        oct_1 = _S4 * vec2<f32>(_S5, _S6);
    }
    else
    {
        oct_1 = oct_0;
    }
    var _S7 : vec2<f32> = vec2<f32>(0.5f);
    var _S8 : pixelOutput_0 = pixelOutput_0( vec4<f32>(oct_1 * _S7 + _S7, 0.0f, 1.0f) );
    return _S8;
}

