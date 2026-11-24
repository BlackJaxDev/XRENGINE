// Synthetic test shader for the shader translator. Written for the engine tests.
// It uses legacy FXC rules that Unity allows: implicit vector truncation, mixed-size
// operators and functions, writes to global variables, initializers of shader
// parameters, float switch selectors, and component type conversions.
Shader "XRTests/Translation Legacy HLSL"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Speed ("Speed", Float) = 1
        _Band ("Band", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;
            float _Speed;
            float _Band;
            float scratchMask;
            uniform float _Unused = 0.0f;
            Texture2D<float4> _Lookup;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 view : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                float3 view = WorldSpaceViewDir(v.vertex) * length(mul(unity_ObjectToWorld, float3(1, 0, 0)));
                o.view = view;
                return o;
            }

            float Pick(int index) { return index * 0.25; }

            float4 frag(v2f i) : SV_Target
            {
                _Time.y += 100;
                _Color.a = 1;
                scratchMask = saturate(length(i.uv - 0.5));
                float2 flat = normalize(i.view);
                float3 tint = _Color * _Speed;
                tint *= _Color;
                float2 offset = flat + _Color;
                float shade = Pick(i.uv) + _Color;
                float4 texel = lerp(_Lookup[int2(i.uv * 4)], _Lookup[uint2(i.uv * 4 + int2(1, 0))], frac(i.uv.x));
                shade += pow(2, 3) + dot(i.uv, 1) + min(tint, _Color).x + texel.x;
                shade += lerp(tint, 1.0, 0.5).x + lerp(0, tint, 0.5).y + lerp(tint, _Color, 0.5).z;
                switch (_Band)
                {
                    case 1: shade *= 0.5; break;
                    default: break;
                }
                return float4(tint * scratchMask + offset.x + shade, _Color.a) * sin(_Time.y * _Speed + _Band);
            }
            ENDCG
        }
    }
}
