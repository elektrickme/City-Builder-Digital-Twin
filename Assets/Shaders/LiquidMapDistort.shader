// UI Image shader that warps the map sprite by a height field and adds a liquid
// specular glint from the surface slope. Drop-in replacement for the Default UI
// Material on the "Main BG" Image. Based on Unity's UI-Default so canvas clip
// rect, tint colour and alpha all still work.
Shader "CityTwin/UI/LiquidMapDistort"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0

        _HeightTex ("Height Field", 2D) = "black" {}
        _HeightTexel ("Height Texel (1/w,1/h)", Vector) = (0.004,0.008,0,0)
        _DisplaceStrength ("Displace Strength (uv)", Float) = 0.02
        _NormalStrength ("Normal Strength", Float) = 2.5
        _Gloss ("Gloss", Float) = 40
        _SpecStrength ("Spec Strength", Float) = 0.5
        _SpecTint ("Spec Colour", Color) = (1,1,1,1)
        _LightDir ("Light Dir (xy)", Vector) = (0.5,0.6,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent"
            "PreviewType"="Plane" "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp]
            ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            sampler2D _HeightTex;
            float4 _HeightTexel;
            float  _DisplaceStrength;
            float  _NormalStrength;
            float  _Gloss;
            float  _SpecStrength;
            fixed4 _SpecTint;
            float4 _LightDir;

            v2f vert (appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.worldPosition = v.vertex;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f IN) : SV_Target
            {
                float2 ht = _HeightTexel.xy;
                float2 uv = IN.texcoord;

                // surface gradient from the height field
                float hL = tex2D(_HeightTex, uv - float2(ht.x, 0)).r;
                float hR = tex2D(_HeightTex, uv + float2(ht.x, 0)).r;
                float hD = tex2D(_HeightTex, uv - float2(0, ht.y)).r;
                float hU = tex2D(_HeightTex, uv + float2(0, ht.y)).r;
                float2 grad = float2(hR - hL, hU - hD);

                // refract the sprite lookup along the slope
                float2 duv = grad * _DisplaceStrength;
                half4 color = (tex2D(_MainTex, uv + duv) + _TextureSampleAdd) * IN.color;

                // liquid specular glint from the slope
                float3 n = normalize(float3(-grad * _NormalStrength, 1.0));
                float3 l = normalize(float3(_LightDir.xy, 1.0));
                float spec = pow(saturate(dot(n, l)), _Gloss) * _SpecStrength;
                color.rgb += spec * _SpecTint.rgb * color.a;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDCG
        }
    }
}
