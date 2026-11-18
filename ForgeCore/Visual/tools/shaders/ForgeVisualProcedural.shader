// ForgeVisual's parametric drawing shader: one pass that runs one of the catalogue's programs (Data/fx-programs.json; the functions are
// in ForgeFxPrograms.hlsl, the dispatch is generated from the catalogue by ForgeVisualShaderBuild.cs) chosen by the integer _Program, with up to
// eight parameters (_FxP0, _FxP1), four colours (_FxC0..3) and the layer transform (_FxXf: scale, rotation, spin in radians, intensity).
// Two uses, chosen by material values, no keyword:
// - _FxUnwrap 1: the mesh is laid out by its UV into the render target (blend One Zero, no depth, no culling); the fragment gets the
//   object-space position and normal, and _FxView as the object-space direction to the eye.
// - _FxUnwrap 0: the mesh is drawn in the world (blend from _Src/_Dst: alpha or additive), the eye direction per vertex.
// ENABLE_FPS_RENDERING (the game's keyword for things in the hands) moves the clip position toward the first-person item camera's
// projection (_FPS_VP, a global the game's FPS_Render sets) by _FPSWeight, as the game's own gear and clothing shaders do.
// Built with Unity 2019.4.21f1 by tools/shaders/build_shaders.ps1 into Native/ShaderBundle.Generated.cs.
Shader "ForgeVisual/Procedural"
{
    Properties
    {
        _Program ("Program", Int) = 0
        _FxP0 ("Parameters 1-4", Vector) = (0,0,0,0)
        _FxP1 ("Parameters 5-8", Vector) = (0,0,0,0)
        _FxC0 ("Colour 1", Color) = (1,1,1,1)
        _FxC1 ("Colour 2", Color) = (0,0,0,0)
        _FxC2 ("Colour 3", Color) = (0,0,0,0)
        _FxC3 ("Colour 4", Color) = (0,0,0,0)
        _FxXf ("Scale, rotation, spin, intensity", Vector) = (1,0,0,1)
        _FxTime ("Time", Float) = 0
        _FxView ("Object-space eye direction (unwrap)", Vector) = (0,0,1,0)
        _FxUnwrap ("Lay out by UV", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _Src ("Source blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _Dst ("Destination blend", Float) = 10
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _FPSWeight ("First-person weight", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Pass
        {
            Blend [_Src] [_Dst]
            ZWrite Off
            ZTest [_ZTest]
            Cull [_Cull]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ ENABLE_FPS_RENDERING
            // Unity 2019.4.21 shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; compiling unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            #include "UnityCG.cginc"
            #define FX_HLSL 1
            #include "ForgeFxPrograms.hlsl"
            #include "ForgeFxDispatch.hlsl"

            int _Program;
            float4 _FxP0, _FxP1, _FxC0, _FxC1, _FxC2, _FxC3, _FxXf, _FxView;
            float _FxTime, _FxUnwrap, _FPSWeight;
            float4x4 _FPS_VP;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 opos : TEXCOORD1;
                float3 onrm : TEXCOORD2;
                float3 oview : TEXCOORD3;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0));
                float4 clip = mul(UNITY_MATRIX_VP, world);
                #if ENABLE_FPS_RENDERING
                clip = lerp(clip, mul(_FPS_VP, world), _FPSWeight);
                #endif
                float3 eye = normalize(ObjSpaceViewDir(v.vertex));
                if (_FxUnwrap > 0.5)
                {
                    float2 p = v.uv * 2.0 - 1.0;
                    #if UNITY_UV_STARTS_AT_TOP
                    p.y = -p.y;
                    #endif
                    clip = float4(p, 0.5, 1.0);
                    eye = normalize(_FxView.xyz + float3(0.0, 0.0, 1e-5));
                }
                o.pos = clip;
                o.uv = v.uv;
                o.opos = v.vertex.xyz;
                o.onrm = v.normal;
                o.oview = eye;
                o.color = _FxUnwrap > 0.5 ? float4(1.0, 1.0, 1.0, 1.0) : v.color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                FxIn f;
                f.uv = fx_transform(i.uv, _FxXf, _FxTime);
                f.pos = i.opos;
                f.nrm = normalize(i.onrm + float3(0.0, 0.0, 1e-5));
                f.view = normalize(i.oview);
                f.time = _FxTime;
                f.p0 = _FxP0;
                f.p1 = _FxP1;
                f.c0 = _FxC0;
                f.c1 = _FxC1;
                f.c2 = _FxC2;
                f.c3 = _FxC3;
                float4 c = fx_dispatch(_Program, f) * i.color;
                c.rgb *= _FxXf.w;
                c.a = saturate(c.a * min(_FxXf.w, 1.0));
                return c;
            }
            ENDCG
        }
    }
}
