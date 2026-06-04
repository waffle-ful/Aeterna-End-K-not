using UnityEngine;

namespace EndKnot
{
    // ============================================================
    // RiptideWaveCNO
    //   ・マップ全体をカバーする波の視覚 CNO。
    //   ・<size=N> 絶対モード + <mark=#xxxxxx>WWWWWWWW</mark> 構造で
    //     1 スプライト ≈ 200 byte 以内を維持し、AU 2026 の 1KB packet 閾値に余裕で収まる。
    //   ・4 方向それぞれ異なるグラデーション色を使用。
    //
    // FontSizeAbsolute の暫定値 1200 について:
    //   ForceField の較正式 visual ≈ 0.05 × font^0.9 を逆算すると
    //   35 unit (半マップ) を覆うには font ≈ 1450 が必要。
    //   ただし文字数や行数が異なるため曲線が変わる可能性がある。
    //   TODO: ユーザーが /sizetest で実機 calibration 後に確定値に差し替えること。
    //         目安: /sizetest 800, 1200, 1600, 2000 で 8×8 スプライトの world 幅を計測し
    //              log-log fit して FontSizeAbsolute を確定する。
    // ============================================================
    internal sealed class RiptideWaveCNO : CustomNetObject
    {
        // 較正 (2026-06-04 公式 anti-cheat 対策で 6→2 sub-CNO 構成に改定):
        //   size=40 absolute × 8 col W × 2 sub-CNO レイアウト → マップ全体カバー
        //   1 CNO ≈ 53u × 53u, perp ±10f オフセットで 2 sub-CNO を並べて連続した波として見せる
        private const int FontSizeAbsolute = 40;

        // 方向別スプライト — 全て 8 col × 8 row、グラデーションで進行方向を表現
        // 前縁 (進行方向側) = 濃い青 #0095ff、後縁 = 薄い青 #b8e1ff
        // </alpha> 閉じタグ禁止 (TMP タグ罠 #10)
        private static readonly string[] Sprites =
        {
            // Index 0 : 左→右 (wave front = 右端 col、後尾 = 左端)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark>\n" +
            $"<mark=#b8e1ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#0095ff>WW</mark></size>",

            // Index 1 : 右→左 (wave front = 左端 col、後尾 = 右端)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark>\n" +
            $"<mark=#0095ff>WW</mark><mark=#5db8ff>WWWW</mark><mark=#b8e1ff>WW</mark></size>",

            // Index 2 : 上→下 (wave front = 下端 row、後尾 = 上端)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"<mark=#b8e1ff>WWWWWWWW</mark>\n" +
            $"<mark=#b8e1ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#0095ff>WWWWWWWW</mark>\n" +
            $"<mark=#0095ff>WWWWWWWW</mark></size>",

            // Index 3 : 下→上 (wave front = 上端 row、後尾 = 下端)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"<mark=#0095ff>WWWWWWWW</mark>\n" +
            $"<mark=#0095ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#5db8ff>WWWWWWWW</mark>\n" +
            $"<mark=#b8e1ff>WWWWWWWW</mark>\n" +
            $"<mark=#b8e1ff>WWWWWWWW</mark></size>",
        };

        public static string GetSprite(int directionIndex)
        {
            if (directionIndex < 0 || directionIndex >= Sprites.Length) return Sprites[0];
            return Sprites[directionIndex];
        }

        public RiptideWaveCNO(Vector2 position, int directionIndex)
        {
            CreateNetObject(GetSprite(directionIndex), position);
            // 即 TP で SnapToSendFrameCount を立てて spawn 位置を確実に sync。
            // CreateNetObject 直後の disconnected player は default 位置 (host transform 付近)
            // に描画されてしまうため、これがないと spawn 瞬間に sprite が host 位置に飛ぶ。
            TP(position);
        }

        public override void OnMeeting()
        {
            Despawn();
        }
    }

    // ============================================================
    // RiptidePredictiveGhostCNO
    //   ・ShowPredictiveGhost option ON 時のみ spawn される半透明予告 CNO。
    //   ・波の前方に配置し、進行先を予告する。
    //   ・<color=#xxxxxx80> で 50% 半透明化 — </alpha> 閉じタグは使わない (TMP タグ罠 #10)
    // ============================================================
    internal sealed class RiptidePredictiveGhostCNO : CustomNetObject
    {
        private const int FontSizeAbsolute = 40;

        // 半透明 (AA=80 ≈ 50% alpha) の予告スプライト。本体 CNO と同 8×8 形状。
        // </alpha> 閉じタグ禁止 (TMP タグ罠 #10)、<color=#xxxxxx80> ベースで実装
        private const string GhostRow = "<color=#b8e1ff80>WWWWWWWW</color>";

        private static readonly string[] GhostSprites =
        {
            // Index 0 : 左→右 (8×8、全体半透明)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}</size>",

            // Index 1 : 右→左 (同上)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}</size>",

            // Index 2 : 上→下 (同上)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}</size>",

            // Index 3 : 下→上 (同上)
            $"<size={FontSizeAbsolute}><line-height=97%>" +
            $"{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}\n{GhostRow}</size>",
        };

        public static string GetGhostSprite(int directionIndex)
        {
            if (directionIndex < 0 || directionIndex >= GhostSprites.Length) return GhostSprites[0];
            return GhostSprites[directionIndex];
        }

        public RiptidePredictiveGhostCNO(Vector2 position, int directionIndex)
        {
            CreateNetObject(GetGhostSprite(directionIndex), position);
            TP(position);  // spawn 位置を即 sync (本体 CNO と同様の理由)
        }

        public override void OnMeeting()
        {
            Despawn();
        }
    }
}
