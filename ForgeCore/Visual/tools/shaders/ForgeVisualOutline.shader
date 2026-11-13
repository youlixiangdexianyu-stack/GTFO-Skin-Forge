// ForgeVisual's outline (built-in pipeline) round enemies and objects: only the outer edge of each target's whole silhouette is
// drawn, never the edges inside it (head, jaw, limbs crossing the body).
// Pass 0 (Fill) runs from a camera command buffer after the transparent pass, into a screen-sized mask that shares the scene's
// depth: the front faces of the target mesh, depth tested against the scene (pulled a hair towards the camera so the target's own
// depth never hides them), so the mask holds exactly the target's visible area and walls cut it.
// Pass 2 (FillXRay) is the same fill without the depth test: the whole silhouette goes into the mask, so its line shows through walls.
// Pass 3 (Silhouette) runs before each Fill: the whole silhouette, marked "inside" with no line strength. A pixel of the body that the
// depth test loses (the game's flesh shader moves its surface, so its own depth beats the plain mesh here and there) stays inside, so a hole in
// the visible area cannot draw a line round itself, and a line never runs along the edge of what covers the target: it follows the
// outer edge of the body and stops where the body goes behind something.
// What the game clips away from the body itself (a shot-off head or limb: up to four ellipsoids in the renderer's property block) is
// dropped too, so the line follows the body that is really drawn. The game tests each pixel's rest-pose mesh position, which every enemy
// mesh stores in its vertex colour (Cell/Enemy/EnemyFlesh_CD and Hidden/DepthOutput read COLOR.xyz, not the skinned or world position),
// so the fill reads the same colour. Each pixel keeps the colour and, in alpha, 1 plus the line's strength (1 marks "inside").
// Pass 1 (Composite) lays the line over the frame after the camera's post-processing and before the game adds its HUD (UI_Apply): a pixel
// outside every target takes the strongest target within the line's width (a ring of samples at the full and half width) and draws its
// colour; a pixel inside draws nothing. The game's exposure and tonemapping brighten its dark scenes many times over; drawn into the
// scene, every colour came out white. Laid over the post-processed frame, the colours are shown exactly as given, and the HUD stays on top.
// Colour and parameters are globals.
Shader "Hidden/ForgeVisual/Outline"
{
    Properties
    {
        _MainTex ("Mask", 2D) = "black" {}
    }
    CGINCLUDE
    #include "UnityCG.cginc"

    float4 _ForgeOutlineColor;
    // x: width in pixels, y: range in metres, z: fade distance in metres.
    float4 _ForgeOutlineParams;
    // The game's body clipping, from the enemy's property block (count 0 when it has none): a rest-pose mesh position (the vertex
    // colour) that _EllipsoidMtx[k] maps inside half the unit sphere is cut (inverted: outside it is cut).
    float4x4 _EllipsoidMtx[4];
    float4 _EllipsoidInverts;
    float _EllipsoidCount;

    struct appdata
    {
        float4 vertex : POSITION;
        float4 color : COLOR;
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float fade : TEXCOORD0;
        float3 rest : TEXCOORD1;
    };

    v2f vert_fill(appdata v)
    {
        v2f o;
        // Half a percent of the distance towards the camera: the target's own surface in the depth buffer never hides it.
        float3 view = UnityObjectToViewPos(v.vertex.xyz) * 0.995;
        o.pos = mul(UNITY_MATRIX_P, float4(view, 1.0));
        float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
        o.rest = v.color.xyz;
        float distanceToCamera = distance(world, _WorldSpaceCameraPos);
        o.fade = saturate((_ForgeOutlineParams.y - distanceToCamera) / max(_ForgeOutlineParams.z, 0.001));
        // A limb reaching into the camera is not outlined: in a dark room only its rim showed, a red curve floating at the
        // screen edge. Gone within 0.6 m, whole from 1.5 m.
        o.fade *= saturate((distanceToCamera - 0.6) / 0.9);
        return o;
    }

    void clip_cuts(v2f i)
    {
        [unroll]
        for (int k = 0; k < 4; k++)
        {
            if (k + 0.5 < _EllipsoidCount)
            {
                float d = min(length(mul(_EllipsoidMtx[k], float4(i.rest, 1.0)).xyz), 1.0);
                clip(0.5 - (_EllipsoidInverts[k] != 0.0 ? d : 1.0 - d));
            }
        }
    }

    float4 frag_fill(v2f i) : SV_Target
    {
        clip_cuts(i);
        return float4(_ForgeOutlineColor.rgb, 1.0 + _ForgeOutlineColor.a * i.fade);
    }

    float4 frag_silhouette(v2f i) : SV_Target
    {
        clip_cuts(i);
        return float4(0.0, 0.0, 0.0, 1.0);
    }
    ENDCG
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Fill"
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert_fill
            #pragma fragment frag_fill
            #pragma target 3.0
            // Unity 2019.4.21's shader compiler crashes in its HLSL optimiser on Zen 5 CPUs; unoptimised is fast enough here.
            #pragma enable_d3d11_debug_symbols
            ENDCG
        }
        Pass
        {
            Name "Composite"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            float4 frag(v2f_img i) : SV_Target
            {
                if (tex2D(_MainTex, i.uv).a > 0.5) return float4(0, 0, 0, 0);
                float2 reach = _MainTex_TexelSize.xy * max(_ForgeOutlineParams.x, 1.0);
                float4 best = float4(0, 0, 0, 0);
                [unroll]
                for (int k = 0; k < 12; k++)
                {
                    float angle = k * 0.5235988;
                    float2 dir = float2(cos(angle), sin(angle)) * reach;
                    float4 far = tex2D(_MainTex, i.uv + dir);
                    float4 near = tex2D(_MainTex, i.uv + dir * 0.5);
                    if (far.a > best.a) best = far;
                    if (near.a > best.a) best = near;
                }
                float strength = saturate(best.a - 1.0);
                return float4(best.rgb * strength, strength);
            }
            ENDCG
        }
        Pass
        {
            Name "FillXRay"
            Cull Back
            ZWrite Off
            ZTest Always
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert_fill
            #pragma fragment frag_fill
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols
            ENDCG
        }
        Pass
        {
            Name "Silhouette"
            Cull Off
            ZWrite Off
            ZTest Always
            // Never lowers what an earlier target wrote: where a target stands in front of another, the front one's line stays.
            BlendOp Max
            Blend One One

            CGPROGRAM
            #pragma vertex vert_fill
            #pragma fragment frag_silhouette
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols
            ENDCG
        }
    }
    Fallback Off
}
