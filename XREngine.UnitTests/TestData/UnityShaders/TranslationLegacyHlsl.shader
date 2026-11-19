// Synthetic test shader for the shader translator. Written for the engine tests.
// It uses legacy FXC rules that Unity allows: implicit vector truncation, mixed-size
// operators, and writes to global variables.
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
                float3 view = WorldSpaceViewDir(v.vertex);
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
                return float4(tint * scratchMask + offset.x + shade, _Color.a) * sin(_Time.y * _Speed + _Band);
            }
            ENDCG
        }
    }
}
