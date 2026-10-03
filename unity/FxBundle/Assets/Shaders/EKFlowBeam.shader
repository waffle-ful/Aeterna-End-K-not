// 流れる光の帯 (半分は色で塗り、半分は光として足す)。UV の x が長さ方向 (0 = 根元)、y が幅方向。根元から先へ向かってノイズの筋が流れ、両脇と先端は滑らかに消える。
// 下向きに流したい時はスプライトを 180 度回して根元を上に置く。色と濃さは頂点色 (SpriteRenderer.color) で決める。
Shader "EndKnot/FlowBeam"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Noise ("Noise", 2D) = "gray" {}
        _Intensity ("Intensity", Float) = 1
        _Cover ("Cover", Float) = 0.7
        _Flow ("Flow speed", Float) = 1.2
        _Streak ("Streak length", Float) = 0.35
        _Sharp ("Edge sharpness", Float) = 1.6
        _Base ("Base opacity", Float) = 0.35
        _Gain ("Noise gain", Float) = 1.5
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

        // 事前乗算: 出力 = 色 + 下地 × (1 - 覆い)。覆い 0 = 純粋な加算、1 = 普通の半透明。
        // 加算だけだと明るい床で白く飛んで色が消えるので、一部は色で下地を塗り替える
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Noise;
            float _Intensity, _Cover, _Flow, _Streak, _Sharp, _Base, _Gain;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; float2 world : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float along = i.uv.x;
                float across = 1 - abs(i.uv.y * 2 - 1);
                float t = _Time.y;
                float seed = dot(floor(i.world * 0.5), float2(0.37, 0.61));

                // 幅方向に細かく・長さ方向に引き伸ばしたノイズを先へ流すと筋になる
                float n1 = tex2D(_Noise, float2(i.uv.y * 1.3 + seed, along * _Streak - t * _Flow)).r;
                float n2 = tex2D(_Noise, float2(i.uv.y * 2.7 - seed, along * _Streak * 1.9 - t * _Flow * 1.6)).r;
                float n = n1 * 0.55 + n2 * 0.45;

                float side = pow(saturate(across), _Sharp);
                float core = pow(saturate(across), 10);
                float ends = smoothstep(0, 0.06, along) * smoothstep(1, 0.45, along);

                float a = (side * (_Base + _Gain * n * n) + core * 0.7) * ends;
                float3 c = i.color.rgb * (1 + core * 1.2);
                a = saturate(a * i.color.a);
                return fixed4(c * _Intensity * a, a * _Cover);
            }
            ENDCG
        }
    }
}
