struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct SpotDepthView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : SpotDepthView_std140_0;
struct SpotDepthObject_std140_0
{
    @align(16) modelMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(1) @group(0) var<uniform> object_0 : SpotDepthObject_std140_0;
struct SpotDepthMaterial_std140_0
{
    @align(16) baseColorOpacity_0 : vec4<f32>,
    @align(16) coverage_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> material_0 : SpotDepthMaterial_std140_0;
struct vertexOutput_0
{
    @builtin(position) output_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_0 : vec3<f32>,
};

@vertex
fn spotShadowDepthVertex( _S1 : vertexInput_0) -> vertexOutput_0
{
    var _S2 : vertexOutput_0 = vertexOutput_0( ((((((vec4<f32>(_S1.position_0, 1.0f)) * (mat4x4<f32>(object_0.modelMatrix_0.data_0[i32(0)][i32(0)], object_0.modelMatrix_0.data_0[i32(1)][i32(0)], object_0.modelMatrix_0.data_0[i32(2)][i32(0)], object_0.modelMatrix_0.data_0[i32(3)][i32(0)], object_0.modelMatrix_0.data_0[i32(0)][i32(1)], object_0.modelMatrix_0.data_0[i32(1)][i32(1)], object_0.modelMatrix_0.data_0[i32(2)][i32(1)], object_0.modelMatrix_0.data_0[i32(3)][i32(1)], object_0.modelMatrix_0.data_0[i32(0)][i32(2)], object_0.modelMatrix_0.data_0[i32(1)][i32(2)], object_0.modelMatrix_0.data_0[i32(2)][i32(2)], object_0.modelMatrix_0.data_0[i32(3)][i32(2)], object_0.modelMatrix_0.data_0[i32(0)][i32(3)], object_0.modelMatrix_0.data_0[i32(1)][i32(3)], object_0.modelMatrix_0.data_0[i32(2)][i32(3)], object_0.modelMatrix_0.data_0[i32(3)][i32(3)]))))) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)])))) );
    return _S2;
}

struct pixelOutput_0
{
    @location(0) output_1 : f32,
};

@fragment
fn spotShadowDepthFragment(@builtin(position) position_1 : vec4<f32>) -> pixelOutput_0
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
    var _S4 : pixelOutput_0 = pixelOutput_0( position_1.z );
    return _S4;
}

