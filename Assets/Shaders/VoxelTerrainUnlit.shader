Shader "CityTwin/VoxelTerrainUnlit"
{
    // Instanced unlit cube shader for the voxel map terrain. Renders through the URP 2D
    // renderer via the SRPDefaultUnlit pass, so it needs no lights and no 3D renderer.
    // Per-instance color arrives through an instancing buffer; a fixed normal-based
    // shade fakes top/side lighting so the voxels read as 3D.
    Properties
    {
        _TopLight ("Top Face Brightness", Range(0.5, 2)) = 1.15
        _SideLight ("Side Face Brightness", Range(0.1, 1)) = 0.55
        _FrontLight ("Front Face Brightness", Range(0.1, 1.5)) = 0.85
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="SRPDefaultUnlit" }
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _VoxelColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            float _TopLight;
            float _SideLight;
            float _FrontLight;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float shade : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);

                float3 n = TransformObjectToWorldNormal(v.normalOS);
                // Blend brightness by dominant world axis of the face normal.
                float top = saturate(n.y);
                float front = saturate(-n.z);
                float side = 1.0 - top - front;
                o.shade = top * _TopLight + front * _FrontLight + saturate(side) * _SideLight;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _VoxelColor);
                return half4(c.rgb * i.shade, 1);
            }
            ENDHLSL
        }
    }
}
