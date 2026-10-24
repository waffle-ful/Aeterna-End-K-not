// Backrooms の床と壁を 1 枚の四角形で描く。セルの種類は _CellTex (1 セル 1 バイト・_Ring セルで巻き戻る)
// から引き、画素ごとに自分のセルと近傍のセルを見て、タイルを 1 枚ずつ重ねていた時と同じ順で塗り重ねる:
//   -10 床 (セルごとに反転・90° 回転) / -9 床のシミ・壁の根元の影 / -6 縦壁の側面の影 /
//   -5 横壁の面 / -4 縦壁の本体 / -3 縦壁の縁・横壁の上端の帯・縦壁の終端
// 寸法とハッシュは C# 側 (BackroomsLobby.ChunkMesh.cs) と同じ式。セル値 0 = 未読み込み (何も描かない)。
// ステンシルは書かない (バニラ影は ShadowQuad 側のステンシル比較だけで受ける場所が決まる)。
Shader "EndKnot/BackroomsChunk"
{
    Properties
    {
        _CellTex ("Cells", 2D) = "black" {}
        _FloorTex ("Floor", 2D) = "white" {}
        _WallTex ("Wall", 2D) = "white" {}
        _FloorB ("Floor bounds", Vector) = (-0.5, -0.5, 0.5, 0.5)
        _FloorUV ("Floor uv", Vector) = (0, 0, 1, 1)
        _WallB ("Wall bounds", Vector) = (-0.5, -0.5, 0.5, 0.5)
        _WallUV ("Wall uv", Vector) = (0, 0, 1, 1)
        _FloorCol ("Floor color", Color) = (1, 1, 1, 1)
        _DarkCol ("Wall dark", Color) = (0.04, 0.03, 0.01, 1)
        _EdgeCol ("Wall edge", Color) = (0.12, 0.09, 0.03, 1)
        _StainCol ("Stain", Color) = (0.16, 0.11, 0.06, 0.5)
        _ShadowA ("Shadow alpha", Float) = 0.45
        _TopBand ("Wall top band", Float) = 1
        _Ring ("Ring size", Float) = 512
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
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _CellTex, _FloorTex, _WallTex;
            float4 _FloorB, _FloorUV, _WallB, _WallUV;
            fixed4 _FloorCol, _DarkCol, _EdgeCol, _StainCol;
            float _ShadowA, _TopBand, _Ring;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float2 world : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }

            // 0 = 未読み込み, 1 = 床, 2 = 横壁, 3 = 縦壁
            int CellAt(int2 c)
            {
                float2 uv = (float2(c) + 0.5) / _Ring; // Repeat で負の座標も巻き戻る
                return (int)(tex2Dlod(_CellTex, float4(uv, 0, 0)).r * 255.0 + 0.5);
            }

            // 事前乗算で 1 枚重ねる
            void Over(inout float4 acc, float3 rgb, float a)
            {
                acc.rgb = rgb * a + acc.rgb * (1.0 - a);
                acc.a = a + acc.a * (1.0 - a);
            }

            float InRect(float2 p, float2 c, float2 halfSize)
            {
                float2 d = abs(p - c);
                return (d.x <= halfSize.x && d.y <= halfSize.y) ? 1.0 : 0.0;
            }

            // スプライトの局所座標 (bounds 基準) → テクスチャの uv
            float2 SpriteUV(float2 l, float4 b, float4 uvr)
            {
                float2 t = (l - b.xy) / (b.zw - b.xy);
                return lerp(uvr.xy, uvr.zw, t);
            }

            float4 SampleSprite(sampler2D tex, float2 l, float4 b, float4 uvr, float2 dx, float2 dy)
            {
                float2 k = (uvr.zw - uvr.xy) / (b.zw - b.xy);
                return tex2Dgrad(tex, SpriteUV(l, b, uvr), dx * k, dy * k);
            }

            void FloorVariation(int2 c, out bool fx, out bool fy, out bool rot)
            {
                uint h = ((uint)c.x * 73856093u) ^ ((uint)c.y * 19349663u);
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                fx = (h & 1u) != 0u;
                fy = (h & 2u) != 0u;
                rot = (h & 4u) != 0u;
            }

            // 床セル c のシミ (3 つの楕円) を p に重ねる
            void Stain(inout float4 acc, float2 p, int2 c)
            {
                uint h = ((uint)c.x * 374761393u) ^ ((uint)c.y * 668265263u);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                if ((h % 100u) >= 7u) return;

                bool fx, fy, rot;
                FloorVariation(c, fx, fy, rot);
                float rc = rot ? 0.0 : 1.0, rs = rot ? 1.0 : 0.0;

                float ox = ((((h >> 4) & 15u) / 15.0) - 0.5) * 0.4;
                float oy = ((((h >> 12) & 15u) / 15.0) - 0.5) * 0.4;

                [unroll]
                for (uint i = 0u; i < 3u; i++)
                {
                    uint s = h * (7919u + i * 31u);
                    s ^= s >> 13;
                    s *= 0x85ebca6bu;
                    s ^= s >> 11;

                    float sx = 0.30 + ((s & 255u) / 255.0) * 0.30;
                    float sy = 0.30 + (((s >> 8) & 255u) / 255.0) * 0.30;
                    float subOx = ((((s >> 16) & 15u) / 15.0) - 0.5) * 0.22;
                    float subOy = ((((s >> 20) & 15u) / 15.0) - 0.5) * 0.22;
                    float subRot = (((s >> 24) & 255u) / 255.0) * 6.28318530718;

                    float lx = ox + subOx, ly = oy + subOy;
                    float2 center = float2(c) + float2(lx * rc - ly * rs, lx * rs + ly * rc);
                    float c2 = cos(subRot), s2 = sin(subRot);
                    float rc2 = rc * c2 - rs * s2, rs2 = rs * c2 + rc * s2;
                    float2 q = p - center;
                    // 回転を戻してから楕円の大きさで割る
                    float2 l = float2(q.x * rc2 + q.y * rs2, -q.x * rs2 + q.y * rc2) / float2(sx, sy);
                    if (abs(l.x) > 0.5 || abs(l.y) > 0.5) continue;
                    float a = saturate(1.0 - length(l) * 2.0);
                    a *= a;
                    Over(acc, _StainCol.rgb, _StainCol.a * a);
                }
            }

            // 下端 bottom・高さ 0.22 の縦グラデ (上ほど濃い t²)
            void ContactShadow(inout float4 acc, float2 p, float2 center, float halfW)
            {
                float2 d = p - center;
                if (abs(d.x) > halfW || abs(d.y) > 0.11) return;
                float t = (d.y + 0.11) / 0.22;
                Over(acc, float3(0, 0, 0), _ShadowA * t * t);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.world;
                int2 c = (int2)floor(p + 0.5);
                // 微分は 2×2 画素でそろえて取るので、早く抜ける画素があっても先に済ませる
                float2 dpx = ddx(p), dpy = ddy(p);
                int self = CellAt(c);
                if (self == 0) return fixed4(0, 0, 0, 0);

                float2 f = p - float2(c);
                int south = CellAt(c + int2(0, -1));
                int north = CellAt(c + int2(0, 1));
                float4 acc = float4(0, 0, 0, 0);

                // -10 床 (横壁のセルは面が全体を覆うので描かない)
                if (self != 2)
                {
                    bool fx, fy, rot;
                    FloorVariation(c, fx, fy, rot);
                    // 回転を戻す (rot90 = +90°)
                    float2 l = rot ? float2(f.y, -f.x) : f;
                    if (fx) l.x = -l.x;
                    if (fy) l.y = -l.y;
                    float2 gdx = rot ? float2(dpx.y, -dpx.x) : dpx;
                    float2 gdy = rot ? float2(dpy.y, -dpy.x) : dpy;
                    float4 t = SampleSprite(_FloorTex, l, _FloorB, _FloorUV, gdx, gdy) * _FloorCol;
                    Over(acc, t.rgb, t.a);
                }

                // -9 シミ (床セルだけが持つ・最大 0.11 隣へはみ出す)
                if (self == 1) Stain(acc, p, c);
                if (f.x < -0.38 && CellAt(c + int2(-1, 0)) == 1) Stain(acc, p, c + int2(-1, 0));
                if (f.x > 0.38 && CellAt(c + int2(1, 0)) == 1) Stain(acc, p, c + int2(1, 0));
                if (f.y < -0.38 && south == 1) Stain(acc, p, c + int2(0, -1));
                if (f.y > 0.38 && north == 1) Stain(acc, p, c + int2(0, 1));

                // -9 北隣の壁の根元の影 (横壁は全幅・下が床の縦壁は終端の幅 0.5)
                if (north == 2) ContactShadow(acc, p, float2(c) + float2(0, 1 - 0.61), 0.5);
                else if (north == 3 && self == 1) ContactShadow(acc, p, float2(c) + float2(0, 1 - 0.61), 0.25);

                if (self == 3)
                {
                    bool cap = south == 1;
                    // -6 側面の影 (幅 0.18・壁側が濃い t²)
                    float ax = abs(f.x);
                    if (ax >= 0.2125 && ax <= 0.3925)
                    {
                        float t = 1.0 - (ax - 0.2125) / 0.18;
                        Over(acc, float3(0, 0, 0), _ShadowA * t * t);
                    }
                    // -4 本体
                    if (!cap && ax <= 0.2) Over(acc, _DarkCol.rgb, 1.0);
                    // -3 縁
                    if (ax >= 0.2 && ax <= 0.225) Over(acc, _EdgeCol.rgb, 1.0);
                    // -3 終端 (下が床): 横壁と同じ面を幅 0.4 で
                    if (cap && ax <= 0.2)
                    {
                        float2 l = float2(f.x / 0.4, f.y);
                        float4 t = SampleSprite(_WallTex, l, _WallB, _WallUV, dpx * float2(2.5, 1), dpy * float2(2.5, 1));
                        Over(acc, t.rgb, t.a);
                    }
                }
                else if (self == 2)
                {
                    // -9 自セルの根元の影は南隣の上端に落ちるので、ここでは面より先に描く物は無い
                    // -5 面
                    float4 t = SampleSprite(_WallTex, f, _WallB, _WallUV, dpx, dpy);
                    Over(acc, t.rgb, t.a);
                    bool connector = south == 3;
                    float ax = abs(f.x);
                    // -4 真下が縦壁: 縦壁の本体を南半分 (高さ 0.8) に延ばす
                    if (connector && f.y <= 0.3 && ax <= 0.2) Over(acc, _DarkCol.rgb, 1.0);
                    // -3 上端の帯 / 延ばした縁
                    if (_TopBand > 0.5 && f.y >= 0.3) Over(acc, _DarkCol.rgb, 1.0);
                    if (connector && f.y <= 0.3 && ax >= 0.2 && ax <= 0.225) Over(acc, _EdgeCol.rgb, 1.0);
                }

                return acc;
            }
            ENDCG
        }
    }
}
