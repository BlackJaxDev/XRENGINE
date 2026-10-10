@binding(0) @group(2) var<storage, read> triangles_0 : array<u32>;

struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct DebugTriangleView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : DebugTriangleView_std140_0;
fn unpackColor_0( packed_0 : u32) -> vec4<f32>
{
    return vec4<f32>(f32((packed_0 & (u32(255)))), f32((((packed_0 >> (u32(8)))) & (u32(255)))), f32((((packed_0 >> (u32(16)))) & (u32(255)))), f32((((packed_0 >> (u32(24)))) & (u32(255))))) / vec4<f32>(255.0f);
}

struct DebugTriangleOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @interpolate(flat) @location(0) color_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn debugTriangleVertex( _S1 : vertexInput_0, @builtin(instance_index) instanceId_0 : u32) -> DebugTriangleOutput_0
{
    var baseWord_0 : u32 = instanceId_0 * u32(10);
    var cornerWord_0 : u32 = baseWord_0 + min(u32(_S1.position_1.x), u32(2)) * u32(3);
    var output_0 : DebugTriangleOutput_0;
    output_0.position_0 = (((vec4<f32>(vec3<f32>((bitcast<f32>((triangles_0[cornerWord_0]))), (bitcast<f32>((triangles_0[cornerWord_0 + u32(1)]))), (bitcast<f32>((triangles_0[cornerWord_0 + u32(2)])))), 1.0f)) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.color_0 = unpackColor_0(triangles_0[baseWord_0 + u32(9)]);
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @interpolate(flat) @location(0) color_1 : vec4<f32>,
};

@fragment
fn debugTriangleFragment( _S2 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var _S3 : pixelOutput_0 = pixelOutput_0( _S2.color_1 );
    return _S3;
}

