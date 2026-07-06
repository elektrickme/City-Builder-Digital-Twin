Shader "UI/CurvedBarFill"
{
    Properties
    {
        _MainTex ("Bar Texture", 2D) = "white" {}
        _Fill ("Fill Amount", Range(0, 1)) = 0.7
        _CapSize ("Cap Size", Range(0.0, 0.6)) = 0.05
        _Aspect ("Image Aspect (W/H)", Float) = 5.0
        _GlowBoost ("HDR Glow Boost", Range(1, 6)) = 1

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
            float _CapSize;
            float _Aspect;
            float _GlowBoost;

            // Find the vertical center of the bar at a given X by sampling alpha
            float findBarCenter(float xPos)
            {
                float topmost = 0.0;
                float bottommost = 1.0;
                float totalAlpha = 0.0;
                float weightedY = 0.0;

                // Sample several Y positions to find bar center
                for (int s = 0; s < 16; s++)
                {
                    float y = (float(s) + 0.5) / 16.0;
                    float a = tex2Dlod(_MainTex, float4(xPos, y, 0, 0)).a;
                    weightedY += y * a;
                    totalAlpha += a;
                }

                return totalAlpha > 0.01 ? weightedY / totalAlpha : 0.5;
            }

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
                fixed4 col = tex2D(_MainTex, i.uv) * i.color;

                float hasFill = smoothstep(0.0, 0.02, _Fill);

                // Hard fill mask
                float filled = step(i.uv.x, _Fill);

                // Find bar center at the fill edge
                float barCenterY = findBarCenter(_Fill);

                // End cap: circle at fill edge, centered on bar
                float2 capCenter = float2(_Fill, barCenterY);
                float2 delta = float2((i.uv.x - capCenter.x) * _Aspect, i.uv.y - capCenter.y);
                float capDist = length(delta);

                float aa = fwidth(capDist);
                float capMask = 1.0 - smoothstep(_CapSize - aa, _CapSize + aa, capDist);

                // Only show cap where texture has alpha (stay inside bar shape)
                capMask *= step(0.1, col.a);

                // Combine: filled region OR inside cap
                float mask = saturate(filled + capMask) * hasFill;

                col.a *= mask;
                col.rgb *= _GlowBoost; // > 1 = HDR: the bloom post pass turns this into a halo

                return col;
            }
            ENDCG
        }
    }
}