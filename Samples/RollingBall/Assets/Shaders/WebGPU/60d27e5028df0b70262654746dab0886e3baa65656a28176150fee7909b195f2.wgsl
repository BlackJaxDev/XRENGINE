struct BloomCombineUniforms_std140_0
{
    @align(16) outputArea_0 : vec4<f32>,
    @align(16) controls_0 : vec4<f32>,
    @align(16) weights0To3_0 : vec4<f32>,
    @align(16) weights4_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> bloomCombine_0 : BloomCombineUniforms_std140_0;
@binding(1) @group(1) var bloomMip0_0 : texture_2d<f32>;

@binding(7) @group(1) var bloomSampler0_0 : sampler;

@binding(2) @group(1) var bloomMip1_0 : texture_2d<f32>;

@binding(8) @group(1) var bloomSampler1_0 : sampler;

@binding(3) @group(1) var bloomMip2_0 : texture_2d<f32>;

@binding(9) @group(1) var bloomSampler2_0 : sampler;

@binding(4) @group(1) var bloomMip3_0 : texture_2d<f32>;

@binding(10) @group(1) var bloomSampler3_0 : sampler;

@binding(5) @group(1) var bloomMip4_0 : texture_2d<f32>;

@binding(11) @group(1) var bloomSampler4_0 : sampler;

@binding(0) @group(1) var hdrTexture_0 : texture_2d<f32>;

@binding(6) @group(1) var hdrSampler_0 : sampler;

struct ScreenOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn bloomCombineVertex( _S1 : vertexInput_0) -> ScreenOutput_0
{
    var output_0 : ScreenOutput_0;
    output_0.position_0 = vec4<f32>(_S1.position_1.xy, 0.0f, 1.0f);
    return output_0;
}

fn sampleBloom_0( level_0 : i32,  uv_0 : vec2<f32>) -> vec3<f32>
{
    if(level_0 == i32(0))
    {
        return max((textureSampleLevel((bloomMip0_0), (bloomSampler0_0), (uv_0), (0.0f))).xyz, vec3<f32>(0.0f));
    }
    if(level_0 == i32(1))
    {
        return max((textureSampleLevel((bloomMip1_0), (bloomSampler1_0), (uv_0), (0.0f))).xyz, vec3<f32>(0.0f));
    }
    if(level_0 == i32(2))
    {
        return max((textureSampleLevel((bloomMip2_0), (bloomSampler2_0), (uv_0), (0.0f))).xyz, vec3<f32>(0.0f));
    }
    if(level_0 == i32(3))
    {
        return max((textureSampleLevel((bloomMip3_0), (bloomSampler3_0), (uv_0), (0.0f))).xyz, vec3<f32>(0.0f));
    }
    return max((textureSampleLevel((bloomMip4_0), (bloomSampler4_0), (uv_0), (0.0f))).xyz, vec3<f32>(0.0f));
}

fn mipWeight_0( level_1 : i32) -> f32
{
    if(level_1 == i32(0))
    {
        return bloomCombine_0.weights0To3_0.x;
    }
    if(level_1 == i32(1))
    {
        return bloomCombine_0.weights0To3_0.y;
    }
    if(level_1 == i32(2))
    {
        return bloomCombine_0.weights0To3_0.z;
    }
    if(level_1 == i32(3))
    {
        return bloomCombine_0.weights0To3_0.w;
    }
    return bloomCombine_0.weights4_0.x;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

@fragment
fn bloomCombineFragment(@builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var uv_1 : vec2<f32> = saturate((position_2.xy - bloomCombine_0.outputArea_0.xy) / max(bloomCombine_0.outputArea_0.zw, vec2<f32>(1.0f)));
    var border_0 : bool;
    var col_0 : i32;
    var borderColor_0 : vec3<f32>;
    if((bloomCombine_0.controls_0.w) > 0.5f)
    {
        if((uv_1.x) < 0.5f)
        {
            col_0 = i32(0);
        }
        else
        {
            col_0 = i32(1);
        }
        var row_0 : i32;
        if((uv_1.y) < 0.5f)
        {
            row_0 = i32(0);
        }
        else
        {
            row_0 = i32(1);
        }
        var level_2 : i32 = row_0 * i32(2) + col_0;
        var cellUV_0 : vec2<f32> = fract(uv_1 * vec2<f32>(2.0f));
        var _S2 : f32 = cellUV_0.x;
        if(_S2 < 0.00499999988824129f)
        {
            border_0 = true;
        }
        else
        {
            border_0 = _S2 > 0.99500000476837158f;
        }
        if(border_0)
        {
            border_0 = true;
        }
        else
        {
            border_0 = (cellUV_0.y) < 0.00499999988824129f;
        }
        if(border_0)
        {
            border_0 = true;
        }
        else
        {
            border_0 = (cellUV_0.y) > 0.99500000476837158f;
        }
        if(level_2 == i32(0))
        {
            borderColor_0 = vec3<f32>(1.0f, 0.0f, 0.0f);
        }
        else
        {
            if(level_2 == i32(1))
            {
                borderColor_0 = vec3<f32>(0.0f, 1.0f, 0.0f);
            }
            else
            {
                if(level_2 == i32(2))
                {
                    borderColor_0 = vec3<f32>(0.0f, 0.0f, 1.0f);
                }
                else
                {
                    borderColor_0 = vec3<f32>(1.0f, 1.0f, 0.0f);
                }
            }
        }
        if(border_0)
        {
        }
        else
        {
            borderColor_0 = sampleBloom_0(level_2, cellUV_0);
        }
        var _S3 : pixelOutput_0 = pixelOutput_0( vec4<f32>(borderColor_0, 1.0f) );
        return _S3;
    }
    var hdr_0 : vec4<f32> = (textureSampleLevel((hdrTexture_0), (hdrSampler_0), (uv_1), (0.0f)));
    var color_0 : vec3<f32> = max(hdr_0.xyz, vec3<f32>(0.0f));
    if((bloomCombine_0.controls_0.x) > 0.0f)
    {
        var startMip_0 : i32 = clamp(i32(bloomCombine_0.controls_0.y), i32(0), i32(4));
        var endMip_0 : i32 = clamp(i32(bloomCombine_0.controls_0.z), startMip_0, i32(4));
        if(startMip_0 == i32(1))
        {
            border_0 = endMip_0 == i32(4);
        }
        else
        {
            border_0 = false;
        }
        if(border_0)
        {
            borderColor_0 = color_0 + sampleBloom_0(i32(1), uv_1) * vec3<f32>(bloomCombine_0.controls_0.x);
        }
        else
        {
            col_0 = startMip_0;
            borderColor_0 = color_0;
            for(;;)
            {
                if(col_0 <= endMip_0)
                {
                }
                else
                {
                    break;
                }
                var weight_0 : f32 = mipWeight_0(col_0);
                if(weight_0 > 0.0f)
                {
                    borderColor_0 = borderColor_0 + sampleBloom_0(col_0, uv_1) * vec3<f32>(weight_0) * vec3<f32>(bloomCombine_0.controls_0.x);
                }
                col_0 = col_0 + i32(1);
            }
        }
    }
    else
    {
        borderColor_0 = color_0;
    }
    var _S4 : pixelOutput_0 = pixelOutput_0( vec4<f32>(borderColor_0, hdr_0.w) );
    return _S4;
}

