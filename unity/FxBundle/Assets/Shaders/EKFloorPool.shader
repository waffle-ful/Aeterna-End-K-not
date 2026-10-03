// 床に落ちた光だまり (半分は色で塗り、半分は光として足す)。縁の線は引かず、中心から外へ滑らかに消え、ノイズが渦を巻きながら外へ流れて揺らめく。
// 形はスプライトの UV (0..1 の正方形) から計算するので、絵は何でもよい (白い板で足りる)。色と濃さは頂点色 (SpriteRenderer.color) で決める。
// ノイズは世界座標でずらすので、同じマテリアルをまとめて描いても場所ごとに模様が変わる。
Shader "EndKnot/FloorPool"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Noise ("Noise", 2D) = "gray" {}
        _Intensity ("Intensity", Float) = 1
        _Cover ("Cover", Float) = 0.9
        _Swirl ("Swirl speed", Float) = 0.08
        _Flow ("Outward flow speed", Float) = 0.35
        _Core ("Core brightness", Float) = 0.3
        _Falloff ("Edge falloff", Float) = 0.8
        _Base ("Base opacity", Float) = 0.7
        _Gain ("Noise gain", Float) = 1.4
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
            float _Intensity, _Cover, _Swirl, _Flow, _Core, _Falloff, _Base, _Gain;

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
                float2 p = i.uv * 2 - 1;
                float r = length(p);
                if (r >= 1) return 0;

                float t = _Time.y;
                float seed = dot(floor(i.world * 0.5), float2(0.37, 0.61));
                float ang = atan2(p.y, p.x) * 0.15915494 + 0.5;

                // 角度方向は整数倍で一周させてテクスチャの継ぎ目を消す
                float n1 = tex2D(_Noise, float2(ang * 3 + t * _Swirl + seed, r * 0.9 - t * _Flow)).r;
                float n2 = tex2D(_Noise, float2(ang * 5 - t * _Swirl * 1.7, r * 1.6 - t * _Flow * 1.4 + seed)).r;
                float n = n1 * 0.6 + n2 * 0.4;

                float body = pow(smoothstep(1, 0, r), _Falloff);
                float core = pow(saturate(1 - r), 4);

                float a = body * (_Base + _Gain * n * n) + core * 0.5;
                float3 c = i.color.rgb * (1 + core * _Core);
                a = saturate(a * i.color.a);
                return fixed4(c * _Intensity * a, a * _Cover);
            }
            ENDCG
        }
    }
}
