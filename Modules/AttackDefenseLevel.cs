using System.Collections.Generic;
using EndKnot.Roles;

namespace EndKnot;

/// <summary>
///     攻撃の種別。同じ役職でも経路ごとに防御が違うケースがあるため、
///     攻撃力・防御力の問い合わせに常に添える。
/// </summary>
public enum AttackKind
{
    /// <summary>キルボタン経由の通常キル。</summary>
    Murder,

    /// <summary>爆弾・毒・呪い・時限死 (Suicide 経路)。</summary>
    Indirect,

    /// <summary>処刑系 (RpcExileV2 で直接殺す型)。</summary>
    Execution,

    /// <summary>反撃キル (ベテラン等が殺し返す)。</summary>
    Retaliation,

    /// <summary>推理 (Guess)。</summary>
    Guess,

    /// <summary>投票追放。キルとは別の関所 (OnVotedOut 鎖) で扱う。</summary>
    Exile
}

/// <summary>
///     攻撃レベル / 防御レベルの梯子。判定式は「攻撃 &gt; 防御なら貫く」。
///     レベルは役職が宣言し、判定はこのクラスに集約する。
/// </summary>
public static class AttackDefense
{
    /// <summary>なし。キル能力を持たない / 素のクルー。</summary>
    public const int None = 0;

    /// <summary>基本。通常キル / 1回まもり系・確率・消費型の防御。</summary>
    public const int Basic = 1;

    /// <summary>強力。処刑系の攻撃 / 反撃・身代わり・他役職由来の保護。</summary>
    public const int Powerful = 2;

    /// <summary>抗えない / 無敵。Pestilence 級。</summary>
    public const int Unstoppable = 3;

    /// <summary>
    ///     攻撃が防御を貫くか。
    ///     <paramref name="defense" /> が null の間は【従来互換】= 実質無限の防御力として扱い、何も貫かない。
    ///     移行が済んでいない役職が無音で弱体化しないよう、失敗は必ず「現状維持」側に倒す。
    /// </summary>
    public static bool Pierces(int attack, int? defense)
    {
        if (Options.EnableAttackDefenseLevels is { } option && !option.GetBool()) return false;

        return defense != null && attack > defense.Value;
    }

    /// <summary>
    ///     キラーの現在の攻撃力。
    ///     散在していた Pestilence / KillingMachine の手書き判定をここに集める。
    /// </summary>
    public static int GetAttackPower(PlayerControl killer, AttackKind kind)
    {
        if (kind == AttackKind.Guess) return Unstoppable;
        if (kind == AttackKind.Retaliation) return Unstoppable;
        if (killer == null) return Basic;

        // KillingMachine の攻撃だけ抗えない (Lv3)。十字軍の身代わり (Lv2) を今も素通りしている事実の追認。
        // ⚠️ 「シールドを貫く」設定が切られている時は普通の攻撃 (Lv1) に戻す。
        //    梯子側で固定していた頃は、設定を OFF にしてもシールドを貫き続けていた。
        // ⚠️ Pestilence はここに入れない。無敵なのは守りの側 (反射盾) で、
        //    キル自体は通常クールダウンの普通の攻撃 = 基本 (Lv1)。
        if (killer.Is(CustomRoles.KillingMachine)) return KillingMachine.BypassShields.GetBool() ? Unstoppable : Basic;

        return Main.PlayerStates.TryGetValue(killer.PlayerId, out PlayerState state) && state.Role != null
            ? state.Role.GetAttackPower(killer, kind)
            : Basic;
    }

    /// <summary>
    ///     ターゲットの役職自身が持つ防御力。
    ///     他役職から与えられた保護 (Medic / GA / Crusader 等) は付与側に定数があり、
    ///     関所の各 if をその場で括る形で扱う (spec §4-4)。
    /// </summary>
    public static int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        if (target == null) return null;

        if (target.Is(CustomRoles.Pestilence)) return kind == AttackKind.Exile ? None : Unstoppable;

        return Main.PlayerStates.TryGetValue(target.PlayerId, out PlayerState state) && state.Role != null
            ? state.Role.GetDefensePower(target, kind)
            : null;
    }

    /// <summary>
    ///     <paramref name="killer" /> の <paramref name="kind" /> 攻撃が、
    ///     <paramref name="target" /> の役職自身の防御を貫くか。
    /// </summary>
    public static bool Pierces(PlayerControl killer, PlayerControl target, AttackKind kind)
    {
        return Pierces(GetAttackPower(killer, kind), GetDefensePower(target, kind));
    }

    /// <summary>
    ///     ブロックのたびにキラーのキルクールダウンを戻す守りのための、キラー単位の最短間隔。
    /// </summary>
    private static readonly Dictionary<byte, float> LastBlockedAttackerReset = [];

    private const float BlockedAttackerResetMinInterval = 1f;

    /// <summary>ゲーム開始時に呼ぶ。前のゲームの時刻が残っていても実害は無いが、状態は持ち越さない。</summary>
    public static void ResetBlockedAttackerThrottle()
    {
        LastBlockedAttackerReset.Clear();
    }

    /// <summary>
    ///     「弾いたのでキルクールダウンを戻す」を、キラー単位で最短 1 秒に間引いて行う。
    ///     消費しない守り (王 / メディック / スーパー無敵 / リコシェ / ベントオープナー) は同じ相手を
    ///     何度でも弾くため、毎フレーム判定で攻める役職 (陰陽師 / Torpedo / 人形 / ケミスト) に当たると
    ///     ブロックのたびに <see cref="ExtendedPlayerControl.SetKillCooldown" /> が走る。その中の
    ///     <c>SyncSettings()</c> はフル GameOptions を dirty-check 無しで送り直すので、
    ///     公式鯖では秒十数本の GameDataTo が積み上がって数秒で切断される (2026-09-21 実機で 2 回確認)。
    ///     弾いた合図としては 1 回で足りるので、ここで間引く。
    /// </summary>
    /// <returns>true = 実際に戻した。false = 直前に戻したばかりなので間引いた (呼び出し側が演出を持つならそれも省ける)。</returns>
    public static bool ResetBlockedAttackerCooldown(PlayerControl killer, float time = -1f)
    {
        if (killer == null) return false;

        float now = UnityEngine.Time.time;

        if (LastBlockedAttackerReset.TryGetValue(killer.PlayerId, out float last) && now - last < BlockedAttackerResetMinInterval)
            return false;

        LastBlockedAttackerReset[killer.PlayerId] = now;
        killer.SetKillCooldown(time);
        return true;
    }

    /// <summary>
    ///     反撃の応酬で関所へ無限に潜らないための再入ガード。
    ///     反撃はキルの関所の中 (OnCheckMurderAsTarget) から飛ぶので、
    ///     撃ち返された相手がさらに撃ち返すと同じ呼び出しの中で積み重なる。
    /// </summary>
    private static readonly HashSet<byte> RetaliationInProgress = [];

    /// <summary>
    ///     反撃キル (ベテラン等が殺し返す) を関所に通す。true = 反撃が成立する。
    ///     攻撃レベルは抗えない (Lv3) なので、無敵 (Lv3) 以外の守りは貫く。
    ///     陣営ルール (CTA / AFKシールド / 陣営同士) はここでも守られる。
    /// </summary>
    /// <param name="performKill">
    ///     true = 判定が通ったらそのまま殺すところまでやる (呼び出し側に後始末が無い時)。
    /// </param>
    public static bool Retaliate(PlayerControl avenger, PlayerControl victim, bool performKill = false)
    {
        if (avenger == null || victim == null) return false;

        // 既にこの相手への反撃処理の中にいる = 撃ち返しの撃ち返し。従来どおり素通しして深追いしない。
        if (!RetaliationInProgress.Add(victim.PlayerId)) return true;

        try
        {
            return performKill
                ? avenger.RpcCheckAndMurder(victim, kind: AttackKind.Retaliation)
                : CheckMurderPatch.PassesGate(avenger, victim, kind: AttackKind.Retaliation);
        }
        finally { RetaliationInProgress.Remove(victim.PlayerId); }
    }

    /// <summary>
    ///     追放の関所。最多得票になった時点で呼ばれ、true = 追放が阻止された。
    ///     キルの関所とは別建てで、murder 系の防御レベルは
    ///     <see cref="RoleBase.MurderOnly" /> により既定で波及しない (spec §2)。
    /// </summary>
    public static bool BlocksExile(byte id)
    {
        if (!Main.PlayerStates.TryGetValue(id, out PlayerState state) || state.Role == null) return false;

        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc == null) return false;

        int atk = GetAttackPower(null, AttackKind.Exile);
        return !Pierces(atk, GetDefensePower(pc, AttackKind.Exile)) && state.Role.OnVotedOut(pc);
    }
}
