using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace EndKnot.Modules.Ekm;

// EKN 役職メーカー R0 の実行時マネージャ。
// 計画正典: docs/ekn-api-plan.md。CustomRoles.EkmCustomRole1..10 の10スロットへ
// EkrDefinition (ノーコード役職定義) を束縛する。ロビー内でのみ束縛操作を許可する
// (試合中の束縛変更はゲームエンド判定等の静的 switch と整合しなくなるため禁止)。
public static class EkrManager
{
    public const string CodePrefix = "EKR1.";

    public static readonly CustomRoles[] Slots =
    [
        CustomRoles.EkmCustomRole1,
        CustomRoles.EkmCustomRole2,
        CustomRoles.EkmCustomRole3,
        CustomRoles.EkmCustomRole4,
        CustomRoles.EkmCustomRole5,
        CustomRoles.EkmCustomRole6,
        CustomRoles.EkmCustomRole7,
        CustomRoles.EkmCustomRole8,
        CustomRoles.EkmCustomRole9,
        CustomRoles.EkmCustomRole10
    ];

    // slot -> 束縛中の定義。ロビーでのみ変更する (Bind/Unbind)。試合中の per-round リセット対象外。
    private static readonly Dictionary<CustomRoles, EkrDefinition> Bound = [];

    // enum 範囲比較の O(1) 判定 (GetRoleSpawnMode 等の高頻度経路から呼ばれる)。
    public static bool IsSlot(CustomRoles role)
    {
        return role is >= CustomRoles.EkmCustomRole1 and <= CustomRoles.EkmCustomRole10;
    }

    // slot -> 現在そのロールが割り当てられているプレイヤー。RoleBase.Init/Add/Remove から更新。
    private static readonly Dictionary<CustomRoles, HashSet<byte>> PlayersBySlot = [];

    // ディスク上のライブラリ (import 済みの役職コード一覧)。
    private static readonly List<(string FileName, EkrDefinition Def)> Library = [];

    public static readonly string RolesPath = BuildRolesPath();

    private static string BuildRolesPath()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            return docs.Replace('\\', '/') + "/EndKnot/EKRoles/";
        }
        catch
        {
            return null;
        }
    }

    public static void EnsureFolder()
    {
        if (string.IsNullOrEmpty(RolesPath)) return;
        try
        {
            if (!Directory.Exists(RolesPath)) Directory.CreateDirectory(RolesPath);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[EkrManager] Could not create EKRoles folder ({RolesPath}): {ex.Message}", "EkrManager");
        }
    }

    // ── ライブラリ (ディスク上の *.ekrole.json) ─────────────────────────────

    public static void ReloadLibrary()
    {
        Library.Clear();
        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath) || !Directory.Exists(RolesPath)) return;

        foreach (string path in Directory.GetFiles(RolesPath, "*.ekrole.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(path);
                if (!EkrDefinition.TryParse(json, out EkrDefinition def, out string error))
                {
                    Logger.Warn($"[EkrManager] Skipping invalid role file {Path.GetFileName(path)}: {error}", "EkrManager");
                    continue;
                }

                Library.Add((Path.GetFileName(path), def));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EkrManager] Could not read role file {path}: {ex.Message}", "EkrManager");
            }
        }
    }

    public static IReadOnlyList<(string FileName, EkrDefinition Def)> ListLibrary()
    {
        return Library;
    }

    public static bool TryImportCode(string code, out string savedFileName, out string error)
    {
        savedFileName = null;

        if (!EkmCodec.TryDecode(code, out string json, out error, CodePrefix))
            return false;

        if (!EkrDefinition.TryParse(json, out EkrDefinition def, out error))
            return false;

        EnsureFolder();
        if (string.IsNullOrEmpty(RolesPath))
        {
            error = "保存先フォルダ (Documents/EndKnot/EKRoles) を用意できませんでした";
            return false;
        }

        string baseName = SanitizeFileName(def.Name);
        string fileName = $"{baseName}.ekrole.json";
        string fullPath = RolesPath + fileName;

        // 同名は連番で回避 (上書きしない)。
        int n = 2;
        while (File.Exists(fullPath))
        {
            fileName = $"{baseName}_{n}.ekrole.json";
            fullPath = RolesPath + fileName;
            n++;
        }

        try
        {
            File.WriteAllText(fullPath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            error = $"役職コードの保存に失敗しました ({ex.Message})";
            return false;
        }

        savedFileName = fileName;
        ReloadLibrary();
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "custom_role";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(invalid.Contains(c) || c is ' ' or '.' ? '_' : c);
        string result = sb.ToString().Trim('_');
        return result.Length == 0 ? "custom_role" : result;
    }

    // ── スロット束縛 (ロビーのみ) ────────────────────────────────────────────

    public static bool TryAssign(int libraryIndex1Based, int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based is < 1 or > 10)
        {
            error = "スロット番号は1〜10で指定してください";
            return false;
        }

        if (libraryIndex1Based < 1 || libraryIndex1Based > Library.Count)
        {
            error = "その番号の役職コードが見つかりません (/role list で確認してください)";
            return false;
        }

        CustomRoles slot = Slots[slotNumber1Based - 1];
        EkrDefinition def = Library[libraryIndex1Based - 1].Def;
        Bind(slot, def);
        return true;
    }

    public static bool TryUnassign(int slotNumber1Based, out string error)
    {
        error = null;

        if (!GameStates.IsLobby)
        {
            error = "役職の割り当てはロビーでのみ変更できます";
            return false;
        }

        if (slotNumber1Based is < 1 or > 10)
        {
            error = "スロット番号は1〜10で指定してください";
            return false;
        }

        Unbind(Slots[slotNumber1Based - 1]);
        return true;
    }

    private static void Bind(CustomRoles slot, EkrDefinition def)
    {
        Bound[slot] = def;

        // 表示名の実行時上書き (RoleBase.StartSetup 系・GetRoleName 等が共通で読む翻訳キー = 型名 = enum 名)。
        Translator.SetRuntimeOverride(slot.ToString(), def.Name);

        // 色: Main.RoleHtmlColors が正典 (Main.cs RoleHtmlColors 辞書)。
        Main.RoleHtmlColors[slot] = def.Color;
        Main.InitRoleColors();

        // 束縛 = 「次のゲームで使う」宣言なので、出現率オプションを 100% にしてメニューにも出す。
        // 保存されても安全: 未束縛スロットは Options.GetRoleSpawnMode のガードで常に 0 扱いになる。
        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetColor(Utils.GetRoleColor(slot));
            opt.SetHidden(false);
            opt.SetValue(Options.Rates.Length - 1);
        }
    }

    private static void Unbind(CustomRoles slot)
    {
        Bound.Remove(slot);
        Translator.ClearRuntimeOverride(slot.ToString());

        if (Options.CustomRoleSpawnChances != null && Options.CustomRoleSpawnChances.TryGetValue(slot, out var opt))
        {
            opt.SetValue(0);
            opt.SetHidden(true);
        }
    }

    // /role set でスロット省略時に使う最初の空きスロット (1..10)。空きが無ければ 0。
    public static int FirstFreeSlotNumber()
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            if (!Bound.ContainsKey(Slots[i]))
                return i + 1;
        }

        return 0;
    }

    public static EkrDefinition GetDefinition(CustomRoles slot)
    {
        return Bound.GetValueOrDefault(slot);
    }

    public static bool IsBound(CustomRoles slot)
    {
        return Bound.ContainsKey(slot);
    }

    // ── per-round プレイヤー追跡 (RoleBase.Init/Add/Remove から呼ばれる) ────────

    public static void ResetSlot(CustomRoles slot)
    {
        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Clear();
        else PlayersBySlot[slot] = [];
    }

    public static void AddPlayer(CustomRoles slot, byte playerId)
    {
        if (!PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) PlayersBySlot[slot] = set = [];
        set.Add(playerId);
    }

    public static void RemovePlayer(CustomRoles slot, byte playerId)
    {
        if (PlayersBySlot.TryGetValue(slot, out HashSet<byte> set)) set.Remove(playerId);
    }

    public static bool HasPlayers(CustomRoles slot)
    {
        return PlayersBySlot.TryGetValue(slot, out HashSet<byte> set) && set.Count > 0;
    }
}
