// Synthetic test shader for the shader translator. Written for the engine tests.
// It uses HLSLPROGRAM, separate texture and sampler objects, screen-space UVs,
// alpha-to-coverage, and a stencil block.
Shader "XRTests/Translation Screen Eye"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (2, 2, 2, 1)
        _MainTex ("Iris", 2D) = "white" {}
        _StarTex ("Stars", 2D) = "black" {}
        _StarScale ("Star Scale", Float) = 4
        _StarSpeed ("Star Speed", Float) = 0.1
        _StencilRef ("Stencil Ref", Int) = 3
    }

    SubShader
    {
        Tags { "Queue" = "AlphaTest+10" "RenderType" = "TransparentCutout" }

        Pass
        {
            Tags { "LightMode" = "Always" }
            AlphaToMask On
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Stencil
            {
                Ref [_StencilRef]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            Texture2D _MainTex;
            Texture2D _StarTex;
            SamplerState sampler_MainTex;
            SamplerState sampler_linear_repeat;
            float4 _MainTex_ST;
            float4 _Color;
            float _StarScale;
            float _StarSpeed;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screenPos = ComputeScreenPos(o.pos);
                o.color = v.color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 screenUv = i.screenPos.xy / i.screenPos.w;
                float4 iris = _MainTex.Sample(sampler_MainTex, i.uv) * _Color * i.color;
                float2 starUv = screenUv * _StarScale + _Time.y * _StarSpeed;
                float stars = _StarTex.Sample(sampler_linear_repeat, starUv).r;
                float texel = _MainTex.Load(int3(0, 0, 0)).a;
                return float4(iris.rgb + stars, step(0.5, iris.a) * texel);
            }
            ENDHLSL
        }
    }
}
