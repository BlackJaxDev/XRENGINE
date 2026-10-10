@binding(3) @group(1) var<storage, read> glyphTextIndex_0 : array<u32>;

@binding(0) @group(1) var<storage, read> glyphTransforms_0 : array<vec4<f32>>;

@binding(1) @group(1) var<storage, read> glyphTexCoords_0 : array<vec4<f32>>;

@binding(2) @group(1) var<storage, read> textInstances_0 : array<vec4<f32>>;

struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct UITextView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : UITextView_std140_0;
@binding(0) @group(2) var fontAtlas_0 : texture_2d<f32>;

@binding(1) @group(2) var fontSampler_0 : sampler;

struct UITextOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
    @interpolate(flat) @location(1) glyphUvBounds_0 : vec4<f32>,
    @interpolate(flat) @location(2) fillColor_0 : vec4<f32>,
    @interpolate(flat) @location(3) outlineColor_0 : vec4<f32>,
    @interpolate(flat) @location(4) outlineThickness_0 : f32,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
};

@vertex
fn uiTextVertex( _S1 : vertexInput_0, @builtin(instance_index) instanceId_0 : u32) -> UITextOutput_0
{
    var textBase_0 : u32 = glyphTextIndex_0[instanceId_0] * u32(8);
    var glyph_0 : vec4<f32> = glyphTransforms_0[instanceId_0];
    var atlasUv_0 : vec4<f32> = glyphTexCoords_0[instanceId_0];
    var outline_0 : vec4<f32> = textInstances_0[textBase_0 + u32(6)];
    var thickness_0 : f32 = textInstances_0[textBase_0 + u32(7)].x;
    var corner_0 : vec2<f32> = _S1.position_1.xy;
    var glyphMin_0 : vec2<f32> = glyph_0.xy;
    var glyphSize_0 : vec2<f32> = glyph_0.zw;
    var uvMin_0 : vec2<f32> = atlasUv_0.xy;
    var uvMax_0 : vec2<f32> = atlasUv_0.zw;
    var _S2 : bool;
    if(thickness_0 > 0.0f)
    {
        _S2 = (outline_0.w) > 0.0f;
    }
    else
    {
        _S2 = false;
    }
    var glyphMin_1 : vec2<f32>;
    var glyphSize_1 : vec2<f32>;
    var uvMin_1 : vec2<f32>;
    var uvMax_1 : vec2<f32>;
    if(_S2)
    {
        var expansion_0 : vec2<f32> = vec2<f32>(thickness_0, thickness_0);
        var uvExpansion_0 : vec2<f32> = (uvMax_0 - uvMin_0) * expansion_0 / max(abs(glyphSize_0), vec2<f32>(9.99999997475242708e-07f, 9.99999997475242708e-07f));
        var _S3 : f32;
        if((glyphSize_0.x) < 0.0f)
        {
            _S3 = -1.0f;
        }
        else
        {
            _S3 = 1.0f;
        }
        var _S4 : f32;
        if((glyphSize_0.y) < 0.0f)
        {
            _S4 = -1.0f;
        }
        else
        {
            _S4 = 1.0f;
        }
        var direction_0 : vec2<f32> = vec2<f32>(_S3, _S4);
        var glyphSize_2 : vec2<f32> = glyphSize_0 + expansion_0 * vec2<f32>(2.0f) * direction_0;
        var uvMin_2 : vec2<f32> = uvMin_0 - uvExpansion_0;
        var uvMax_2 : vec2<f32> = uvMax_0 + uvExpansion_0;
        glyphMin_1 = glyphMin_0 - expansion_0 * direction_0;
        glyphSize_1 = glyphSize_2;
        uvMin_1 = uvMin_2;
        uvMax_1 = uvMax_2;
    }
    else
    {
        glyphMin_1 = glyphMin_0;
        glyphSize_1 = glyphSize_0;
        uvMin_1 = uvMin_0;
        uvMax_1 = uvMax_0;
    }
    var local_0 : vec4<f32> = vec4<f32>(glyphMin_1 + corner_0 * glyphSize_1, 0.0f, 1.0f);
    var output_0 : UITextOutput_0;
    output_0.position_0 = (((vec4<f32>(local_0.x) * textInstances_0[textBase_0] + vec4<f32>(local_0.y) * textInstances_0[textBase_0 + u32(1)] + vec4<f32>(local_0.z) * textInstances_0[textBase_0 + u32(2)] + vec4<f32>(local_0.w) * textInstances_0[textBase_0 + u32(3)]) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.uv_0 = mix(uvMin_1, uvMax_1, corner_0);
    output_0.glyphUvBounds_0 = atlasUv_0;
    output_0.fillColor_0 = textInstances_0[textBase_0 + u32(4)];
    output_0.outlineColor_0 = outline_0;
    output_0.outlineThickness_0 = thickness_0;
    return output_0;
}

fn sampleGlyph_0( uv_1 : vec2<f32>,  bounds_0 : vec4<f32>,  dx_0 : vec2<f32>,  dy_0 : vec2<f32>) -> f32
{
    var _S5 : f32 = uv_1.x;
    var _S6 : bool;
    if(_S5 < (bounds_0.x))
    {
        _S6 = true;
    }
    else
    {
        _S6 = _S5 > (bounds_0.z);
    }
    if(_S6)
    {
        _S6 = true;
    }
    else
    {
        _S6 = (uv_1.y) < (bounds_0.y);
    }
    if(_S6)
    {
        _S6 = true;
    }
    else
    {
        _S6 = (uv_1.y) > (bounds_0.w);
    }
    if(_S6)
    {
        return 0.0f;
    }
    return (textureSampleGrad((fontAtlas_0), (fontSampler_0), (uv_1), (dx_0), (dy_0))).x;
}

fn strokeRing_0( uv_2 : vec2<f32>,  bounds_1 : vec4<f32>,  dx_1 : vec2<f32>,  dy_1 : vec2<f32>,  radius_0 : f32) -> f32
{
    var _S7 : vec2<f32> = vec2<f32>(radius_0);
    var a_0 : vec2<f32> = dx_1 * _S7;
    var b_0 : vec2<f32> = dy_1 * _S7;
    var _S8 : vec2<f32> = vec2<f32>((radius_0 * 0.70710676908493042f));
    var diagonalA_0 : vec2<f32> = (dx_1 + dy_1) * _S8;
    var diagonalB_0 : vec2<f32> = (dx_1 - dy_1) * _S8;
    var _S9 : vec2<f32> = vec2<f32>((radius_0 * 0.92387950420379639f));
    var _S10 : vec2<f32> = dx_1 * _S9;
    var _S11 : vec2<f32> = vec2<f32>((radius_0 * 0.38268342614173889f));
    var _S12 : vec2<f32> = dy_1 * _S11;
    var c_0 : vec2<f32> = _S10 + _S12;
    var d_0 : vec2<f32> = _S10 - _S12;
    var _S13 : vec2<f32> = dx_1 * _S11;
    var _S14 : vec2<f32> = dy_1 * _S9;
    var e_0 : vec2<f32> = _S13 + _S14;
    var f_0 : vec2<f32> = _S13 - _S14;
    return max(max(max(max(max(max(max(max(max(max(max(max(max(max(max(max(0.0f, sampleGlyph_0(uv_2 + a_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - a_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + b_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - b_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + diagonalA_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - diagonalA_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + diagonalB_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - diagonalB_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + c_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - c_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + d_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - d_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + e_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - e_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 + f_0, bounds_1, dx_1, dy_1)), sampleGlyph_0(uv_2 - f_0, bounds_1, dx_1, dy_1));
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_3 : vec2<f32>,
    @interpolate(flat) @location(1) glyphUvBounds_1 : vec4<f32>,
    @interpolate(flat) @location(2) fillColor_1 : vec4<f32>,
    @interpolate(flat) @location(3) outlineColor_1 : vec4<f32>,
    @interpolate(flat) @location(4) outlineThickness_1 : f32,
};

@fragment
fn uiTextFragment( _S15 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var dx_2 : vec2<f32> = dpdx(_S15.uv_3);
    var dy_2 : vec2<f32> = dpdy(_S15.uv_3);
    var fill_0 : f32 = sampleGlyph_0(_S15.uv_3, _S15.glyphUvBounds_1, dx_2, dy_2);
    var _S16 : bool;
    if((_S15.outlineThickness_1) > 0.0f)
    {
        _S16 = (_S15.outlineColor_1.w) > 0.0f;
    }
    else
    {
        _S16 = false;
    }
    var outlineMask_0 : f32;
    if(_S16)
    {
        var radius_1 : f32 = clamp(_S15.outlineThickness_1, 0.0f, 5.0f);
        var steps_0 : i32 = i32(floor(radius_1));
        outlineMask_0 = fill_0;
        var i_0 : i32 = i32(1);
        for(;;)
        {
            if(i_0 <= i32(5))
            {
            }
            else
            {
                break;
            }
            if(i_0 > steps_0)
            {
                break;
            }
            var _S17 : f32 = max(outlineMask_0, strokeRing_0(_S15.uv_3, _S15.glyphUvBounds_1, dx_2, dy_2, f32(i_0)));
            var i_1 : i32 = i_0 + i32(1);
            outlineMask_0 = _S17;
            i_0 = i_1;
        }
        if((radius_1 - f32(steps_0)) > 0.00999999977648258f)
        {
            outlineMask_0 = max(outlineMask_0, strokeRing_0(_S15.uv_3, _S15.glyphUvBounds_1, dx_2, dy_2, radius_1));
        }
        outlineMask_0 = outlineMask_0 * (1.0f - smoothstep(0.25f, 0.85000002384185791f, fill_0));
    }
    else
    {
        outlineMask_0 = 0.0f;
    }
    var fillAlpha_0 : f32 = _S15.fillColor_1.w * fill_0;
    var outlineAlpha_0 : f32 = _S15.outlineColor_1.w * outlineMask_0;
    var _S18 : f32 = 1.0f - fillAlpha_0;
    var alpha_0 : f32 = fillAlpha_0 + outlineAlpha_0 * _S18;
    var premulRgb_0 : vec3<f32> = _S15.fillColor_1.xyz * vec3<f32>(fillAlpha_0) + _S15.outlineColor_1.xyz * vec3<f32>(outlineAlpha_0) * vec3<f32>(_S18);
    var rgb_0 : vec3<f32>;
    if(alpha_0 > 0.0f)
    {
        rgb_0 = premulRgb_0 / vec3<f32>(alpha_0);
    }
    else
    {
        rgb_0 = vec3<f32>(0.0f, 0.0f, 0.0f);
    }
    var _S19 : pixelOutput_0 = pixelOutput_0( vec4<f32>(rgb_0, alpha_0) );
    return _S19;
}

