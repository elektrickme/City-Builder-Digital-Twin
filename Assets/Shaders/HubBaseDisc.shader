Shader "CityTwin/UI/HubBaseDisc"
{
    // Procedural replacement for the City Hub base PNG: a dark disc with a radial
    // gradient (dark center -> lighter edge) and a thin rim stroke, all vector so the
    // hub scales crisply at any size. The rim carries the HDR glow for the blink pulse.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _CenterColor ("Fill Center Color", Color) = (0.045, 0.055, 0.13, 1)
        _EdgeColor ("Fill Edge Color", Color) = (0.24, 0.26, 0.34, 1)
        _GradientPower ("Gradient Falloff", Range(0.25, 4)) = 1.6
        _RimColor ("Rim Color", Color) = (0.016, 0.757, 0.996, 1)
        _DiscRadius ("Disc Radius (UV)", Range(0.1, 0.5)) = 0.46
        _RimThickness ("Rim Thickness (UV)", Range(0.001, 0.1)) = 0.012
        _GlowBoost ("HDR Glow Boost (rim)", Range(1, 6)) = 1

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
            Name "HubBaseDisc"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _CenterColor, _EdgeColor, _RimColor;
            float _GradientPower;
            float _DiscRadius;
            float _RimThickness;
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
                float2 p = i.texcoord - 0.5;
                float r = length(p);
                float aa = max(fwidth(r), 0.001) * 1.5;

                // The rim straddles the disc edge; fill stops at the rim's inner side.
                float rimInner = _DiscRadius - _RimThickness;
                float fillMask = 1.0 - smoothstep(rimInner - aa, rimInner + aa, r);
                float rimMask = (1.0 - smoothstep(_DiscRadius - aa, _DiscRadius + aa, r))
                              * smoothstep(rimInner - aa, rimInner + aa, r);

                // Radial gradient across the fill: center color out to edge color.
                float t = pow(saturate(r / max(rimInner, 0.001)), _GradientPower);
                float3 fillRgb = lerp(_CenterColor.rgb, _EdgeColor.rgb, t);

                float fillA = fillMask * _CenterColor.a;
                float rimA = rimMask * _RimColor.a;
                float3 rgb = fillRgb * fillA + _RimColor.rgb * _GlowBoost * rimA;
                float alpha = max(fillA, rimA);

                half4 col = half4(rgb, alpha) * i.color;
                return col;
            }
            ENDCG
        }
    }
}
