using System;
using AmongUs.GameOptions;
using EndKnot.Modules.Ekm;

namespace EndKnot.Roles;

// EKN ノーコード役職メーカー R0 の共通実体 (計画正典: docs/ekn-api-plan.md)。
// 10個の予約スロット (CustomRoles.EkmCustomRole1..10) はこのクラスの「スロット番号だけ持つ薄い派生」。
// per-slot の状態は static フィールドで持たない (基底クラスの static は全スロットで1個しか無いため、
// 誤って混ざる) — 必ず EkrManager に CustomRoles (Slot) をキーとして持たせる。
public abstract class EkmTemplateRole : RoleBase
{
    // RoleBase.IsThisRole 等と同じ規約: 派生クラスの型名 = CustomRoles enum 名 (EkmCustomRole1..10)。
    protected CustomRoles Slot => Enum.Parse<CustomRoles>(GetType().Name, true);

    public override bool IsEnable => EkrManager.HasPlayers(Slot);

    // SetupCustomOption は各スロットクラス側で実装する (check-option-ids.ps1 は
    // `Options.SetupRoleOptions(<id>` と同一ファイル内の `const int Id = <literal>` を突き合わせて
    // 衝突検査するため、id リテラルと呼び出しは同じファイルに置く必要がある)。
    // R0 は最小構成: 役職ヘッダ (出現率) + Maximum のみ。数値パラメータは役職コード (JSON) 側が持つので
    // ホストオプション化しない (ekn-api-plan §決定事項6)。

    // ── Wave 3 (docs/ekn-wave3-contract.md §4.2): ホスト露出の「前登録プール」 ──────────────────
    //
    // 🔴 Bind 時に OptionItem を新規生成することは構造的に不可能 (保存値の復元 OptionSaver.Load が
    // 束縛より先に走る / 役職タブは GroupedOptions の一度きりのスナップショット / repo に後付け生成の
    // 前例がゼロ)。そこで各スロットが Id+2..Id+9 の 8 枠を**最初から作っておき**、束縛時に名前 (翻訳の
    // 実行時上書き) と表示/非表示と既定値だけを差し替える。
    //
    // ⚠️ id は必ず `Id + N` のリテラル形で各スロットのファイルに書くこと (ここでループ生成すると
    // tools/check-option-ids.ps1 の解決対象から外れ、衝突検査の網に穴が空く)。
    protected const int HostOptionPoolSize = 8;

    // 値域は**広め固定**。動的 ValueRule は BaseGameSettingCache の二重凍結と、オプション同期が
    // 生インデックス送信であること (ホスト/客で Rule が食い違うと客のロビー表示が別の数値に化ける) の
    // 2点で採用できない。範囲外は消費時にクランプする側で吸収する (契約 §4.2)。
    //
    // ⚠️ **負側を必ず含めること**。`var:` 露出は符号付きの変数 (「かちに必要な数 -5〜+5」等) を
    // 一級サポートしており、値域が 0 始まりだと作者の初期値が負のときに `FloatValueRule.RepeatIndex`
    // (ValueRule.cs:61-68) が**負のインデックスを 0 側へクランプせず maxIndex へ折り返す**ため、
    // -50 が 600 に化ける無音のデータ破損になる (2026-08-14 完成前監査で捕獲)。
    // -999..999 は固定6キーの契約範囲 (最大 doom 600 / killCooldown 300) を全て内包する。
    protected static FloatValueRule HostOptionRule => new(-999f, 999f, 0.1f);

    // 翻訳キー。束縛時に Translator.SetRuntimeOverride で作者のラベルへ差し替える (出現率行の
    // 名前上書きと同じ機構)。未束縛のときは枠ごと隠れているので lang への既定エントリは持たせない。
    protected string HostOptionName(int index) => $"{Slot}HostOpt{index}";

    protected void SetupHostOptionPool(params OptionItem[] pool)
    {
        OptionItem parent = Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(Slot, out var spawnOpt) ? spawnOpt : null;

        foreach (OptionItem opt in pool)
        {
            // 親 = 出現率行。親が隠れていれば子も自動で隠れる (IsCurrentlyHidden は毎回評価される)。
            if (parent != null) opt.SetParent(parent);
            opt.SetHidden(true); // 既定は非表示 — 束縛した役職コードが露出を宣言した枠だけ開く
        }

        EkrManager.RegisterHostOptionPool(Slot, pool);
    }

    // 未束縛スロットをオプションメニューから隠す (各スロットの SetupCustomOption 末尾から呼ぶ)。
    // 表示と出現率の復帰は EkrManager.Bind/Unbind が行い、選出の安全網は Options.GetRoleSpawnMode。
    protected void HideUntilBound()
    {
        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(Slot, out var opt))
            opt.SetHidden(!EkrManager.IsBound(Slot));
    }

    public override void Init()
    {
        EkrManager.ResetSlot(Slot);
    }

    public override void Add(byte playerId)
    {
        EkrManager.AddPlayer(Slot, playerId);
    }

    public override void Remove(byte playerId)
    {
        EkrManager.RemovePlayer(Slot, playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        // R1: set_kill_cooldown opcode によるランタイム上書きを優先する (無ければ従来どおり役職コードの値)。
        float? runtimeOverride = EkrManager.GetKillCooldownOverride(id);
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        // Wave 3 (契約 §4): killCooldown をホストに露出している役職コードならホストの値を使う。
        Main.AllPlayerKillCooldown[id] = runtimeOverride ?? (def is { CanKill: true } ? EkrManager.GetEffectiveKillCooldown(Slot, def.KillCooldown) : Options.AdjustedDefaultKillCooldown);
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        return def is { CanKill: true } && pc.IsAlive();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        return def is { CanVent: true };
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);
        if (def == null) return;

        // Wave 3 (契約 §4): vision をホストに露出している役職コードならホストの値を使う。
        float vision = EkrManager.GetEffectiveVision(Slot, def.VisionMultiplier);

        if (Math.Abs(vision - 1f) > 0.001f)
        {
            opt.SetVision(false);

            // R2: 基底が本物/desync の Impostor になる役職 (インポスター陣営スロット・キル可の第三陣営)
            // は ImpostorLightMod の側を読む。SetVision(false) は「今の CrewLightMod を ImpostorLightMod へ
            // 複製する」動きなので、片方だけ書くともう片方に旧値が残る。家の標準形 (PlayerGameOptionsSender
            // .cs:489-491) と同じく両方へ明示的に書く。
            opt.SetFloat(FloatOptionNames.CrewLightMod, vision);
            opt.SetFloat(FloatOptionNames.ImpostorLightMod, vision);
        }

        // Wave 1 (docs/ekr-logic-spec.md §1.1): passives.killDistance を vanilla 0/1/2 へ写像。
        // 未指定 (-1) はホスト設定のまま。
        if (def.ParsedPassives.KillDistance >= 0)
            opt.SetInt(Int32OptionNames.KillDistance, def.ParsedPassives.KillDistance);
    }

    // Wave 1 (spec §2 on_attacked): 自分へのキル試行の一点関門。まもり (passives.shield) の消費判定と
    // on_attacked の同期プロローグはすべて EkrManager 側 (per-holder 状態を触るのはあちらの責務)。
    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (!base.OnCheckMurderAsTarget(killer, target)) return false;

        return EkrManager.FireAttacked(Slot, target, killer);
    }

    // ── R1 (docs/ekr-logic-spec.md): イベントフック→発行のみの薄い配線 ──────────
    // per-holder 状態は一切持たず、すべて EkrManager (playerId キー) へ委譲する。

    public override void OnPet(PlayerControl pc)
    {
        EkrDefinition def = EkrManager.GetDefinition(Slot);

        if (def?.ParsedLogic == null)
        {
            base.OnPet(pc); // logic 無し (R0 のみの役職) は従来どおりフレーバーテキスト
            return;
        }

        EkrManager.FirePet(Slot, pc);
    }

    // Wave 1 (spec §2 発動トリガ統合): on_pet は「とくいわざボタンをおしたとき」= 能力ボタンの発動全般。
    // AST の id は on_pet のまま (契約不変) で、役職基盤がペット以外のボタンを提供する場合はそちらでも
    // 同じイベントを発行する。現行の EKR 基盤 (Crewmate / CanVent=Engineer / CanKill=desync Impostor) は
    // シェイプシフト/バニッシュを持たないため、いまは将来の基盤拡張に備えた配線。
    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (shapeshifting) FireAbilityButton(shapeshifter); // 戻り (shapeshifting=false) では二重発火させない
        return true;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        FireAbilityButton(pc);
        return true;
    }

    private void FireAbilityButton(PlayerControl pc)
    {
        if (!pc) return;

        EkrDefinition def = EkrManager.GetDefinition(Slot);
        if (def?.ParsedLogic == null) return;

        EkrManager.FirePet(Slot, pc);
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        EkrManager.FireKill(Slot, killer, target);
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        EkrManager.FireVentEnter(Slot, pc);
    }

    // Wave 3 (docs/ekn-wave3-contract.md §1.4): ExitVentPatch.Postfix (ホストガード後) からの1本道。
    public override void OnExitVent(PlayerControl pc, Vent vent)
    {
        EkrManager.FireVentExit(Slot, pc);
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        EkrManager.FireTaskComplete(Slot, pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        EkrManager.Pump(Slot, pc);
    }

    // ── Wave 3 (docs/ekn-wave3-contract.md §3 progress): 名前の横に出す作者の文字 ──
    // 加算型 — base の ability-limit + タスク数を残して末尾に足す (Tank.cs:70 型)。
    // ⚠️ Utils.GetProgressText は共有 StringBuilder を使い回すため、この override の中から
    // 直接にも間接的にも呼ばないこと (base.GetProgressText は SB を触らない別経路)。
    public override string GetProgressText(byte playerId, bool comms)
    {
        return base.GetProgressText(playerId, comms) + EkrManager.BuildProgressText(Slot, playerId);
    }

    // on_meeting_end (会議明け・タスク再開時)。このメソッド自体は「保持者の人数ぶん」呼ばれる共有
    // シングルトン呼び出しなので、重複排除は EkrManager.FireMeetingEndForSlot 側 (会議番号ベース) で行う。
    public override void AfterMeetingTasks()
    {
        EkrManager.FireMeetingEndForSlot(Slot);
    }

    // ── Wave 2 (docs/ekn-wave2-contract.md §1.1 on_meeting_vote): CastVote 関門 (MeetingHudPatch.cs:1610) ──
    // 戻り値 = 票を消費した (=キャンセルした) か。CancelsVote() の EKR arm (定義に on_meeting_vote
    // ルールがあれば true) が通ったときだけこの経路に来る。Oracle/FortuneTeller と同じ
    // 「cancel したら Main.DontCancelVoteList へ積んで revote を許す」規約 — これが cancel_vote の
    // 「ひと会議に1回だけ有効」を無料で実現する (2回目は CancelsVote() の外側ゲートで OnVote 自体が
    // 呼ばれなくなる)。
    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (!voter || !target) return false;

        bool canceled = EkrManager.FireMeetingVote(Slot, voter, target);
        if (canceled) Main.DontCancelVoteList.Add(voter.PlayerId);

        return canceled;
    }

    // ── Wave 2 (spec §2.2 reveal): KnowRole は 1 点 override のみ (4 表示系の総なめ集約が拾う)。
    // 集約は Main.PlayerStates.Values.Any(x => x.Role.KnowRole(seer, target)) — this や x には依存せず
    // seer/target の playerId だけで判定すること (個別サイトへの直書き禁止・memory 罠)。
    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (!seer || !target) return false;

        return EkrManager.HasRevealed(seer.PlayerId, target.PlayerId);
    }

    // ── Wave 2 (spec §2.3 矢印): Scout.GetSuffix と同じ3点セット (自分の名札の上にだけ描画)。
    // advisor 指摘 (2026-08-11): 呼び出し元 (Utils.BuildSuffix) は Main.PlayerStates.Values を全部
    // なめて state.Role.GetSuffix(seer, target, ...) を呼ぶ — this は「そのスロットの共有シングルトン」
    // であって seer の役職とは無関係。ガード無しだと束縛中の EKR スロットの数だけ矢印が重複描画される
    // (KnowRole アグリゲータと同型の罠 — this に依存せず、seer が「このスロットの保持者か」を毎回検証する)。
    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer == null || (target != null && seer.PlayerId != target.PlayerId) || meeting || hud) return string.Empty;
        if (seer.GetCustomRole() != Slot) return string.Empty;

        return TargetArrow.GetAllArrows(seer.PlayerId) + LocateArrow.GetArrows(seer);
    }
}
