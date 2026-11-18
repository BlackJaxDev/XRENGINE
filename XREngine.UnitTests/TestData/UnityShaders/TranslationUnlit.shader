// Synthetic test shader for the shader translator. Written for the engine tests.
Shader "XRTests/Translation Unlit"
{
    Properties
    {
        [HDR] _Color ("Tint", Color) = (1, 0.5, 0.25, 1)
        _MainTex ("Main Texture", 2D) = "white" {}
        _ScrollSpeed ("Scroll Speed", Range(0, 4)) = 1
        [Toggle(_SCROLL_ON)] _Scroll ("Scroll", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Int) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100
        Cull [_Cull]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma shader_feature_local _SCROLL_ON
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _ScrollSpeed;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            #ifdef _SCROLL_ON
                o.uv.x += _Time.y * _ScrollSpeed;
            #endif
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }

    Fallback "Unlit/Texture"
}
