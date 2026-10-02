using System;
using UnityEngine;

namespace EndKnot.Modules.StreamOverlay;

// 開発者ビルド (PluginVersion が "-dev") のときだけ画面上部に出す告知バナー。
// 配信を見ている人が「配布版では参加できない回だ」と一目で分かるようにするのが目的なので、
// ゲーム中 (役職 HUD の邪魔になる) 以外 = メインメニュー・ロビー・リザルト画面で出す。
// 見た目は箱なしの素の赤文字 (TOH/TOHE 系の ErrorLevel3 表示に倣った文言)。
// 100% host 画面ローカル (RPC / NetObject 不使用)。
public class DevBuildBanner : MonoBehaviour
{
    private GUIStyle _titleStyle, _noteStyle;
    private float _builtScale = -1f;

    // OnGUI は 1 フレームに複数回呼ばれるので、表示文字列は組み立て済みのものを読むだけにする。
    // GUI.Label に string を渡すと呼ぶたびにゲーム側へ文字列が複製されるので、GUIContent に 1 回だけ包んで使い回す。
    private string _note, _noteTemplate, _title;
    private GUIContent _titleContent, _noteContent;

    // 例外が出たら以後描かない (毎フレーム呼ばれるのでログが洪水になる)
    private bool _faulted;

    private static float Scale => Il2Direct.ScreenWidth / 1080f * 0.5f;

    private void Awake()
    {
        ImguiNoLayout.Apply(this);
    }

    private bool ShouldShow()
    {
        if (_faulted || !Main.IsDevBuild) return false;
        // 起動直後は Translator.Init() より前に OnGUI が回る。翻訳テーブルが揃うまでは描かない
        // (ここで GetString を引くと NRE で _faulted が立ち、以後そのセッションは二度と出なくなる)。
        if (!Translator.IsInitialized) return false;
        // ゲーム中は役職 HUD と重なるので出さない (見せたいのはロビー画面)
        return !GameStates.InGame;
    }

    private void OnGUI()
    {
        try
        {
            if (!ShouldShow()) return;

            EnsureStyles();
            EnsureNote();
            Draw();
        }
        catch (Exception e)
        {
            _faulted = true;
            Utils.ThrowException(e);
        }
    }

    // 言語切替でも追従できるよう、翻訳済みテンプレの実体が変わったときだけ組み直す
    // (Translator.GetString は呼ぶたびに別の文字列を返すことがあるので中身で比べる)
    private void EnsureNote()
    {
        string title = Translator.GetString("DevBuildBannerTitle");

        if (!string.Equals(title, _title, StringComparison.Ordinal) || _titleContent == null)
        {
            _title = title;
            _titleContent = new GUIContent(title);
        }

        string template = Translator.GetString("DevBuildBannerNote");
        if (string.Equals(template, _noteTemplate, StringComparison.Ordinal) && _noteContent != null) return;

        _noteTemplate = template;
        _note = string.Format(template, Main.PluginVersion);
        _noteContent = new GUIContent(_note);
    }

    private void Draw()
    {
        // 箱を持たないので画面幅いっぱいのラベルを中央寄せで置く。
        // 座標は手元の float で持つ (Rect の x/y/width/height を読むとゲーム側の読み取りを呼び、1 回ごとに箱が出る)
        float scale = Scale;
        float width = Il2Direct.ScreenWidth;
        float titleY = 10f * scale;
        float titleH = 78f * scale;

        DrawOutlined(0f, titleY, width, titleH, _titleContent, _titleStyle, FxMath.Rgba(1f, 0.16f, 0.16f));
        DrawOutlined(0f, titleY + titleH, width, 22f * scale, _noteContent, _noteStyle, FxMath.Rgba(1f, 0.35f, 0.35f, 0.9f));
    }

    // 明るいメニュー背景でも赤が沈まないよう、暗い縁取りを 4 方向に敷いてから本体を描く。
    // GUI.color で色を変調する (style 側の textColor は白のまま)。描き終えたら白へ戻す (このバナーの前後で色を変える描画は無い)。
    private static void DrawOutlined(float x, float y, float w, float h, GUIContent content, GUIStyle style, Color color)
    {
        float off = FxMath.Max(1f, h * 0.06f);

        GUI.color = FxMath.Rgba(0f, 0f, 0f, 0.65f * color.a);
        GUI.Label(new Rect(x - off, y, w, h), content, style);
        GUI.Label(new Rect(x + off, y, w, h), content, style);
        GUI.Label(new Rect(x, y - off, w, h), content, style);
        GUI.Label(new Rect(x, y + off, w, h), content, style);

        GUI.color = color;
        GUI.Label(new Rect(x, y, w, h), content, style);

        GUI.color = FxMath.Rgba(1f, 1f, 1f);
    }

    private void EnsureStyles()
    {
        if (_titleStyle != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;

        _titleStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Max(33, Mathf.RoundToInt(57f * Scale)),
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _titleStyle.normal.textColor = Color.white;

        _noteStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Max(10, Mathf.RoundToInt(15f * Scale)),
            fontStyle = FontStyle.Normal,
            richText = false
        };
        _noteStyle.normal.textColor = Color.white;
    }
}
