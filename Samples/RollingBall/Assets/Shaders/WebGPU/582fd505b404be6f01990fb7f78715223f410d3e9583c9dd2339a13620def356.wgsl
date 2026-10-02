@binding(0) @group(2) var<storage, read> points_0 : array<u32>;

struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct DebugPointView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) inverseView_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : DebugPointView_std140_0;
struct DebugPointMaterial_std140_0
{
    @align(16) parameters_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> material_0 : DebugPointMaterial_std140_0;
fn unpackColor_0( packed_0 : u32) -> vec4<f32>
{
    return vec4<f32>(f32((packed_0 & (u32(255)))), f32((((packed_0 >> (u32(8)))) & (u32(255)))), f32((((packed_0 >> (u32(16)))) & (u32(255)))), f32((((packed_0 >> (u32(24)))) & (u32(255))))) / vec4<f32>(255.0f);
}

struct DebugPointOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @interpolate(flat) @location(0) color_0 : vec4<f32>,
    @location(1) uv_0 : vec2<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn debugPointVertex( _S1 : vertexInput_0, @builtin(instance_index) instanceId_0 : u32) -> DebugPointOutput_0
{
    var baseWord_0 : u32 = instanceId_0 * u32(4);
    var center_0 : vec3<f32> = vec3<f32>((bitcast<f32>((points_0[baseWord_0]))), (bitcast<f32>((points_0[baseWord_0 + u32(1)]))), (bitcast<f32>((points_0[baseWord_0 + u32(2)]))));
    var output_0 : DebugPointOutput_0;
    output_0.position_0 = (((vec4<f32>(center_0 + ((((vec4<f32>(1.0f, 0.0f, 0.0f, 0.0f)) * (mat4x4<f32>(view_0.inverseView_0.data_0[i32(0)][i32(0)], view_0.inverseView_0.data_0[i32(1)][i32(0)], view_0.inverseView_0.data_0[i32(2)][i32(0)], view_0.inverseView_0.data_0[i32(3)][i32(0)], view_0.inverseView_0.data_0[i32(0)][i32(1)], view_0.inverseView_0.data_0[i32(1)][i32(1)], view_0.inverseView_0.data_0[i32(2)][i32(1)], view_0.inverseView_0.data_0[i32(3)][i32(1)], view_0.inverseView_0.data_0[i32(0)][i32(2)], view_0.inverseView_0.data_0[i32(1)][i32(2)], view_0.inverseView_0.data_0[i32(2)][i32(2)], view_0.inverseView_0.data_0[i32(3)][i32(2)], view_0.inverseView_0.data_0[i32(0)][i32(3)], view_0.inverseView_0.data_0[i32(1)][i32(3)], view_0.inverseView_0.data_0[i32(2)][i32(3)], view_0.inverseView_0.data_0[i32(3)][i32(3)])))).xyz * vec3<f32>(_S1.position_1.x) + (((vec4<f32>(0.0f, 1.0f, 0.0f, 0.0f)) * (mat4x4<f32>(view_0.inverseView_0.data_0[i32(0)][i32(0)], view_0.inverseView_0.data_0[i32(1)][i32(0)], view_0.inverseView_0.data_0[i32(2)][i32(0)], view_0.inverseView_0.data_0[i32(3)][i32(0)], view_0.inverseView_0.data_0[i32(0)][i32(1)], view_0.inverseView_0.data_0[i32(1)][i32(1)], view_0.inverseView_0.data_0[i32(2)][i32(1)], view_0.inverseView_0.data_0[i32(3)][i32(1)], view_0.inverseView_0.data_0[i32(0)][i32(2)], view_0.inverseView_0.data_0[i32(1)][i32(2)], view_0.inverseView_0.data_0[i32(2)][i32(2)], view_0.inverseView_0.data_0[i32(3)][i32(2)], view_0.inverseView_0.data_0[i32(0)][i32(3)], view_0.inverseView_0.data_0[i32(1)][i32(3)], view_0.inverseView_0.data_0[i32(2)][i32(3)], view_0.inverseView_0.data_0[i32(3)][i32(3)])))).xyz * vec3<f32>(_S1.position_1.y)) * vec3<f32>((length(center_0 - (((vec4<f32>(0.0f, 0.0f, 0.0f, 1.0f)) * (mat4x4<f32>(view_0.inverseView_0.data_0[i32(0)][i32(0)], view_0.inverseView_0.data_0[i32(1)][i32(0)], view_0.inverseView_0.data_0[i32(2)][i32(0)], view_0.inverseView_0.data_0[i32(3)][i32(0)], view_0.inverseView_0.data_0[i32(0)][i32(1)], view_0.inverseView_0.data_0[i32(1)][i32(1)], view_0.inverseView_0.data_0[i32(2)][i32(1)], view_0.inverseView_0.data_0[i32(3)][i32(1)], view_0.inverseView_0.data_0[i32(0)][i32(2)], view_0.inverseView_0.data_0[i32(1)][i32(2)], view_0.inverseView_0.data_0[i32(2)][i32(2)], view_0.inverseView_0.data_0[i32(3)][i32(2)], view_0.inverseView_0.data_0[i32(0)][i32(3)], view_0.inverseView_0.data_0[i32(1)][i32(3)], view_0.inverseView_0.data_0[i32(2)][i32(3)], view_0.inverseView_0.data_0[i32(3)][i32(3)])))).xyz) * material_0.parameters_0.x)), 1.0f)) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.color_0 = unpackColor_0(points_0[baseWord_0 + u32(3)]);
    output_0.uv_0 = _S1.position_1.xy;
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @interpolate(flat) @location(0) color_1 : vec4<f32>,
    @location(1) uv_1 : vec2<f32>,
};

@fragment
fn debugPointFragment( _S2 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var radius_0 : f32 = length(_S2.uv_1);
    if(radius_0 > 1.0f)
    {
        discard;
    }
    var alpha_0 : f32 = 1.0f - smoothstep(0.94999998807907104f, 1.0f, radius_0);
    if(alpha_0 <= 0.00100000004749745f)
    {
        discard;
    }
    var _S3 : pixelOutput_0 = pixelOutput_0( vec4<f32>(_S2.color_1.xyz, _S2.color_1.w * alpha_0) );
    return _S3;
}

