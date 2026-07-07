// Height-field ripple simulation, one Blit per frame per instance.
// State is packed into RG: R = height(t), G = height(t-1). Disturbances from
// scene objects are injected as gaussian splats folded straight into the update
// pass, so there is no separate disturbance RenderTexture. Extremely light:
// a single fullscreen pass over a small (~256x128) RGHalf ping-pong target.
Shader "Hidden/CityTwin/LiquidWaveUpdate"
{
    Properties { _MainTex ("Prev State", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            #define MAX_SPLATS 32

            sampler2D _MainTex;
            float4 _TexelSize;        // xy = 1/width, 1/height (of the sim RT)
            float  _WaveSpeed;        // c^2 term, keep < 0.5 for stability
            float  _Damping;          // <1 dissipates energy so ripples decay
            float  _AspectHW;         // rect height / rect width -> circular splats
            float4 _SplatData[MAX_SPLATS]; // xy = uv, z = radius(uv), w = strength
            int    _SplatCount;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float2 t  = _TexelSize.xy;

                float2 s  = tex2D(_MainTex, uv).rg;
                float  h  = s.r;   // height now
                float  hp = s.g;   // height previous

                // 5-point laplacian (edges clamp -> reflective boundary)
                float lap = tex2D(_MainTex, uv + float2(t.x, 0)).r
                          + tex2D(_MainTex, uv - float2(t.x, 0)).r
                          + tex2D(_MainTex, uv + float2(0, t.y)).r
                          + tex2D(_MainTex, uv - float2(0, t.y)).r
                          - 4.0 * h;

                // discretized wave equation + damping
                float nh = (2.0 * h - hp) + _WaveSpeed * lap;
                nh *= _Damping;

                // inject object disturbances (gaussian, corrected to world-circular)
                [loop] for (int k = 0; k < _SplatCount; k++)
                {
                    float2 d = uv - _SplatData[k].xy;
                    d.y *= _AspectHW;
                    float r = _SplatData[k].z;
                    float f = exp(-dot(d, d) / max(r * r, 1e-6));
                    nh += _SplatData[k].w * f;
                }

                return float4(nh, h, 0, 0);
            }
            ENDCG
        }
    }
    Fallback Off
}
