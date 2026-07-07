Shader "UI/RoundedRadialFill"
{
    Properties
    {
        _MainTex ("Ring Texture", 2D) = "white" {}
        _Fill ("Fill Amount", Range(0, 1)) = 0.75
        _RingRadius ("Ring Center Radius", Range(0, 0.5)) = 0.38
        _RingThickness ("Ring Thickness", Range(0, 0.5)) = 0.08
        _StartAngle ("Start Angle (deg)", Float) = 90
        _StartCapScale ("Start Cap Scale", Range(0.5, 3.0)) = 1.0
        _EndCapScale ("End Cap Scale", Range(0.5, 3.0)) = 1.5
        _GlowBoost ("HDR Glow Boost", Range(1, 6)) = 1

        // Unity UI required
        _StencilComp ("Stencil Comp", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Op", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            float _Fill;
            float _RingRadius;
            float _RingThickness;
            float _StartAngle;
            float _StartCapScale;
            float _EndCapScale;
            float _GlowBoost;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv - 0.5;
                float dist = length(uv);
                float angle = atan2(uv.x, uv.y);

                float startRad = _StartAngle * 0.0174533;
                float a = fmod(angle - startRad + 6.2832, 6.2832) / 6.2832;

                float fillArc = _Fill;
                float halfThick = _RingThickness;

                float ringDist = abs(dist - _RingRadius);

                // Arc body
                float arcMask = step(a, fillArc);

                // Hide everything when fill is zero
                float hasFill = smoothstep(0.0, 0.02, _Fill);

                // Start cap
                float startCapR = halfThick * _StartCapScale;
                float sAngle = startRad;
                float2 capStartPos = float2(sin(sAngle), cos(sAngle)) * _RingRadius;
                float dStart = length(uv - capStartPos);

                // End cap
                float endCapR = halfThick * _EndCapScale;
                float eAngle = startRad + fillArc * 6.2832;
                float2 capEndPos = float2(sin(eAngle), cos(eAngle)) * _RingRadius;
                float dEnd = length(uv - capEndPos);

                // Antialiasing
                float aa = fwidth(dist);

                float ringAA = (1.0 - smoothstep(halfThick - aa, halfThick + aa, ringDist)) * arcMask;
                float capStartAA = (1.0 - smoothstep(startCapR - aa, startCapR + aa, dStart)) * hasFill;
                float capEndAA = (1.0 - smoothstep(endCapR - aa, endCapR + aa, dEnd)) * hasFill;

                float smoothMask = saturate(ringAA + capStartAA + capEndAA);

                // Sample gradient texture for color
                fixed4 col = tex2D(_MainTex, i.uv) * i.color;
                col.a *= smoothMask;
                col.rgb *= _GlowBoost; // > 1 = HDR: the bloom post pass turns this into a halo

                return col;
            }
            ENDCG
        }
    }
}