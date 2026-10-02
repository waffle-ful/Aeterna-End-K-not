using System;
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
// 光と文字は同じ色相なので、そのままでは文字の縁が光に溶けて読みにくい。光の輪と文字のあいだに
// 暗い縁取り (同じく位置だけずらした複製) を 1 層挟み、光の派手さは変えずに字形だけを浮かせる。
//
// 毎フレームの文字列生成を避けるため、色相の位相を Steps 段に量子化してタグ済みの文字列を 1 回だけ焼き、
// 以降は添字で差し替えるだけにしてある。差し替えも 1 段ごと (= 20Hz) なので TMP のメッシュ再生成も間引かれる。
// 焼いた文字列はゲーム側にも 1 回だけ複製して握っておく (`.text =` は渡すたびにゲーム側へ文字列を複製し、
// 25 層 × 20Hz ではロビーで最大のゴミの出どころになる)。
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

    // 縁取りの層。光より内側・文字の直下に置き、息づかいはさせない (字形の輪郭は動かない方が読みやすい)。
    private const int StrokeDirections = 8;
    private const float StrokeRadius = 0.035f;
    private const string StrokeColor = "0A0A14A0";
    private const int StrokeRing = -1;

    private static GameStartManager Owner;
    private static TextMeshPro Face;
    private static TextMeshPro[] Auras;
    private static int[] AuraRing;
    private static Vector2[] AuraDirection;
    private static Il2Direct.PinnedString[] FaceFrames;
    private static Il2Direct.PinnedString[][] RingFrames;
    private static Vector3 BaseLocalPos;
    private static float TextHeight;
    private static int FramesGameId;
    private static int LastStep = -1;
    private static int SettledStep = -1;  // 全層を描き終えた段 (-1 = 未完)
    private static int[] SlotStep;        // 層ごとに今出している段。光・縁取りの各層の後ろに本体を 1 枠
    private static int SlotCursor;
    private static int LayersShown = -1; // SetLayersEnabled を状態が変わった時だけ呼ぶための控え (-1 = 未適用)

    private static bool Active => Il2Direct.Alive(Owner) && Il2Direct.Alive(Face) && Auras != null && FaceFrames != null;

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
        FreeFrames();
        FaceFrames = null;
        RingFrames = null;
        TextHeight = 0f;
        FramesGameId = 0;
        LastStep = -1;
        SettledStep = -1;
        SlotStep = null;
        SlotCursor = 0;
        LayersShown = -1;
    }

    // GameRoomNameCode に子 (HideName) が付く前に呼ぶこと。付いた後だと Instantiate が子ごと複製する。
    public static void Setup(GameStartManager gsm)
    {
        Reset();

        // オン/オフはロビーの途中でも切り替えられるので、設定に関係なく層は常に用意しておき、表示は Apply 側で決める。
        // (後から作り直そうとすると、その時点では HideName が子に付いていて一緒に複製されてしまう)
        if (!gsm) return;

        TextMeshPro code = gsm.GameRoomNameCode;
        if (!code) return;

        BaseLocalPos = code.transform.localPosition;

        var total = StrokeDirections;
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

        // 縁取りは光の最内リングより手前・文字より奥。光の後ろに置くと半透明の光越しに濁って見える。
        // 光と同じ配列に入れておけば、Reset / 表示切替の後始末をそのまま共有できる。
        for (var d = 0; d < StrokeDirections; d++)
        {
            float angle = Mathf.PI * 2f * d / StrokeDirections;
            auras[n] = CreateLayer(code, $"LobbyCodeStroke_{d}", StrokeZ);
            rings[n] = StrokeRing;
            dirVectors[n] = new(Mathf.Cos(angle), Mathf.Sin(angle));
            n++;
        }

        Face = CreateLayer(code, "LobbyCodeFace", -0.01f * (RingDirections.Length + 2));
        Auras = auras;
        AuraRing = rings;
        AuraDirection = dirVectors;
        SlotStep = new int[total + 1];
        Array.Fill(SlotStep, -1);
        Owner = gsm;
    }

    private static void InvalidateSteps()
    {
        LastStep = -1;
        SettledStep = -1;
        if (SlotStep != null) Array.Fill(SlotStep, -1);
    }

    private static float RingZ(int ring) => -0.01f * (RingDirections.Length - ring);
    private static float StrokeZ => -0.01f * (RingDirections.Length + 1);

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
        // 生存確認は Il2Direct.Alive で行う (`if (obj)` / `!=` はゲーム側の比較を呼び、1 回ごとに箱が出る)
        if (Auras == null || !Il2Direct.Alive(gsm)) return;

        // Start Postfix が早期 return したロビーでは Setup が走らない。前ロビーの破棄済み TMP を
        // 掴んだまま触らないよう、持ち主が変わっていたらここで捨てる。
        if (Owner is null || Owner.Pointer != gsm.Pointer || !Il2Direct.Alive(Owner) || !Il2Direct.Alive(Face))
        {
            Reset();
            return;
        }

        TextMeshPro code = gsm.GameRoomNameCode;
        if (!Il2Direct.Alive(code)) return;

        // オフの間は層を消して return するだけで、バニラの白いコードに戻る (本体の色は呼び出し元が毎フレーム白に戻している)。
        bool show = !hidden && Main.RainbowLobbyCode != null && Main.RainbowLobbyCode.Value;
        int shown = show ? 1 : 0;

        if (LayersShown != shown)
        {
            SetLayersEnabled(show);
            LayersShown = shown;
            InvalidateSteps(); // 再表示した時に止まった色のまま出ないよう、次のフレームで必ず差し替える
        }

        if (!show) return;

        // 本体は素のコード文字列を保ったまま透明にする (読み取り経路を壊さずに見た目だけ差し替える)。
        // 直前の既存ブロックが毎フレーム alpha を戻すので、こちらも毎フレーム上書きしないとちらつく。
        code.color = FxMath.Rgba(1f, 1f, 1f, 0f);

        // 焼き直しの判定は int の GameId で行う。文字列で比べると IntToGameName が毎フレーム
        // 新しい managed string を確保してしまう (このファイルの他の表示更新と同じ作法)。
        AmongUsClient client = AmongUsClient.Instance;
        int gameId = Il2Direct.Alive(client) ? client.GameId : 0;
        if (gameId == 0) return;

        if (FaceFrames == null || FramesGameId != gameId)
        {
            string source = GameCode.IntToGameName(gameId);
            if (string.IsNullOrEmpty(source)) return;

            FreeFrames();
            // 組み終えてから差し替える (途中で失敗したら作りかけの分を手放してから投げ直す)
            Il2Direct.PinnedString[] face = null;
            var rings = new Il2Direct.PinnedString[RingDirections.Length][];

            try
            {
                face = BuildFrames(source, 0xFF);
                for (var ring = 0; ring < RingDirections.Length; ring++) rings[ring] = BuildFrames(source, RingAlpha[ring]);
            }
            catch
            {
                FreeAll(face);
                foreach (Il2Direct.PinnedString[] r in rings) FreeAll(r);
                throw;
            }

            FaceFrames = face;
            RingFrames = rings;

            FramesGameId = gameId;
            InvalidateSteps();

            // 光の広がりは文字の高さを基準にする。文字が変わった時だけ測り直す。
            Face.text = FaceFrames[0].Managed;
            Face.ForceMeshUpdate();
            TextHeight = Face.textBounds.size.y;
            if (TextHeight <= 0f) TextHeight = Face.fontSize * 0.1f;

            // 縁取りは色も位置も動かないので、文字が変わった時にだけ置き直す
            string strokeText = $"<color=#{StrokeColor}>{source}</color>";
            float strokeRadius = TextHeight * StrokeRadius;

            for (var i = 0; i < Auras.Length; i++)
            {
                if (AuraRing[i] != StrokeRing || !Il2Direct.Alive(Auras[i])) continue;

                Vector2 dir = AuraDirection[i];
                Auras[i].text = strokeText;
                Auras[i].transform.localPosition = BaseLocalPos + new Vector3(dir.x * strokeRadius, dir.y * strokeRadius, StrokeZ);
            }
        }

        if (!Active || SlotStep == null) return;

        var step = (int)(Time.time / CycleSeconds * Steps) % Steps;
        if (step < 0) step += Steps;
        if (step == SettledStep) return;

        // 表示を出し直した時・文字が変わった時は、古い色や古い文字が混ざらないよう全層を同じフレームで差し替える。
        bool flush = LastStep < 0;
        LastStep = step;

        // text の差し替えは描画直前の TMP メッシュ作り直しを呼ぶ。25 層を段が変わるフレームにまとめると
        // 3 フレームに 1 回だけ約 1ms 重くなりフレーム間隔が揺れるので、1 段の時間 (fps によって 1〜数フレーム)
        // に均等に振り分ける。層どうしの色のずれは最大で 1 段ぶん。
        int budget = flush ? int.MaxValue : SlotBudget();

        var phase = step / (float)Steps;

        // 息づかい: 位相 1 周で 2 回、光だけがふくらむ
        float pulse = 1f + (PulseAmount * FxMath.Sin(phase * Mathf.PI * 4f));

        int slots = SlotStep.Length; // 光と縁取りの各層 + 最後に本体
        var updated = false;

        for (var n = 0; n < slots && budget > 0; n++)
        {
            int i = SlotCursor;
            SlotCursor = (SlotCursor + 1) % slots;

            if (SlotStep[i] == step) continue;

            SlotStep[i] = step;

            if (i == Auras.Length)
            {
                Il2Direct.SetText(Face, FaceFrames[step]);
                budget--;
                updated = true;
                continue;
            }

            int ring = AuraRing[i];
            if (ring == StrokeRing) continue;

            TextMeshPro aura = Auras[i];
            if (!Il2Direct.Alive(aura)) continue;

            Il2Direct.SetText(aura, RingFrames[ring][step]);

            float radius = TextHeight * RingRadius[ring] * pulse;
            Vector2 dir = AuraDirection[i];
            // Vector3 の + はゲーム側の演算子を呼んで箱が出るので成分ごとに足す
            aura.transform.localPosition = FxMath.V3(BaseLocalPos.x + (dir.x * radius), BaseLocalPos.y + (dir.y * radius), BaseLocalPos.z + RingZ(ring));
            budget--;
            updated = true;
        }

        // 1 周回って差し替える層が無ければ、この段は描き終わり (次の段まで毎フレームの走査も省く)
        if (!updated) SettledStep = step;
    }

    // このフレームで差し替える層の数。1 段の時間に収まる最小の数を、実際のフレーム時間から求める。
    private static int SlotBudget()
    {
        const float stepSeconds = CycleSeconds / Steps;
        int layers = Auras.Length - StrokeDirections + 1; // 光の層 + 本体 (縁取りは段で変わらない)
        int budget = (int)Math.Ceiling(layers * Time.deltaTime / stepSeconds);
        return Math.Clamp(budget, 1, layers);
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
    private static Il2Direct.PinnedString[] BuildFrames(string source, byte alpha)
    {
        var frames = new Il2Direct.PinnedString[Steps];
        var built = 0;

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

            try { frames[s] = new Il2Direct.PinnedString(sb.ToString()); }
            catch
            {
                for (var i = 0; i < built; i++) frames[i].Free();
                throw;
            }

            built++;
        }

        return frames;
    }

    // 解放したら参照も捨てる (解放済みのハンドルを持ったまま残すと、次の解放で同じハンドルを二度渡す)
    private static void FreeFrames()
    {
        FreeAll(FaceFrames);
        FaceFrames = null;

        if (RingFrames != null)
            foreach (Il2Direct.PinnedString[] ring in RingFrames) FreeAll(ring);

        RingFrames = null;
    }

    private static void FreeAll(Il2Direct.PinnedString[] frames)
    {
        if (frames == null) return;
        foreach (Il2Direct.PinnedString f in frames) f.Free();
    }

    private static float Frac(float v)
    {
        v -= Mathf.Floor(v);
        return v;
    }
}
