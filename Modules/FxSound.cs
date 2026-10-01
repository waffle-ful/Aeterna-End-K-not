using System.Collections.Generic;
using AmongUs.Data;
using UnityEngine;

namespace EndKnot.Modules;

// 演出に付ける効果音。鳴らすのは演出を描くクライアント自身で、送信は無い (演出が見えない人には音も届かない)。
// 聞こえ方は聞き手と音源の位置関係で変える:
//   距離      … NearDist までは元の音量、そこから FarDist へ向けて絞り、FarDist より遠ければ鳴らさない
//   左右      … 音源が画面の右にあれば右から聞こえる
//   壁越し    … 聞き手との間に壁がある・聞き手がベントの中 → こもった音 (<名前>_m)。幽霊は壁を抜けるのでこもらない
//   広い部屋  … 音源が食堂・ホール・貨物室のような広い部屋の中 → 響きの付いた音 (<名前>_r)。廊下・狭い部屋・屋外は響かない
// こもり・響きは素材の側に焼いてあるので、実行時にフィルタは掛けない。
public static class FxSound
{
    private const float NearDist = 2.5f;
    private const float FarDist = 9f;
    private const float MinGap = 0.08f;
    private const int Voices = 6;

    private static readonly Dictionary<string, float> LastPlayed = [];

    // 壁の本数で広さを測ると、家具の多い広い部屋と狭い部屋が同じ値になる (エアシップの実測で 貨物室 9/16・警備室 11/16)。
    // 部屋の種類で決めれば、同じ部屋の中を歩いても聞こえ方が変わらない
    private static readonly HashSet<SystemTypes> Halls =
    [
        SystemTypes.Cafeteria, SystemTypes.Storage, SystemTypes.MainHall, SystemTypes.CargoBay, SystemTypes.Engine,
        SystemTypes.MeetingRoom, SystemTypes.GapRoom, SystemTypes.Launchpad, SystemTypes.Laboratory, SystemTypes.Specimens,
        SystemTypes.Reactor, SystemTypes.Greenhouse
    ];

    // SoundManager の AudioSource は他の音にも使い回されるので、音程や左右の定位を書くと後から鳴る別の音に残る。
    // 自前の AudioSource を数本持ち、順番に使う
    private static GameObject _root;
    private static readonly AudioSource[] Pool = new AudioSource[Voices];
    private static int _next;

    // AudioSource の pitch / panStereo が実行時に使えない環境では、一度失敗したら以後触らない
    private static bool _shapingBroken;

    // everywhere = 画面全体に掛かる演出の音。距離で絞らず、左右にも振らない
    public static void At(string name, Vector2 pos, float volume = 1f, bool everywhere = false)
    {
        try
        {
            // 会議や追放の画面では演出の絵がすぐ消されるので、音だけ残さない
            if (GameStates.IsMeeting || ExileController.Instance) return;

            float now = Time.unscaledTime;
            if (LastPlayed.TryGetValue(name, out float last) && now - last < MinGap) return;

            PlayerControl lp = PlayerControl.LocalPlayer;
            if (!lp) return;

            Vector2 ear = lp.Pos();
            float dx = pos.x - ear.x, dy = pos.y - ear.y;
            float dist = FxMath.Sqrt(dx * dx + dy * dy);

            float gain = 1f;
            float pan = 0f;

            if (!everywhere)
            {
                if (dist >= FarDist) return;

                if (dist > NearDist)
                {
                    float k = 1f - (dist - NearDist) / (FarDist - NearDist);
                    gain = k * FxMath.Sqrt(k);
                }

                pan = FxMath.Clamp(dx / 7f, -0.7f, 0.7f);
            }

            string suffix = "";

            // すぐそば (1u 以内) は壁を挟んでいても直接聞こえる扱いにする。短い区間の壁判定は当てにならない
            if (lp.inVent || (!everywhere && lp.IsAlive() && dist > 1f && Blocked(lp, ear, pos)))
            {
                suffix = "_m";
                gain *= 0.8f;
            }
            else if (InHall(everywhere ? ear : pos))
                suffix = "_r";

            LastPlayed[name] = now;

            AudioClip clip = CustomSoundsManager.GetClip(name + suffix);
            if (!clip) return;

            AudioSource src = NextVoice();
            if (!src) return;

            float sfx = DataManager.Settings?.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;
            src.clip = clip;
            src.volume = sfx * volume * gain;

            if (!_shapingBroken)
            {
                try
                {
                    src.pitch = FxMath.Range(0.97f, 1.03f);
                    src.panStereo = pan;
                }
                catch (System.Exception e)
                {
                    _shapingBroken = true;
                    Logger.Warn($"pitch / pan unavailable: {e.Message}", "FxSound");
                }
            }

            src.Play();
            Logger.Info($"{name}{suffix} dist={dist:F1} gain={gain:F2} pan={pan:F2}", "FxSound");
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    private static AudioSource NextVoice()
    {
        if (!_root)
        {
            _root = new GameObject("FxSound");
            Object.DontDestroyOnLoad(_root);

            for (int i = 0; i < Voices; i++)
            {
                AudioSource s = _root.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                s.loop = false;
                Pool[i] = s;
            }
        }

        _next = (_next + 1) % Voices;
        return Pool[_next];
    }

    // 位置は体の中心だが、船の壁は足元の高さに敷かれている。そのまま結ぶと壁の上下の端をかすめて見逃すので、両端を足元へ下ろす
    private static bool Blocked(PlayerControl lp, Vector2 a, Vector2 b)
    {
        Vector2 off = lp.WallRayOffset();
        return PhysicsHelpers.AnythingBetween(FxMath.V2(a.x + off.x, a.y + off.y), FxMath.V2(b.x + off.x, b.y + off.y), Constants.ShipOnlyMask, false);
    }

    private static bool InHall(Vector2 p)
    {
        if (!ShipStatus.Instance) return false;

        PlainShipRoom room = p.GetPlainShipRoom();
        return room && Halls.Contains(room.RoomId);
    }
}
