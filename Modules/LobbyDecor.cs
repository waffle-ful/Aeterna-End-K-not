using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

// ロビーで host と mod client にだけ見えるローカル装飾。
// vanilla netobject を一切 spawn しないので anti-cheat に検出されない。
// 非モッド client は CustomRPC.LobbyDecorSpawn を解釈できないので何も見えない。
//
// 検証目的のプロトタイプ。動作確認後、温泉ロビーの本実装テンプレートとして拡張する
public static class LobbyDecor
{
    public readonly record struct Decor(int Id, Vector2 Pos, string Text, float FontSize, Color Color);

    // host 側で「現在のロビー装飾セット」を保持。late-join 時の再送に使う
    private static readonly Dictionary<int, Decor> CurrentDecors = [];
    // ローカルにインスタンス化した GameObject。host/mod client 両方が保持
    private static readonly Dictionary<int, GameObject> Spawned = [];

    // host が呼ぶ: ローカル spawn + 既存全 client に broadcast + 状態保存
    public static void BroadcastSpawn(Decor decor)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        CurrentDecors[decor.Id] = decor;
        SpawnLocal(decor);
        SendSpawnRpc(decor, targetClientId: -1);
    }

    // 既存 client が join した時 host が呼ぶ: その client にだけ再送
    public static void ReplayToClient(int targetClientId)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (CurrentDecors.Count == 0) return;

        foreach (Decor d in CurrentDecors.Values)
            SendSpawnRpc(d, targetClientId);

        Logger.Info($"LobbyDecor: replayed {CurrentDecors.Count} decors to client {targetClientId}", "LobbyDecor");
    }

    public static void BroadcastClear()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        CurrentDecors.Clear();
        ClearLocal();

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            PlayerControl.LocalPlayer.NetId,
            (byte)CustomRPC.LobbyDecorClear,
            SendOption.Reliable);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static void ReceiveSpawnRPC(MessageReader reader)
    {
        int id = reader.ReadPackedInt32();
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();
        string text = reader.ReadString();
        float fontSize = reader.ReadSingle();
        byte r = reader.ReadByte();
        byte g = reader.ReadByte();
        byte b = reader.ReadByte();
        SpawnLocal(new Decor(id, new Vector2(x, y), text, fontSize, new Color(r / 255f, g / 255f, b / 255f)));
    }

    public static void ReceiveClearRPC() => ClearLocal();

    private static void SendSpawnRpc(Decor d, int targetClientId)
    {
        MessageWriter writer = targetClientId < 0
            ? AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.LobbyDecorSpawn, SendOption.Reliable)
            : AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.LobbyDecorSpawn, SendOption.Reliable, targetClientId);
        writer.WritePacked(d.Id);
        writer.Write(d.Pos.x);
        writer.Write(d.Pos.y);
        writer.Write(d.Text);
        writer.Write(d.FontSize);
        writer.Write((byte)(d.Color.r * 255));
        writer.Write((byte)(d.Color.g * 255));
        writer.Write((byte)(d.Color.b * 255));
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    private static void SpawnLocal(Decor d)
    {
        if (Spawned.TryGetValue(d.Id, out GameObject existing) && existing != null)
            Object.Destroy(existing);

        // BGMInfoDisplay と同じ TMP クローンパターン。asset 不要
        TextMeshPro template = null;
        if (HudManager.InstanceExists && HudManager.Instance.KillButton != null)
            template = HudManager.Instance.KillButton.cooldownTimerText;
        template ??= Object.FindObjectOfType<TextMeshPro>();

        if (template == null)
        {
            Logger.Warn($"LobbyDecor.SpawnLocal id={d.Id}: no TMP template", "LobbyDecor");
            return;
        }

        TextMeshPro tmp = Object.Instantiate(template);
        tmp.gameObject.SetActive(false);
        tmp.gameObject.name = $"LobbyDecor_{d.Id}";
        tmp.DestroyTranslator();
        tmp.transform.SetParent(null, false);

        AspectPosition inheritedAp = tmp.GetComponent<AspectPosition>();
        if (inheritedAp != null) Object.Destroy(inheritedAp);

        tmp.text = d.Text;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = tmp.fontSizeMax = tmp.fontSizeMin = d.FontSize;
        tmp.color = d.Color;
        tmp.transform.position = new Vector3(d.Pos.x, d.Pos.y, 1f);
        tmp.transform.localScale = Vector3.one;
        tmp.sortingOrder = 50;
        tmp.gameObject.SetActive(true);
        tmp.ForceMeshUpdate();

        Spawned[d.Id] = tmp.gameObject;
        Logger.Info($"LobbyDecor spawned id={d.Id} at {d.Pos} text='{d.Text}'", "LobbyDecor");
    }

    private static void ClearLocal()
    {
        foreach (GameObject go in Spawned.Values)
            if (go != null) Object.Destroy(go);
        Spawned.Clear();
        Logger.Info("LobbyDecor cleared", "LobbyDecor");
    }
}

// 既存 client が join したら現在の装飾をその client にだけ再送
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class LobbyDecorReplayHook
{
    public static void Postfix([HarmonyArgument(0)] ClientData client)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (client == null) return;

        // PlayerControl の spawn が完了するまで少し待つ
        LateTask.New(() =>
        {
            if (!GameStates.IsLobby) return;
            LobbyDecor.ReplayToClient(client.Id);
        }, 1.5f, "LobbyDecor.Replay");
    }
}

// ロビー脱出時にクリーンアップ (host/client 両方)
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.OnDestroy))]
internal static class LobbyDecorClearHook
{
    public static void Postfix()
    {
        // host だけが broadcast 役。client はローカルクリアのみ
        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
            LobbyDecor.BroadcastClear();
        else
            LobbyDecor.ReceiveClearRPC();
    }
}
