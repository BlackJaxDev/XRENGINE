struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct TextureNormalView_std140_0
{
    @align(16) viewProjection_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(0) @group(0) var<uniform> view_0 : TextureNormalView_std140_0;
struct TextureNormalObject_std140_0
{
    @align(16) modelMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) normalMatrix_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

@binding(1) @group(0) var<uniform> object_0 : TextureNormalObject_std140_0;
@binding(3) @group(1) var normalTexture_0 : texture_2d<f32>;

@binding(4) @group(1) var normalSampler_0 : sampler;

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

struct TextureNormalOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) worldNormal_0 : vec3<f32>,
    @location(1) worldTangent_0 : vec3<f32>,
    @location(2) worldBitangent_0 : vec3<f32>,
    @location(3) uv_0 : vec2<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) normal_0 : vec3<f32>,
    @location(2) uv_1 : vec2<f32>,
    @location(3) tangent_0 : vec4<f32>,
};

@vertex
fn textureDepthNormalVertex( _S8 : vertexInput_0) -> TextureNormalOutput_0
{
    var output_0 : TextureNormalOutput_0;
    output_0.position_0 = ((((((vec4<f32>(_S8.position_1, 1.0f)) * (mat4x4<f32>(object_0.modelMatrix_0.data_0[i32(0)][i32(0)], object_0.modelMatrix_0.data_0[i32(1)][i32(0)], object_0.modelMatrix_0.data_0[i32(2)][i32(0)], object_0.modelMatrix_0.data_0[i32(3)][i32(0)], object_0.modelMatrix_0.data_0[i32(0)][i32(1)], object_0.modelMatrix_0.data_0[i32(1)][i32(1)], object_0.modelMatrix_0.data_0[i32(2)][i32(1)], object_0.modelMatrix_0.data_0[i32(3)][i32(1)], object_0.modelMatrix_0.data_0[i32(0)][i32(2)], object_0.modelMatrix_0.data_0[i32(1)][i32(2)], object_0.modelMatrix_0.data_0[i32(2)][i32(2)], object_0.modelMatrix_0.data_0[i32(3)][i32(2)], object_0.modelMatrix_0.data_0[i32(0)][i32(3)], object_0.modelMatrix_0.data_0[i32(1)][i32(3)], object_0.modelMatrix_0.data_0[i32(2)][i32(3)], object_0.modelMatrix_0.data_0[i32(3)][i32(3)]))))) * (mat4x4<f32>(view_0.viewProjection_0.data_0[i32(0)][i32(0)], view_0.viewProjection_0.data_0[i32(1)][i32(0)], view_0.viewProjection_0.data_0[i32(2)][i32(0)], view_0.viewProjection_0.data_0[i32(3)][i32(0)], view_0.viewProjection_0.data_0[i32(0)][i32(1)], view_0.viewProjection_0.data_0[i32(1)][i32(1)], view_0.viewProjection_0.data_0[i32(2)][i32(1)], view_0.viewProjection_0.data_0[i32(3)][i32(1)], view_0.viewProjection_0.data_0[i32(0)][i32(2)], view_0.viewProjection_0.data_0[i32(1)][i32(2)], view_0.viewProjection_0.data_0[i32(2)][i32(2)], view_0.viewProjection_0.data_0[i32(3)][i32(2)], view_0.viewProjection_0.data_0[i32(0)][i32(3)], view_0.viewProjection_0.data_0[i32(1)][i32(3)], view_0.viewProjection_0.data_0[i32(2)][i32(3)], view_0.viewProjection_0.data_0[i32(3)][i32(3)]))));
    output_0.worldNormal_0 = normalize((((vec4<f32>(_S8.normal_0, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    var _S9 : vec3<f32> = _S8.tangent_0.xyz;
    output_0.worldTangent_0 = normalize((((vec4<f32>(_S9, 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    output_0.worldBitangent_0 = normalize((((vec4<f32>(cross(_S8.normal_0, _S9) * vec3<f32>(_S8.tangent_0.w), 0.0f)) * (mat4x4<f32>(object_0.normalMatrix_0.data_0[i32(0)][i32(0)], object_0.normalMatrix_0.data_0[i32(1)][i32(0)], object_0.normalMatrix_0.data_0[i32(2)][i32(0)], object_0.normalMatrix_0.data_0[i32(3)][i32(0)], object_0.normalMatrix_0.data_0[i32(0)][i32(1)], object_0.normalMatrix_0.data_0[i32(1)][i32(1)], object_0.normalMatrix_0.data_0[i32(2)][i32(1)], object_0.normalMatrix_0.data_0[i32(3)][i32(1)], object_0.normalMatrix_0.data_0[i32(0)][i32(2)], object_0.normalMatrix_0.data_0[i32(1)][i32(2)], object_0.normalMatrix_0.data_0[i32(2)][i32(2)], object_0.normalMatrix_0.data_0[i32(3)][i32(2)], object_0.normalMatrix_0.data_0[i32(0)][i32(3)], object_0.normalMatrix_0.data_0[i32(1)][i32(3)], object_0.normalMatrix_0.data_0[i32(2)][i32(3)], object_0.normalMatrix_0.data_0[i32(3)][i32(3)])))).xyz);
    output_0.uv_0 = _S8.uv_1;
    return output_0;
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

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) worldNormal_1 : vec3<f32>,
    @location(1) worldTangent_1 : vec3<f32>,
    @location(2) worldBitangent_1 : vec3<f32>,
    @location(3) uv_3 : vec2<f32>,
};

@fragment
fn textureDepthNormalFragment( _S14 : pixelInput_0, @builtin(position) position_2 : vec4<f32>) -> pixelOutput_0
{
    var normal_2 : vec3<f32> = mappedTextureNormal_0(_S14.uv_3, _S14.worldNormal_1, _S14.worldTangent_1, _S14.worldBitangent_1);
    var n_1 : vec3<f32> = normal_2 / vec3<f32>(max(abs(normal_2.x) + abs(normal_2.y) + abs(normal_2.z), 9.99999997475242708e-07f));
    var oct_0 : vec2<f32> = n_1.xy;
    var oct_1 : vec2<f32>;
    if((n_1.z) < 0.0f)
    {
        var _S15 : vec2<f32> = vec2<f32>(1.0f) - abs(oct_0.yx);
        var _S16 : f32;
        if((n_1.x) >= 0.0f)
        {
            _S16 = 1.0f;
        }
        else
        {
            _S16 = -1.0f;
        }
        var _S17 : f32;
        if((n_1.y) >= 0.0f)
        {
            _S17 = 1.0f;
        }
        else
        {
            _S17 = -1.0f;
        }
        oct_1 = _S15 * vec2<f32>(_S16, _S17);
    }
    else
    {
        oct_1 = oct_0;
    }
    var _S18 : vec2<f32> = vec2<f32>(0.5f);
    var _S19 : pixelOutput_0 = pixelOutput_0( vec4<f32>(oct_1 * _S18 + _S18, 0.0f, 1.0f) );
    return _S19;
}

