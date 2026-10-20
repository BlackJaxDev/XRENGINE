@binding(0) @group(2) var<storage, read> lines_0 : array<u32>;

struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct DebugLineView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) viewport_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> view_0 : DebugLineView_std140_0;
struct DebugLineMaterial_std140_0
{
    @align(16) parameters_0 : vec4<f32>,
};

@binding(0) @group(1) var<uniform> material_0 : DebugLineMaterial_std140_0;
fn rsqrt_0( x_0 : f32) -> f32
{
    return 1.0f / sqrt(x_0);
}

fn unpackColor_0( packed_0 : u32) -> vec4<f32>
{
    return vec4<f32>(f32((packed_0 & (u32(255)))), f32((((packed_0 >> (u32(8)))) & (u32(255)))), f32((((packed_0 >> (u32(16)))) & (u32(255)))), f32((((packed_0 >> (u32(24)))) & (u32(255))))) / vec4<f32>(255.0f);
}

fn clipNear_0( a_0 : ptr<function, vec4<f32>>,  b_0 : ptr<function, vec4<f32>>) -> bool
{
    var da_0 : f32 = (*a_0).z;
    var db_0 : f32 = (*b_0).z;
    var _S1 : bool = da_0 < 0.0f;
    var _S2 : bool;
    if(_S1)
    {
        _S2 = db_0 < 0.0f;
    }
    else
    {
        _S2 = false;
    }
    if(_S2)
    {
        return false;
    }
    if(_S1)
    {
        _S2 = true;
    }
    else
    {
        _S2 = db_0 < 0.0f;
    }
    if(_S2)
    {
        var denominator_0 : f32 = da_0 - db_0;
        var t_0 : f32;
        if((abs(denominator_0)) < 9.99999997475242708e-07f)
        {
            t_0 = 0.0f;
        }
        else
        {
            t_0 = da_0 / denominator_0;
        }
        var crossing_0 : vec4<f32> = mix((*a_0), (*b_0), vec4<f32>(saturate(t_0)));
        if(_S1)
        {
            (*a_0) = crossing_0;
        }
        else
        {
            (*b_0) = crossing_0;
        }
    }
    if(((*a_0).w) > 9.99999997475242708e-07f)
    {
        _S2 = ((*b_0).w) > 9.99999997475242708e-07f;
    }
    else
    {
        _S2 = false;
    }
    return _S2;
}

struct DebugLineOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @interpolate(flat) @location(0) color_0 : vec4<f32>,
    @interpolate(linear) @location(1) edgeCoord_0 : f32,
    @interpolate(flat) @location(2) halfWidthPixels_0 : f32,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn debugLineVertex( _S3 : vertexInput_0, @builtin(instance_index) instanceId_0 : u32) -> DebugLineOutput_0
{
    var baseWord_0 : u32 = instanceId_0 * u32(7);
    var p1_0 : vec3<f32> = vec3<f32>((bitcast<f32>((lines_0[baseWord_0 + u32(3)]))), (bitcast<f32>((lines_0[baseWord_0 + u32(4)]))), (bitcast<f32>((lines_0[baseWord_0 + u32(5)]))));
    var start_0 : vec4<f32> = (((vec4<f32>(vec3<f32>((bitcast<f32>((lines_0[baseWord_0]))), (bitcast<f32>((lines_0[baseWord_0 + u32(1)]))), (bitcast<f32>((lines_0[baseWord_0 + u32(2)])))), 1.0f)) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    var end_0 : vec4<f32> = (((vec4<f32>(p1_0, 1.0f)) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    var output_0 : DebugLineOutput_0;
    output_0.color_0 = unpackColor_0(lines_0[baseWord_0 + u32(6)]);
    output_0.edgeCoord_0 = 0.0f;
    output_0.halfWidthPixels_0 = 0.0f;
    var _S4 : bool = clipNear_0(&(start_0), &(end_0));
    if(!_S4)
    {
        output_0.position_0 = vec4<f32>(2.0f, 2.0f, 0.0f, 1.0f);
        return output_0;
    }
    var viewport_1 : vec2<f32> = max(view_0.viewport_0.xy, vec2<f32>(1.0f));
    var _S5 : vec2<f32> = vec2<f32>(0.5f);
    var delta_0 : vec2<f32> = end_0.xy / vec2<f32>(end_0.w) * viewport_1 * _S5 - start_0.xy / vec2<f32>(start_0.w) * viewport_1 * _S5;
    var squaredLength_0 : f32 = dot(delta_0, delta_0);
    var direction_0 : vec2<f32>;
    if(squaredLength_0 < 9.99999997475242708e-07f)
    {
        direction_0 = vec2<f32>(1.0f, 0.0f);
    }
    else
    {
        direction_0 = delta_0 * vec2<f32>(rsqrt_0(squaredLength_0));
    }
    var _S6 : f32 = max(0.5f, material_0.parameters_0.x * min(viewport_1.x, viewport_1.y) * 0.5f);
    var rasterHalfWidth_0 : f32 = _S6 + 2.0f;
    var offsetNdc_0 : vec2<f32> = vec2<f32>(- direction_0.y, direction_0.x) * vec2<f32>(rasterHalfWidth_0) * (vec2<f32>(2.0f) / viewport_1);
    var side_0 : f32 = _S3.position_1.x;
    var endpoint_0 : vec4<f32>;
    if((_S3.position_1.y) > 0.0f)
    {
        endpoint_0 = end_0;
    }
    else
    {
        endpoint_0 = start_0;
    }
    output_0.position_0 = endpoint_0 + vec4<f32>(offsetNdc_0 * vec2<f32>(endpoint_0.w) * vec2<f32>(side_0), 0.0f, 0.0f);
    output_0.edgeCoord_0 = rasterHalfWidth_0 * side_0;
    output_0.halfWidthPixels_0 = _S6;
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @interpolate(flat) @location(0) color_1 : vec4<f32>,
    @interpolate(linear) @location(1) edgeCoord_1 : f32,
    @interpolate(flat) @location(2) halfWidthPixels_1 : f32,
};

@fragment
fn debugLineFragment( _S7 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var alpha_0 : f32 = _S7.color_1.w * (1.0f - smoothstep(_S7.halfWidthPixels_1, _S7.halfWidthPixels_1 + max((fwidth((_S7.edgeCoord_1))) * 1.25f, 0.75f), abs(_S7.edgeCoord_1)));
    if(alpha_0 <= 0.00100000004749745f)
    {
        discard;
    }
    var _S8 : pixelOutput_0 = pixelOutput_0( vec4<f32>(_S7.color_1.xyz, alpha_0) );
    return _S8;
}

