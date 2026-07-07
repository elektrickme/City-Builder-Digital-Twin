Shader "CityTwin/UI/HubStatRing"
{
    // Procedural replacement for the four hand-drawn hub stat arcs: one square RawImage draws
    // all four quadrant arcs mathematically, so alignment, radius, and the gaps between
    // sections are exact by construction. Quadrants (0 deg = 12 o'clock, clockwise):
    //   A = top-right, B = bottom-right, C = bottom-left, D = top-left.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _FillA ("Fill Top-Right", Range(0, 1)) = 0
        _FillB ("Fill Bottom-Right", Range(0, 1)) = 0
        _FillC ("Fill Bottom-Left", Range(0, 1)) = 0
        _FillD ("Fill Top-Left", Range(0, 1)) = 0
        _ColorA ("Color Top-Right", Color) = (0.20, 0.78, 0.96, 1)
        _ColorB ("Color Bottom-Right", Color) = (1.00, 0.63, 0.15, 1)
        _ColorC ("Color Bottom-Left", Color) = (0.65, 0.91, 0.15, 1)
        _ColorD ("Color Top-Left", Color) = (1.00, 0.18, 0.70, 1)
        _GapDegrees ("Gap Between Sections (deg)", Range(0, 45)) = 14
        _RingRadius ("Ring Radius (UV)", Range(0.1, 0.5)) = 0.42
        _Thickness ("Ring Half-Thickness (UV)", Range(0.005, 0.1)) = 0.03
        _TrackAlpha ("Empty Track Alpha", Range(0, 1)) = 0.16
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
            Name "HubStatRing"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _FillA, _FillB, _FillC, _FillD;
            fixed4 _ColorA, _ColorB, _ColorC, _ColorD;
            float _GapDegrees;
            float _RingRadius;
            float _Thickness;
            float _TrackAlpha;
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

            // Mask for one arc: quadrant start angle (deg from top, CW), swept degrees, with
            // rounded caps at both ends. Caps carry the anti-aliasing at the angular edges.
            float ArcMask(float2 p, float r, float ang, float startDeg, float sweepDeg, float aa)
            {
                if (sweepDeg <= 0.001) return 0.0;

                float local = ang - startDeg;
                float inSweep = step(0.0, local) * step(local, sweepDeg);
                float ringDist = abs(r - _RingRadius);
                float body = inSweep * (1.0 - smoothstep(_Thickness - aa, _Thickness + aa, ringDist));

                float a0 = radians(startDeg);
                float a1 = radians(startDeg + sweepDeg);
                float2 cap0 = float2(sin(a0), cos(a0)) * _RingRadius;
                float2 cap1 = float2(sin(a1), cos(a1)) * _RingRadius;
                float d0 = length(p - cap0);
                float d1 = length(p - cap1);
                float caps = max(1.0 - smoothstep(_Thickness - aa, _Thickness + aa, d0),
                                 1.0 - smoothstep(_Thickness - aa, _Thickness + aa, d1));

                return max(body, caps);
            }

            // One quadrant: faint full-length track underneath + the actual fill arc on top.
            void Quadrant(float2 p, float r, float ang, int q, float fill, fixed4 col, float aa,
                          inout float3 rgb, inout float alpha)
            {
                float startDeg = q * 90.0 + _GapDegrees * 0.5;
                float sweepMax = 90.0 - _GapDegrees;

                float track = ArcMask(p, r, ang, startDeg, sweepMax, aa) * _TrackAlpha;
                // Visibility floor: the rounded caps give any arc a minimum on-screen size, so
                // trace amounts (cross-category crumbs from the sim) must render as nothing.
                float hasFill = smoothstep(0.015, 0.05, fill);
                float arc = ArcMask(p, r, ang, startDeg, sweepMax * saturate(fill), aa) * hasFill;

                float a = max(arc, track) * col.a;
                rgb += col.rgb * a;
                alpha = max(alpha, a);
            }

            half4 frag(v2f i) : SV_Target
            {
                float2 p = i.texcoord - 0.5;
                float r = length(p);
                float ang = degrees(atan2(p.x, p.y)); // 0 at 12 o'clock, clockwise
                if (ang < 0.0) ang += 360.0;
                float aa = max(fwidth(r), 0.001) * 1.5;

                float3 rgb = 0;
                float alpha = 0;
                Quadrant(p, r, ang, 0, _FillA, _ColorA, aa, rgb, alpha); // top-right
                Quadrant(p, r, ang, 1, _FillB, _ColorB, aa, rgb, alpha); // bottom-right
                Quadrant(p, r, ang, 2, _FillC, _ColorC, aa, rgb, alpha); // bottom-left
                Quadrant(p, r, ang, 3, _FillD, _ColorD, aa, rgb, alpha); // top-left

                half4 col = half4(rgb, alpha) * i.color;
                col.rgb *= _GlowBoost; // > 1 = HDR: the bloom post pass halos the arcs
                return col;
            }
            ENDCG
        }
    }
}
