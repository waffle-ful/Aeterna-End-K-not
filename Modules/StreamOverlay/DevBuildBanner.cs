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

    // OnGUI は 1 フレームに複数回呼ばれるので、表示文字列は組み立て済みのものを読むだけにする
    private string _note, _noteTemplate;

    // 例外が出たら以後描かない (毎フレーム呼ばれるのでログが洪水になる)
    private bool _faulted;

    private static float Scale => Screen.width / 1080f * 0.5f;

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
    // (Translator.GetString はキャッシュ済み文字列を返すので参照比較で足りる)
    private void EnsureNote()
    {
        string template = Translator.GetString("DevBuildBannerNote");
        if (ReferenceEquals(template, _noteTemplate) && _note != null) return;

        _noteTemplate = template;
        _note = string.Format(template, Main.PluginVersion);
    }

    private float TitleHeight => 78f * Scale;
    private float NoteHeight => 22f * Scale;

    private void Draw()
    {
        // 箱を持たないので画面幅いっぱいのラベルを中央寄せで置く
        var titleRect = new Rect(0f, 10f * Scale, Screen.width, TitleHeight);
        var noteRect = new Rect(0f, titleRect.y + TitleHeight, Screen.width, NoteHeight);

        DrawOutlined(titleRect, Translator.GetString("DevBuildBannerTitle"), _titleStyle, new Color(1f, 0.16f, 0.16f));
        DrawOutlined(noteRect, _note, _noteStyle, new Color(1f, 0.35f, 0.35f, 0.9f));
    }

    // 明るいメニュー背景でも赤が沈まないよう、暗い縁取りを 4 方向に敷いてから本体を描く。
    // GUI.color で色を変調する (style 側の textColor は白のまま)。
    private static void DrawOutlined(Rect rect, string text, GUIStyle style, Color color)
    {
        float off = Mathf.Max(1f, rect.height * 0.06f);
        Color old = GUI.color;

        GUI.color = new Color(0f, 0f, 0f, 0.65f * color.a);
        GUI.Label(new Rect(rect.x - off, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x + off, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y - off, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y + off, rect.width, rect.height), text, style);

        GUI.color = color;
        GUI.Label(rect, text, style);

        GUI.color = old;
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
