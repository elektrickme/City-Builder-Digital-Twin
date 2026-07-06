Shader "CityTwin/UI/HoloHalo"
{
    // Animated hologram halo for building markers: soft base glow (from the halo sprite)
    // plus an expanding sonar ping. Tint and alpha come from the Image vertex color, so
    // connection-state colors and the breath pulse from BuildingMarkerDisplay keep working
    // unchanged. No hard rings/dashes — those scale badly across differently-sized halos.
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _GlowStrength ("Base Glow Strength", Range(0, 2)) = 0.55
        _GlowBoost ("HDR Glow Boost", Range(1, 6)) = 2.0
        _RingRadius ("Halo Radius (UV)", Range(0.1, 0.5)) = 0.42
        _PulsePeriod ("Sonar Ping Period (sec)", Range(0.5, 20)) = 6
        _PulseWidth ("Sonar Ping Width (UV)", Range(0.005, 0.2)) = 0.05
        _PulseStrength ("Sonar Ping Strength", Range(0, 3)) = 0.9
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "HoloHalo"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _GlowStrength;
            float _GlowBoost;
            float _RingRadius;
            float _PulsePeriod;
            float _PulseWidth;
            float _PulseStrength;

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

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.texcoord - 0.5;
                float r = length(p);

                // 1. soft base glow from the authored halo sprite
                float glow = tex2D(_MainTex, i.texcoord).a * _GlowStrength;

                // 2. expanding sonar ping, fading as it travels outward
                float pt = frac(_Time.y / _PulsePeriod);
                float pulseR = pt * _RingRadius;
                float pulse = (1.0 - smoothstep(0.0, _PulseWidth, abs(r - pulseR)))
                              * (1.0 - pt) * (1.0 - pt) * _PulseStrength;

                // keep everything inside the halo disc, with a soft edge
                float inside = 1.0 - smoothstep(_RingRadius, _RingRadius + 0.06, r);

                float a = saturate(glow + pulse) * inside;
                half4 col = i.color;
                col.a *= a;
                // HDR: base boost pushes the halo over 1 for the bloom pass; the ping rides higher still.
                col.rgb *= _GlowBoost * (1.0 + pulse * 0.5);
                return col;
            }
            ENDCG
        }
    }
}
