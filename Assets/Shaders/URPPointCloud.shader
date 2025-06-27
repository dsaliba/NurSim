Shader "Custom/URPPointCloud"
{
    Properties
    {
        _PointSize("Point Size", Float) = 5.0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalRenderPipeline" "Queue"="Transparent" "RenderType"="Opaque" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR; // per-vertex color
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
            };

            float _PointSize;

            Varyings vert(Attributes input)
            {
                Varyings output;

                // Transform point to clip space
                output.positionHCS = TransformObjectToHClip(input.positionOS);

                // Pass vertex color through
                output.color = input.color;

                #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
                // Only works on OpenGL, sets point size
                gl_PointSize = _PointSize;
                #endif

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return input.color;
            }

            ENDHLSL
        }
    }
}
