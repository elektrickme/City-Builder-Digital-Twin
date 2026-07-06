Shader "CityTwin/UI/RectOutline"
{
    // Procedural rounded-rectangle OUTLINE for UI: crisp stroke, no fill, no sprite needed.
    // Geometry is in pixels via _RectSize (set on the material to match the RectTransform).
    // Tint and alpha come from the Image vertex color, so scripts can drive Image.color.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _RectSize ("Rect Size (px)", Vector) = (530, 104, 0, 0)
        _CornerRadius ("Corner Radius (px)", Range(0, 60)) = 24
        _Thickness ("Stroke Thickness (px)", Range(1, 20)) = 4
        _GlowBoost ("HDR Glow Boost", Range(1, 6)) = 1

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "RectOutline"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _RectSize;
            float _CornerRadius;
            float _Thickness;
            float _GlowBoost;

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // signed distance to the rounded-box edge, in pixels
                float2 p = (i.texcoord - 0.5) * _RectSize.xy;
                float2 b = _RectSize.xy * 0.5 - _CornerRadius;
                float2 q = abs(p) - b;
                float dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - _CornerRadius;

                float aa = fwidth(dist);
                float half_t = _Thickness * 0.5;
                float stroke = 1.0 - smoothstep(half_t - aa, half_t + aa, abs(dist));

                half4 col = i.color;
                col.a *= stroke;
                col.rgb *= _GlowBoost; // > 1 = HDR: the bloom post pass halos the outline
                return col;
            }
            ENDCG
        }
    }
}
