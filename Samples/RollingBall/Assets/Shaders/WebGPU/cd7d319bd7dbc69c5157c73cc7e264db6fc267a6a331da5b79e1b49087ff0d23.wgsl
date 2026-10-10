@binding(0) @group(1) var<storage, read> quadTransforms_0 : array<vec4<f32>>;

struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct UIView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : UIView_std140_0;
@binding(1) @group(1) var<storage, read> quadColors_0 : array<vec4<f32>>;

@binding(2) @group(1) var<storage, read> quadBounds_0 : array<vec4<f32>>;

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

fn isnan_1( x_1 : vec4<f32>) -> vec4<bool>
{
    var result_0 : vec4<bool>;
    var i_0 : i32 = i32(0);
    for(;;)
    {
        if(i_0 < i32(4))
        {
        }
        else
        {
            break;
        }
        result_0[i_0] = isnan_0(x_1[i_0]);
        i_0 = i_0 + i32(1);
    }
    return result_0;
}

struct UIQuadOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @interpolate(flat) @location(0) color_0 : vec4<f32>,
    @interpolate(flat) @location(1) bounds_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn uiQuadVertex( _S4 : vertexInput_0, @builtin(instance_index) instanceId_0 : u32) -> UIQuadOutput_0
{
    var baseRow_0 : u32 = instanceId_0 * u32(4);
    var local_0 : vec4<f32> = vec4<f32>(_S4.position_1, 1.0f);
    var output_0 : UIQuadOutput_0;
    output_0.position_0 = (((vec4<f32>(local_0.x) * quadTransforms_0[baseRow_0] + vec4<f32>(local_0.y) * quadTransforms_0[baseRow_0 + u32(1)] + vec4<f32>(local_0.z) * quadTransforms_0[baseRow_0 + u32(2)] + vec4<f32>(local_0.w) * quadTransforms_0[baseRow_0 + u32(3)]) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.color_0 = quadColors_0[instanceId_0];
    output_0.bounds_0 = quadBounds_0[instanceId_0];
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @interpolate(flat) @location(0) color_1 : vec4<f32>,
    @interpolate(flat) @location(1) bounds_1 : vec4<f32>,
};

@fragment
fn uiQuadFragment( _S5 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    if((any((isnan_1(_S5.bounds_1)))))
    {
        discard;
    }
    var _S6 : pixelOutput_0 = pixelOutput_0( _S5.color_1 );
    return _S6;
}

