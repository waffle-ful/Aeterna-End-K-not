using System.Text;
using InnerNet;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

// ロビーパネルのルームコードを虹色に光らせるホスト画面ローカル演出。
// RPC も NetObject も使わないので非モッド client には何も届かない (見えるのは自分の画面だけ)。
//
// バニラの GameRoomNameCode 自身の text は素のコードのまま触らない。コピーボタン・Discord 表示など
// text を読む経路にリッチテキストのタグが混ざるのを避けるため、本体は透明にして複製 TMP を重ねる。
//
// 光は「わずかにずらした半透明の複製を円周状に敷く」ことで出す。このフォントのマテリアルでは TMP の
// 縁取り / underlay が無反応で、拡大で膨らませると文字どうしの間隔まで広がって二重像になるため、
// 位置だけをずらして重ねる。文字の並びは動かないまま輪郭の外へ光がにじむ。
//
// 毎フレームの文字列生成を避けるため、色相の位相を Steps 段に量子化してタグ済みの文字列を 1 回だけ焼き、
// 以降は添字で差し替えるだけにしてある。差し替えも 1 段ごと (= 20Hz) なので TMP のメッシュ再生成も間引かれる。
public static class LobbyCodeRainbow
{
    private const int Steps = 60;            // 1 周の段数 (= 更新頻度 Steps / CycleSeconds)
    private const float CycleSeconds = 3f;   // 虹が 1 周する時間
    private const float HuePerChar = 0.16f;  // 隣の文字との色相差 (6 文字でほぼ 1 周ぶん)
    private const float Saturation = 0.9f;

    // 光の層。内側のリングほど濃く近く、外側ほど薄く遠い。半径は文字の高さに対する比で持つ
    // (フォントサイズや解像度が変わっても見た目の比率が保たれる)。
    private static readonly int[] RingDirections = [8, 8, 8];
    private static readonly float[] RingRadius = [0.07f, 0.15f, 0.26f];
    private static readonly byte[] RingAlpha = [0x3E, 0x24, 0x14];
    private const float PulseAmount = 0.12f; // 半径の息づかい幅

    private static GameStartManager Owner;
    private static TextMeshPro Face;
    private static TextMeshPro[] Auras;
    private static int[] AuraRing;
    private static Vector2[] AuraDirection;
    private static string[] FaceFrames;
    private static string[][] RingFrames;
    private static Vector3 BaseLocalPos;
    private static float TextHeight;
    private static int FramesGameId;
    private static int LastStep = -1;

    private static bool Active => Owner && Face && Auras != null && FaceFrames != null;

    // GameStartManager はロビーごとに作り直される。破棄済みの native TMP を掴んだままだと
    // managed からは null に見えないので、ロビーが変わったら必ず参照ごと捨てる。
    public static void Reset()
    {
        // 参照を捨てる前に必ず消しておく。生き残った層をそのまま手放すと、以降どこからも
        // enabled を落とせなくなり、ストリーマーモードでもコードが出たままになる。
        if (Auras != null) SetLayersEnabled(false);

        Owner = null;
        Face = null;
        Auras = null;
        AuraRing = null;
        AuraDirection = null;
        FaceFrames = null;
        RingFrames = null;
        TextHeight = 0f;
        FramesGameId = 0;
        LastStep = -1;
    }

    // GameRoomNameCode に子 (HideName) が付く前に呼ぶこと。付いた後だと Instantiate が子ごと複製する。
    public static void Setup(GameStartManager gsm)
    {
        Reset();

        if (Main.RainbowLobbyCode == null || !Main.RainbowLobbyCode.Value) return;
        if (!gsm) return;

        TextMeshPro code = gsm.GameRoomNameCode;
        if (!code) return;

        BaseLocalPos = code.transform.localPosition;

        var total = 0;
        foreach (int dirs in RingDirections) total += dirs;

        var auras = new TextMeshPro[total];
        var rings = new int[total];
        var dirVectors = new Vector2[total];

        var n = 0;

        for (var ring = 0; ring < RingDirections.Length; ring++)
        {
            int dirs = RingDirections[ring];
            // リングごとに半ステップ回して、内外の光がきれいに互い違いに並ぶようにする
            float angleOffset = Mathf.PI / dirs * ring;

            for (var d = 0; d < dirs; d++)
            {
                float angle = (Mathf.PI * 2f * d / dirs) + angleOffset;
                // パネルの各パーツは z を少しずつずらして重なりを決めているので、光の層も本体より手前 (-z) に
                // 積む。奥に置くとパネル背景に飲まれて一切見えない。外側のリングほど奥に置く。
                auras[n] = CreateLayer(code, $"LobbyCodeAura{ring}_{d}", RingZ(ring));
                rings[n] = ring;
                dirVectors[n] = new(Mathf.Cos(angle), Mathf.Sin(angle));
                n++;
            }
        }

        Face = CreateLayer(code, "LobbyCodeFace", -0.01f * (RingDirections.Length + 1));
        Auras = auras;
        AuraRing = rings;
        AuraDirection = dirVectors;
        Owner = gsm;
    }

    private static float RingZ(int ring) => -0.01f * (RingDirections.Length - ring);

    private static TextMeshPro CreateLayer(TextMeshPro code, string name, float zOffset)
    {
        TextMeshPro layer = Object.Instantiate(code, code.transform.parent);
        layer.name = name;
        layer.transform.localPosition = BaseLocalPos + new Vector3(0f, 0f, zOffset);
        layer.transform.localScale = code.transform.localScale;
        layer.transform.localRotation = code.transform.localRotation;
        layer.color = Color.white;
        return layer;
    }

    // Update Prefix から毎フレーム呼ぶ。ストリーマーモード時は既存の本体 alpha 0 と同じく丸ごと隠す。
    public static void Apply(GameStartManager gsm, bool hidden)
    {
        if (Auras == null || !gsm) return;

        // Start Postfix が早期 return したロビーでは Setup が走らない。前ロビーの破棄済み TMP を
        // 掴んだまま触らないよう、持ち主が変わっていたらここで捨てる。
        if (Owner != gsm || !Owner || !Face)
        {
            Reset();
            return;
        }

        TextMeshPro code = gsm.GameRoomNameCode;
        if (!code) return;

        // 本体は素のコード文字列を保ったまま透明にする (読み取り経路を壊さずに見た目だけ差し替える)。
        // 直前の既存ブロックが毎フレーム alpha を戻すので、こちらも毎フレーム上書きしないとちらつく。
        code.color = new(1f, 1f, 1f, 0f);

        SetLayersEnabled(!hidden);
        if (hidden) return;

        // 焼き直しの判定は int の GameId で行う。文字列で比べると IntToGameName が毎フレーム
        // 新しい managed string を確保してしまう (このファイルの他の表示更新と同じ作法)。
        int gameId = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (gameId == 0) return;

        if (FaceFrames == null || FramesGameId != gameId)
        {
            string source = GameCode.IntToGameName(gameId);
            if (string.IsNullOrEmpty(source)) return;

            FaceFrames = BuildFrames(source, 0xFF);
            RingFrames = new string[RingDirections.Length][];
            for (var ring = 0; ring < RingDirections.Length; ring++) RingFrames[ring] = BuildFrames(source, RingAlpha[ring]);

            FramesGameId = gameId;
            LastStep = -1;

            // 光の広がりは文字の高さを基準にする。文字が変わった時だけ測り直す。
            Face.text = FaceFrames[0];
            Face.ForceMeshUpdate();
            TextHeight = Face.textBounds.size.y;
            if (TextHeight <= 0f) TextHeight = Face.fontSize * 0.1f;
        }

        if (!Active) return;

        var step = (int)(Time.time / CycleSeconds * Steps) % Steps;
        if (step < 0) step += Steps;
        if (step == LastStep) return;

        LastStep = step;

        var phase = step / (float)Steps;
        Face.text = FaceFrames[step];

        // 息づかい: 位相 1 周で 2 回、光だけがふくらむ
        float pulse = 1f + (PulseAmount * Mathf.Sin(phase * Mathf.PI * 4f));

        for (var i = 0; i < Auras.Length; i++)
        {
            TextMeshPro aura = Auras[i];
            if (!aura) continue;

            int ring = AuraRing[i];
            aura.text = RingFrames[ring][step];

            float radius = TextHeight * RingRadius[ring] * pulse;
            Vector2 dir = AuraDirection[i];
            aura.transform.localPosition = BaseLocalPos + new Vector3(dir.x * radius, dir.y * radius, RingZ(ring));
        }
    }

    private static void SetLayersEnabled(bool enabled)
    {
        if (Face && Face.enabled != enabled) Face.enabled = enabled;

        foreach (TextMeshPro aura in Auras)
        {
            if (aura && aura.enabled != enabled) aura.enabled = enabled;
        }
    }

    // 不透明度は <color=#RRGGBBAA> に焼き込む。コンポーネントの color の alpha は
    // 色タグを張った文字には掛からないので、層ごとの濃さはタグ側で決める。
    private static string[] BuildFrames(string source, byte alpha)
    {
        var frames = new string[Steps];
        var sb = new StringBuilder(source.Length * 26);
        string alphaHex = alpha.ToString("X2");

        for (var s = 0; s < Steps; s++)
        {
            sb.Clear();
            var phase = s / (float)Steps;

            foreach (char ch in source)
            {
                Color c = Color.HSVToRGB(Frac(phase), Saturation, 1f);
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(c)).Append(alphaHex).Append('>').Append(ch).Append("</color>");
                phase = Frac(phase + HuePerChar);
            }

            frames[s] = sb.ToString();
        }

        return frames;
    }

    private static float Frac(float v)
    {
        v -= Mathf.Floor(v);
        return v;
    }
}
