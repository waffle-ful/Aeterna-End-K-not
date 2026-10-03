// 粒用: テクスチャ × 頂点色 (粒の色) を事前乗算で重ねる。出力 = 色 + 下地 × (1 - 覆い)。
// 覆い 0 = 純粋な加算 (光る粒)、1 = 普通の半透明 (煙・灰のように下地を暗くできる)。
// 加算だけだと明るい床で白く飛んで色が消えるので、光る粒も少しだけ覆う。
Shader "EndKnot/Particle"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Intensity ("Intensity", Float) = 1
        _Cover ("Cover", Float) = 0.35
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }

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

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Intensity, _Cover;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv) * i.color;
                float a = t.a;
                return fixed4(t.rgb * _Intensity * a, a * _Cover);
            }
            ENDCG
        }
    }
}
