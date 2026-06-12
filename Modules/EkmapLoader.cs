using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace EndKnot.Modules;

// EKM v1/v2 カスタムマップローダー (Phase 0 / Phase B)
// 仕様: docs/ekmap-spec.md (凍結 2026-06-10 v1 / 凍結 2026-06-11 v2)
public static class EkmapLoader
{
    // BepInEx.Paths ベースで確実にゲームフォルダ配下を指す
    // (Environment.CurrentDirectory は起動方法 (Steam/Epic/ショートカット) で変わり得るため不採用)
    public static readonly string EKMapsPath =
        $"{BepInEx.Paths.BepInExRootPath.Replace(@"\", "/")}/resources/EKMaps/";

    // Web エディタの直接保存先 (FS Access API は Program Files 配下を blocklist 拒否するため必要)
    // ROADMAP D6: Documents/EndKnot/EKMaps を第2検索パスとして追加
    // MyDocuments が空文字の環境 (一部 Linux/Android) では null → 全処理で安全にスキップ
    public static readonly string UserEKMapsPath = _BuildUserEKMapsPath();

    private static string _BuildUserEKMapsPath()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return null;
            return docs.Replace(@"\", "/") + "/EndKnot/EKMaps/";
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        PropertyNameCaseInsensitive = true,
    };

    // ── 外部 API ──────────────────────────────────────────────────────────────

    // null = procgen モード (EkmapLoader 未使用)
    public static CustomMapSource ActiveSource { get; private set; }

    // ActiveSource を解除して procgen に戻す (/map exit / /bbenter 等の procgen 系コマンドから呼ぶ)
    public static void ClearActiveSource() => ActiveSource = null;

    // ExitBackrooms 後に src を復元するための公開 setter (ActiveSource は private set のまま)
    public static void Activate(CustomMapSource src) => ActiveSource = src;

    // フォルダ準備 + golden サンプル初回書き出し (Main / LobbyBehaviour Start 等から呼ぶ)
    // golden サンプルの書き出し先は primary の EKMapsPath のみ (UserEKMapsPath には書かない)
    public static void EnsureFolder()
    {
        if (!Directory.Exists(EKMapsPath))
        {
            Directory.CreateDirectory(EKMapsPath);
            Logger.Info($"Created EKMaps folder: {EKMapsPath}", "EkmapLoader");
        }

        string samplePath = EKMapsPath + "sample.ekmap.json";
        if (!File.Exists(samplePath))
        {
            File.WriteAllText(samplePath, GoldenSample, new System.Text.UTF8Encoding(false));
            Logger.Info("Wrote golden sample to sample.ekmap.json", "EkmapLoader");
        }

        // 第2パス (UserEKMapsPath) が有効ならフォルダだけ用意する。失敗は警告のみ
        if (UserEKMapsPath != null && !Directory.Exists(UserEKMapsPath))
        {
            try
            {
                Directory.CreateDirectory(UserEKMapsPath);
                Logger.Info($"Created user EKMaps folder: {UserEKMapsPath}", "EkmapLoader");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[EkmapLoader] Could not create user EKMaps folder ({UserEKMapsPath}): {ex.Message}", "EkmapLoader");
            }
        }
    }

    // EKMaps フォルダの一覧を返す。ファイルが 1 つもなければ空リスト
    // 両パスを走査し、同名ファイルは UserEKMapsPath 側を優先 (ユーザー編集中の最新を表示)
    public static List<(string filename, long bytes)> ListMaps()
    {
        EnsureFolder();
        // filename → フルパス の辞書 (UserEKMapsPath 優先なので後勝ち = primary を先に入れ、user で上書き)
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string f in Directory.GetFiles(EKMapsPath, "*.ekmap.json"))
            seen[Path.GetFileName(f)] = f;

        if (UserEKMapsPath != null && Directory.Exists(UserEKMapsPath))
        {
            foreach (string f in Directory.GetFiles(UserEKMapsPath, "*.ekmap.json"))
                seen[Path.GetFileName(f)] = f; // 同名は UserEKMapsPath 側で上書き
        }

        var result = new List<(string filename, long bytes)>(seen.Count);
        foreach (var kv in seen)
            result.Add((kv.Key, new FileInfo(kv.Value).Length));

        result.Sort((a, b) => string.Compare(a.filename, b.filename, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // 指定ファイルを読み込んで ActiveSource にセットする。
    // 失敗時は false を返し、理由は errorMessage に格納する。
    public static bool TryLoad(string filename, out string errorMessage)
    {
        EnsureFolder();

        // 拡張子省略サポート (UserEKMapsPath → EKMapsPath の優先順で探索)
        string resolved = ResolveFilename(filename);
        if (resolved == null)
        {
            // 探した場所を全てユーザーに見せる (フォルダ取り違え・タイポの自己診断用)
            string paths = UserEKMapsPath != null
                ? $"{UserEKMapsPath}\n{EKMapsPath}"
                : EKMapsPath;
            errorMessage = $"File not found: {filename}\n<size=70%>{paths}</size>";
            return false;
        }

        long size = new FileInfo(resolved).Length;
        // 上限 4MB (v2 最大値。v1 512KB チェックは TryParse 内で ekm バージョン確定後に行う)
        if (size > 4 * 1024 * 1024)
        {
            errorMessage = $"File too large ({size} bytes > 4 MB limit)";
            return false;
        }

        string json;
        try { json = File.ReadAllText(resolved, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            errorMessage = $"Read error: {ex.Message}";
            return false;
        }

        return TryParse(json, Path.GetFileName(resolved), out errorMessage);
    }

    // 現在ロード中のファイルを再読み込みする (エディタ往復ループ用)
    public static bool TryReload(out string errorMessage)
    {
        if (ActiveSource == null)
        {
            errorMessage = "No map currently loaded";
            return false;
        }

        return TryLoad(ActiveSource.Filename, out errorMessage);
    }

    // マップコード (EKM1.…) を取り込んでファイルに保存し、検証込みでロードする。
    // 成功時: savedFilename にファイル名、ActiveSource にセット (呼び出し側が EnterCustomMap する)。
    // 失敗時: false + errorMessage。検証に失敗した場合は書き込んだファイルを掃除する。
    public static bool TryImportCode(string code, out string savedFilename, out string errorMessage)
    {
        savedFilename = null;

        if (!EkmCodec.TryDecode(code, out string json, out errorMessage))
            return false;

        // 保存先: 書き込み可能な UserEKMapsPath を優先。無ければ primary。
        string folder = UserEKMapsPath ?? EKMapsPath;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not create folder for import ({folder}): {ex.Message}";
            return false;
        }

        // ファイル名は取り込んだマップの name から導出 (sanitize)。取れなければ "imported"。
        string baseName = SanitizeFileName(PeekName(json) ?? "imported");
        savedFilename = $"imported_{baseName}.ekmap.json";
        string fullPath = folder + savedFilename;

        try
        {
            File.WriteAllText(fullPath, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not write imported map ({fullPath}): {ex.Message}";
            return false;
        }

        // TryLoad で本検証 (ResolveFilename が UserEKMapsPath を優先的に見つける)。
        if (!TryLoad(savedFilename, out errorMessage))
        {
            // 不正なコードのファイルが残らないよう掃除する
            try { File.Delete(fullPath); } catch { /* best-effort */ }
            savedFilename = null;
            return false;
        }

        return true;
    }

    // 現在ロード中のマップをマップコード (EKM1.…) に書き出す。
    // CustomMapSource は raw JSON を保持しないため、元ファイルを読み直してエンコードする。
    public static bool TryExportCurrentCode(out string code, out string errorMessage)
    {
        code = null;
        if (ActiveSource == null)
        {
            errorMessage = "No map currently loaded";
            return false;
        }

        string resolved = ResolveFilename(ActiveSource.Filename);
        if (resolved == null)
        {
            errorMessage = $"Map file not found: {ActiveSource.Filename}";
            return false;
        }

        string json;
        try { json = File.ReadAllText(resolved, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            errorMessage = $"Read error: {ex.Message}";
            return false;
        }

        return EkmCodec.TryEncode(json, out code, out errorMessage);
    }

    // JSON の "name" フィールドだけを軽量に覗く (検証はしない)。失敗時 null。
    private static string PeekName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("name", out JsonElement nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
                return nameEl.GetString();
        }
        catch { /* ignore — 呼び出し側でフォールバック名を使う */ }
        return null;
    }

    // ファイル名に使えない文字を除去し、長さを制限する。
    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim();
        if (s.Length == 0) s = "imported";
        if (s.Length > 40) s = s.Substring(0, 40);
        return s;
    }

    // ── JSON パース + 検証 ────────────────────────────────────────────────────

    private static bool TryParse(string json, string filename, out string errorMessage)
    {
        EkmMapRaw raw;
        try
        {
            raw = JsonSerializer.Deserialize<EkmMapRaw>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            errorMessage = $"JSON parse error: {ex.Message}";
            return false;
        }

        if (raw == null)
        {
            errorMessage = "JSON is null or empty";
            return false;
        }

        // バージョン分岐 (§18)
        if (raw.ekm == 2)
            return TryParseV2(json, raw, filename, out errorMessage);

        if (raw.ekm != 1)
        {
            errorMessage = $"Unsupported ekm version: {raw.ekm} (expected 1 or 2)";
            return false;
        }

        // v1 専用 512KB 制限チェック (§1)
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024)
        {
            errorMessage = $"File too large for ekm:1 (> 512 KB limit)";
            return false;
        }

        string name = raw.name?.Trim() ?? "";
        if (name.Length < 1 || name.Length > 64)
        {
            errorMessage = $"name must be 1-64 characters (got {name.Length})";
            return false;
        }

        if (raw.author != null && raw.author.Length > 32)
        {
            errorMessage = $"author must be 0-32 characters (got {raw.author.Length})";
            return false;
        }

        if (raw.width < 1 || raw.width > 256)
        {
            errorMessage = $"width out of range: {raw.width} (must be 1-256)";
            return false;
        }

        if (raw.height < 1 || raw.height > 256)
        {
            errorMessage = $"height out of range: {raw.height} (must be 1-256)";
            return false;
        }

        if (raw.cells == null || raw.cells.Count != raw.height)
        {
            errorMessage = $"cells length {raw.cells?.Count ?? 0} != height {raw.height}";
            return false;
        }

        // セル文字チェック + グリッド構築
        var grid = new CellKindEkm[raw.width, raw.height];
        for (int row = 0; row < raw.height; row++)
        {
            string line = raw.cells[row] ?? "";
            if (line.Length != raw.width)
            {
                errorMessage = $"cells[{row}] length {line.Length} != width {raw.width}";
                return false;
            }

            for (int col = 0; col < raw.width; col++)
            {
                char c = line[col];
                CellKindEkm k = c switch
                {
                    '.' => CellKindEkm.Floor,
                    '#' => CellKindEkm.Wall,
                    '-' => CellKindEkm.Void,
                    _   => CellKindEkm.Invalid,
                };
                if (k == CellKindEkm.Invalid)
                {
                    errorMessage = $"cells[{row}][{col}] illegal char '{c}'";
                    return false;
                }

                grid[col, row] = k;
            }
        }

        // spawn 検証
        if (raw.spawn == null)
        {
            errorMessage = "spawn is required";
            return false;
        }

        float spawnCellX = raw.spawn.x;
        float spawnCellY = raw.spawn.y;

        if (spawnCellX < 0 || spawnCellX >= raw.width ||
            spawnCellY < 0 || spawnCellY >= raw.height)
        {
            errorMessage = $"spawn ({spawnCellX},{spawnCellY}) out of map range";
            return false;
        }

        int spawnGx = (int)spawnCellX;
        int spawnGy = (int)spawnCellY;
        if (grid[spawnGx, spawnGy] != CellKindEkm.Floor)
        {
            errorMessage = $"spawn ({spawnCellX},{spawnCellY}) is not a floor cell";
            return false;
        }

        // ambient (クランプは黙って許容)
        float visionRadius = 8f;
        if (raw.ambient != null && raw.ambient.TryGetValue("visionRadius", out JsonElement vrElem))
        {
            if (vrElem.TryGetSingle(out float vr))
                visionRadius = Math.Clamp(vr, 4f, 8f);
        }

        // decor — 警告スキップ対応 (§9)
        var decors = new List<DecorEntry>();
        if (raw.decor != null)
        {
            foreach (var d in raw.decor)
            {
                if (d == null) continue;
                string kind = d.kind ?? "";
                if (!IsKnownDecorKind(kind))
                {
                    Logger.Warn($"[EkmapLoader] Unknown decor kind '{kind}', skipping", "EkmapLoader");
                    continue;
                }

                float dx = d.x, dy = d.y;
                if (dx < 0 || dx >= raw.width || dy < 0 || dy >= raw.height)
                {
                    Logger.Warn($"[EkmapLoader] decor '{kind}' at ({dx},{dy}) out of range, skipping", "EkmapLoader");
                    continue;
                }

                decors.Add(new DecorEntry(kind, dx, dy));
            }
        }

        // ビジュアル分類 (§5.1): Wall セルが 4 近傍に Wall が 1 つも無ければ孤立柱 (WallV 系)
        var visualGrid = new VisualKind[raw.width, raw.height];
        for (int gy = 0; gy < raw.height; gy++)
        for (int gx = 0; gx < raw.width; gx++)
        {
            CellKindEkm cell = grid[gx, gy];
            if (cell == CellKindEkm.Floor)
            {
                visualGrid[gx, gy] = VisualKind.Floor;
                continue;
            }

            if (cell == CellKindEkm.Void)
            {
                visualGrid[gx, gy] = VisualKind.Void;
                continue;
            }

            // Wall: 4 近傍に Wall があれば WallH、なければ WallV (孤立柱)
            bool hasWallNeighbor = HasWallNeighbor(grid, raw.width, raw.height, gx, gy);
            visualGrid[gx, gy] = hasWallNeighbor ? VisualKind.WallH : VisualKind.WallV;
        }

        // spawn ワールド座標 (§4: world = (OriginX + x, OriginY - y))
        float spawnWorldX = OriginX + spawnCellX;
        float spawnWorldY = OriginY - spawnCellY;

        ActiveSource = new CustomMapSource(
            filename, name, raw.author ?? "", raw.width, raw.height,
            grid, visualGrid, decors,
            new UnityEngine.Vector2(spawnWorldX, spawnWorldY),
            visionRadius);

        errorMessage = null;
        return true;
    }

    // ── v2 パース (§13〜§16) ─────────────────────────────────────────────────

    private static bool TryParseV2(string json, EkmMapRaw raw, string filename, out string errorMessage)
    {
        // cells キー禁止 (§16)
        if (raw.cells != null)
        {
            errorMessage = "ekm:2 must not contain 'cells' key";
            return false;
        }

        // 共通フィールド検証 (name/author/width/height/spawn/ambient は v1 と同規則)
        string name = raw.name?.Trim() ?? "";
        if (name.Length < 1 || name.Length > 64)
        {
            errorMessage = $"name must be 1-64 characters (got {name.Length})";
            return false;
        }

        if (raw.author != null && raw.author.Length > 32)
        {
            errorMessage = $"author must be 0-32 characters (got {raw.author.Length})";
            return false;
        }

        if (raw.width < 1 || raw.width > 256)
        {
            errorMessage = $"width out of range: {raw.width} (must be 1-256)";
            return false;
        }

        if (raw.height < 1 || raw.height > 256)
        {
            errorMessage = $"height out of range: {raw.height} (must be 1-256)";
            return false;
        }

        // tileset 必須 (§14)
        if (raw.tilesetRaw == null)
        {
            errorMessage = "tileset is required for ekm:2";
            return false;
        }

        EkmTilesetRaw ts = raw.tilesetRaw;

        if (ts.tileSize < 8 || ts.tileSize > 128)
        {
            errorMessage = $"tileset.tileSize out of range: {ts.tileSize} (must be 8-128)";
            return false;
        }

        if (ts.columns < 1)
        {
            errorMessage = $"tileset.columns must be >= 1 (got {ts.columns})";
            return false;
        }

        if (string.IsNullOrEmpty(ts.image) || !ts.image.StartsWith("data:image/png;base64,"))
        {
            errorMessage = "tileset.image must be a data:image/png;base64, URI";
            return false;
        }

        string b64 = ts.image.Substring("data:image/png;base64,".Length);
        byte[] pngBytes;
        try { pngBytes = Convert.FromBase64String(b64); }
        catch (Exception ex)
        {
            errorMessage = $"tileset.image base64 decode error: {ex.Message}";
            return false;
        }

        if (pngBytes.Length > 1024 * 1024)
        {
            errorMessage = $"tileset.image decoded PNG too large ({pngBytes.Length} bytes > 1 MB)";
            return false;
        }

        // Texture2D ロード (画像寸法検証はロード後に行う — §A)
        // 注意: Il2CppStructArray はインデクサ 1 バイト代入だと LoadImage が false を返す (2026-06-11 実機)。
        // Utils.LoadTextureFromResources (Utils.cs:4811) と同じ「データ領域へのポインタ直書き」方式を使う
        // (オフセット IntPtr.Size*4 = Il2Cpp 配列オブジェクトヘッダ)。
        Texture2D tex = new(2, 2, TextureFormat.ARGB32, false);
        tex.filterMode = FilterMode.Point;
        var il2cppBytes = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(pngBytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(pngBytes, 0, IntPtr.Add(il2cppBytes.Pointer, IntPtr.Size * 4), pngBytes.Length);
        if (!tex.LoadImage(il2cppBytes, false))
        {
            UnityEngine.Object.Destroy(tex);
            errorMessage = "tileset.image could not be decoded as PNG";
            return false;
        }

        // 画像寸法検証 (§14)
        int expectedWidth = ts.columns * ts.tileSize;
        if (tex.width != expectedWidth)
        {
            UnityEngine.Object.Destroy(tex);
            errorMessage = $"tileset image width {tex.width} != columns({ts.columns}) * tileSize({ts.tileSize}) = {expectedWidth}";
            return false;
        }

        if (tex.height % ts.tileSize != 0)
        {
            UnityEngine.Object.Destroy(tex);
            errorMessage = $"tileset image height {tex.height} is not a multiple of tileSize({ts.tileSize})";
            return false;
        }

        int rows = tex.height / ts.tileSize;
        int tilecount = ts.columns * rows;
        if (tilecount > 4096)
        {
            UnityEngine.Object.Destroy(tex);
            errorMessage = $"tilecount {tilecount} > 4096";
            return false;
        }

        // per-tile 属性パース (§14.1)。疎配列: 未記載チップはデフォルト値
        var tileProps = new EkmTileProp[tilecount]; // デフォルト = pass:"o" over:false light:false
        for (int i = 0; i < tilecount; i++)
            tileProps[i] = new EkmTileProp { pass = "o", over = false, light = false, tag = 0, dir = 15 };

        if (ts.tiles != null)
        {
            var seenIds = new HashSet<int>();
            foreach (EkmTileRaw t in ts.tiles)
            {
                if (t == null) continue;
                if (t.id < 0 || t.id >= tilecount)
                {
                    errorMessage = $"tiles[].id={t.id} out of range (tilecount={tilecount})";
                    return false;
                }

                if (!seenIds.Add(t.id))
                {
                    errorMessage = $"tiles[].id={t.id} is duplicated";
                    return false;
                }

                if (t.pass is not ("o" or "x" or "v"))
                {
                    errorMessage = $"tiles[id={t.id}].pass '{t.pass}' is invalid (must be o/x/v)";
                    return false;
                }

                if (t.tag < 0 || t.tag > 99)
                {
                    errorMessage = $"tiles[id={t.id}].tag={t.tag} out of range (0-99)";
                    return false;
                }

                if (t.dir < 0 || t.dir > 15)
                {
                    errorMessage = $"tiles[id={t.id}].dir={t.dir} out of range (0-15)";
                    return false;
                }

                tileProps[t.id] = new EkmTileProp
                {
                    pass  = t.pass ?? "o",
                    over  = t.over,
                    light = t.light,
                    tag   = t.tag,
                    dir   = t.dir,
                };
            }
        }

        // layers 検証 (§13)
        if (raw.layersRaw == null || raw.layersRaw.ground == null)
        {
            errorMessage = "layers.ground is required for ekm:2";
            return false;
        }

        int expectedLen = raw.width * raw.height;
        if (raw.layersRaw.ground.Count != expectedLen)
        {
            errorMessage = $"layers.ground length {raw.layersRaw.ground.Count} != width*height ({expectedLen})";
            return false;
        }

        List<int> groundData = raw.layersRaw.ground;
        List<int> upperData  = raw.layersRaw.upper; // null = 全 -1

        if (upperData != null && upperData.Count != expectedLen)
        {
            errorMessage = $"layers.upper length {upperData.Count} != width*height ({expectedLen})";
            return false;
        }

        // ground/upper 値範囲チェック (§16)
        for (int i = 0; i < expectedLen; i++)
        {
            int g = groundData[i];
            if (g < -1 || g >= tilecount)
            {
                errorMessage = $"layers.ground[{i}]={g} out of range (-1 to {tilecount - 1})";
                return false;
            }

            if (upperData != null)
            {
                int u = upperData[i];
                if (u < -1 || u >= tilecount)
                {
                    errorMessage = $"layers.upper[{i}]={u} out of range (-1 to {tilecount - 1})";
                    return false;
                }
            }
        }

        // Sprite[] 構築 (§B: PNG 左上原点→Unity 左下原点の Y 反転)
        // Unity Sprite.Create の Rect は左下原点。tile (col, row) の左下 Unity Y = texHeight - (row+1)*tileSize
        int texH = tex.height;
        var sprites = new Sprite[tilecount];
        for (int tileId = 0; tileId < tilecount; tileId++)
        {
            int col = tileId % ts.columns;
            int row = tileId / ts.columns;
            float rx = col * ts.tileSize;
            float ry = texH - (row + 1) * ts.tileSize; // PNG 行 → Unity bottom-up Y
            sprites[tileId] = Sprite.Create(
                tex,
                new Rect(rx, ry, ts.tileSize, ts.tileSize),
                new Vector2(0.5f, 0.5f),
                ts.tileSize); // pixelsPerUnit = tileSize → 1 チップ = 1 ワールドユニット (§14)
        }

        // v2 per-cell 解決 (§15)
        var cells = new V2CellData[raw.width, raw.height];
        for (int gy = 0; gy < raw.height; gy++)
        for (int gx = 0; gx < raw.width; gx++)
        {
            int idx = gy * raw.width + gx;
            int gId = groundData[idx];
            int uId = upperData != null ? upperData[idx] : -1;

            EkmTileProp? gProp = gId >= 0 ? tileProps[gId] : (EkmTileProp?)null;
            EkmTileProp? uProp = uId >= 0 ? tileProps[uId] : (EkmTileProp?)null;

            // §15.1: void = G == -1
            bool isVoid = gId == -1;

            // §15.2: 実効通行 (pass)
            // U があり U.pass != "v" → U.pass、それ以外 → G.pass。void セルは "x" 扱い
            string effectivePass;
            if (isVoid)
                effectivePass = "x";
            else if (uProp.HasValue && uProp.Value.pass != "v")
                effectivePass = uProp.Value.pass;
            else
                effectivePass = gProp!.Value.pass; // gId >= 0 保証済

            bool isSolid = effectivePass == "x";

            // §15.3: 実効遮光 (light)
            bool blocksLight = isVoid || (gProp.HasValue && gProp.Value.light) || (uProp.HasValue && uProp.Value.light);

            cells[gx, gy] = new V2CellData
            {
                groundId   = gId,
                upperId    = uId,
                isSolid    = isSolid,
                isVoid     = isVoid,
                blocksLight = blocksLight,
                upperOver  = uProp.HasValue && uProp.Value.over,
            };
        }

        // spawn 検証 (§16: 実効通行可セル基準)
        if (raw.spawn == null)
        {
            errorMessage = "spawn is required";
            return false;
        }

        float spawnCellX = raw.spawn.x;
        float spawnCellY = raw.spawn.y;

        if (spawnCellX < 0 || spawnCellX >= raw.width || spawnCellY < 0 || spawnCellY >= raw.height)
        {
            errorMessage = $"spawn ({spawnCellX},{spawnCellY}) out of map range";
            return false;
        }

        var spawnCell = cells[(int)spawnCellX, (int)spawnCellY];
        if (spawnCell.isSolid || spawnCell.isVoid)
        {
            errorMessage = $"spawn ({spawnCellX},{spawnCellY}) is not a passable cell";
            return false;
        }

        // ambient
        float visionRadius = 8f;
        if (raw.ambient != null && raw.ambient.TryGetValue("visionRadius", out JsonElement vrElem))
        {
            if (vrElem.TryGetSingle(out float vr))
                visionRadius = Math.Clamp(vr, 4f, 8f);
        }

        // decor (v1 と同規則)
        var decors = new List<DecorEntry>();
        if (raw.decor != null)
        {
            foreach (var d in raw.decor)
            {
                if (d == null) continue;
                string kind = d.kind ?? "";
                if (!IsKnownDecorKind(kind))
                {
                    Logger.Warn($"[EkmapLoader] Unknown decor kind '{kind}', skipping", "EkmapLoader");
                    continue;
                }

                float dx = d.x, dy = d.y;
                if (dx < 0 || dx >= raw.width || dy < 0 || dy >= raw.height)
                {
                    Logger.Warn($"[EkmapLoader] decor '{kind}' at ({dx},{dy}) out of range, skipping", "EkmapLoader");
                    continue;
                }

                decors.Add(new DecorEntry(kind, dx, dy));
            }
        }

        float spawnWorldX = OriginX + spawnCellX;
        float spawnWorldY = OriginY - spawnCellY;

        var runtime = new EkmTilesetRuntime(tex, sprites, tileProps, ts.tileSize);

        ActiveSource = new CustomMapSource(
            filename, name, raw.author ?? "", raw.width, raw.height,
            null, null, // grid/visualGrid は v2 では不使用
            decors,
            new Vector2(spawnWorldX, spawnWorldY),
            visionRadius,
            cells,
            runtime);

        errorMessage = null;
        return true;
    }

    // ── 座標変換定数 (§4) ────────────────────────────────────────────────────
    // バニラロビーの船から離れた領域に配置する。
    // BackroomsLobby の procgen は player 位置中心に張るため、ここは固定オフセット。
    // バニラロビーの船は概ね (-7, -2) ~ (6, 4) の範囲。
    // player はロビー入室時に概ね (-0.2, 1.3) に居るが、EnterBackrooms は player を TP しないため、
    // カスタムマップも同じ player 位置のままタイルを展開する。
    // OriginX/OriginY = タイル(0,0) のワールド座標。
    // 16×16 マップの中心 (8,8) がプレイヤー周辺に来るよう origin を設定する。
    // spawn セルがワールド (0, 0) 付近に来るのが理想だが、spawn はファイルごとに違うため、
    // origin は「map 全体が概ねプレイヤー周囲に収まる」固定値とし、spawn へ TP して合わせる。
    public const float OriginX = -8f;
    public const float OriginY = 8f;

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    // 拡張子省略解決。優先順: UserEKMapsPath → EKMapsPath
    // 各パスで「そのまま / +.ekmap.json / +.json」の 3 候補を順に試す
    private static string ResolveFilename(string name)
    {
        // UserEKMapsPath を優先的に探す (ユーザーが編集中の最新を見せる)
        if (UserEKMapsPath != null)
        {
            string u1 = UserEKMapsPath + name;
            if (File.Exists(u1)) return u1;
            string u2 = UserEKMapsPath + name + ".ekmap.json";
            if (File.Exists(u2)) return u2;
            string u3 = UserEKMapsPath + name + ".json";
            if (File.Exists(u3)) return u3;
        }

        // 次に primary EKMapsPath を探す
        string p1 = EKMapsPath + name;
        if (File.Exists(p1)) return p1;
        string p2 = EKMapsPath + name + ".ekmap.json";
        if (File.Exists(p2)) return p2;
        string p3 = EKMapsPath + name + ".json";
        if (File.Exists(p3)) return p3;

        return null;
    }

    private static bool IsKnownDecorKind(string kind) =>
        kind is "light" or "stain" or "door" or "vent" or "ceiling";

    private static bool HasWallNeighbor(CellKindEkm[,] grid, int w, int h, int gx, int gy)
    {
        // 上下左右の 4 近傍。グリッド外は void と同扱い (= 非 Wall)
        return IsWall(grid, w, h, gx - 1, gy) ||
               IsWall(grid, w, h, gx + 1, gy) ||
               IsWall(grid, w, h, gx, gy - 1) ||
               IsWall(grid, w, h, gx, gy + 1);
    }

    private static bool IsWall(CellKindEkm[,] grid, int w, int h, int gx, int gy)
    {
        if (gx < 0 || gx >= w || gy < 0 || gy >= h) return false;
        return grid[gx, gy] == CellKindEkm.Wall;
    }

    // ── データ型 ─────────────────────────────────────────────────────────────

    public enum CellKindEkm { Floor, Wall, Void, Invalid }

    public enum VisualKind { Floor, WallH, WallV, Void }

    public record DecorEntry(string Kind, float X, float Y);

    // v2 per-cell 解決済みデータ (§15)
    public struct V2CellData
    {
        public int  groundId;   // -1 = void
        public int  upperId;    // -1 = なし
        public bool isSolid;    // 実効通行不可
        public bool isVoid;     // G == -1
        public bool blocksLight; // 実効遮光
        public bool upperOver;  // upper の over=true フラグ
    }

    // v2 タイルセット実行時データ (Texture2D + Sprite[] キャッシュ)
    public sealed class EkmTilesetRuntime
    {
        public readonly Texture2D  Texture;
        public readonly Sprite[]   Sprites;
        public readonly EkmTileProp[] TileProps;
        public readonly int        TileSize;

        public EkmTilesetRuntime(Texture2D tex, Sprite[] sprites, EkmTileProp[] tileProps, int tileSize)
        {
            Texture   = tex;
            Sprites   = sprites;
            TileProps = tileProps;
            TileSize  = tileSize;
        }

        // Texture/Sprite リソース解放 (ExitBackrooms / OnGameStart から呼ぶ)
        public void Destroy()
        {
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            // Sprites は Texture を参照するだけなので Texture 破棄で連鎖して無効化される
        }
    }

    // v2 per-tile 属性 (§14.1)
    public struct EkmTileProp
    {
        public string pass;   // "o" / "x" / "v"
        public bool   over;
        public bool   light;
        public int    tag;
        public int    dir;
    }

    // JSON デシリアライズ用 (System.Text.Json POCO)
    private sealed class EkmMapRaw
    {
        public int               ekm    { get; set; }
        public string            name   { get; set; }
        public string            author { get; set; }
        public int               width  { get; set; }
        public int               height { get; set; }
        public List<string>      cells  { get; set; }
        public List<EkmDecorRaw> decor  { get; set; }
        public EkmSpawnRaw       spawn  { get; set; }
        // ambient は任意キー混在なので Dictionary で受ける
        public Dictionary<string, JsonElement> ambient { get; set; }
        // v2 フィールド
        [JsonPropertyName("tileset")]
        public EkmTilesetRaw tilesetRaw { get; set; }
        [JsonPropertyName("layers")]
        public EkmLayersRaw  layersRaw  { get; set; }
    }

    private sealed class EkmTilesetRaw
    {
        public int              tileSize { get; set; }
        public int              columns  { get; set; }
        public string           image    { get; set; }
        public List<EkmTileRaw> tiles    { get; set; }
    }

    private sealed class EkmTileRaw
    {
        public int    id    { get; set; }
        public string pass  { get; set; } = "o";
        public bool   over  { get; set; }
        public bool   light { get; set; }
        public int    tag   { get; set; }
        public int    dir   { get; set; } = 15;
    }

    private sealed class EkmLayersRaw
    {
        public List<int> ground { get; set; }
        public List<int> upper  { get; set; }
    }

    private sealed class EkmDecorRaw
    {
        public string kind { get; set; }
        public float  x    { get; set; }
        public float  y    { get; set; }
    }

    private sealed class EkmSpawnRaw
    {
        public float x { get; set; }
        public float y { get; set; }
    }

    // ── golden サンプル (§10) ────────────────────────────────────────────────

    private const string GoldenSample = @"{
  ""ekm"": 1,
  ""name"": ""Sample Rooms"",
  ""author"": ""End K not"",
  ""width"": 16,
  ""height"": 16,
  ""cells"": [
    ""################"",
    ""#......#.......#"",
    ""#......#...#...#"",
    ""#..##..#...#...#"",
    ""#......#...#...#"",
    ""#......#...##..#"",
    ""###.#######....#"",
    ""#..............#"",
    ""#....#....#....#"",
    ""#....#....#....#"",
    ""##.###....###.##"",
    ""#......##......#"",
    ""#......##......#"",
    ""#..#........#..#"",
    ""#..............#"",
    ""################""
  ],
  ""decor"": [
    { ""kind"": ""light"",  ""x"": 3,  ""y"": 2  },
    { ""kind"": ""light"",  ""x"": 9,  ""y"": 3  },
    { ""kind"": ""light"",  ""x"": 7,  ""y"": 7  },
    { ""kind"": ""light"",  ""x"": 4,  ""y"": 12 },
    { ""kind"": ""light"",  ""x"": 11, ""y"": 12 },
    { ""kind"": ""stain"",  ""x"": 2,  ""y"": 8  },
    { ""kind"": ""stain"",  ""x"": 13, ""y"": 5  },
    { ""kind"": ""vent"",   ""x"": 14, ""y"": 14 }
  ],
  ""spawn"": { ""x"": 7, ""y"": 7 },
  ""ambient"": { ""visionRadius"": 8 }
}
";
}

// カスタムマップのグリッド保持クラス。BackroomsLobby.ClassifyCell の委譲先
public sealed class CustomMapSource
{
    public readonly string                           Filename;
    public readonly string                           Name;
    public readonly string                           Author;
    public readonly int                              Width;
    public readonly int                              Height;
    // v1 専用 (v2 では null)
    public readonly EkmapLoader.CellKindEkm[,]      Grid;
    public readonly EkmapLoader.VisualKind[,]        VisualGrid;
    public readonly List<EkmapLoader.DecorEntry>     Decors;
    public readonly UnityEngine.Vector2              SpawnWorld;
    public readonly float                            VisionRadius;
    // v2 専用 (v1 では null)
    public readonly EkmapLoader.V2CellData[,]        V2Cells;
    public readonly EkmapLoader.EkmTilesetRuntime    Tileset;

    // v2 か否か
    public bool IsV2 => V2Cells != null;

    // v1 コンストラクタ (既存互換)
    public CustomMapSource(
        string filename, string name, string author,
        int width, int height,
        EkmapLoader.CellKindEkm[,] grid,
        EkmapLoader.VisualKind[,] visualGrid,
        List<EkmapLoader.DecorEntry> decors,
        UnityEngine.Vector2 spawnWorld,
        float visionRadius)
    {
        Filename     = filename;
        Name         = name;
        Author       = author;
        Width        = width;
        Height       = height;
        Grid         = grid;
        VisualGrid   = visualGrid;
        Decors       = decors;
        SpawnWorld   = spawnWorld;
        VisionRadius = visionRadius;
        V2Cells      = null;
        Tileset      = null;
    }

    // v2 コンストラクタ
    public CustomMapSource(
        string filename, string name, string author,
        int width, int height,
        EkmapLoader.CellKindEkm[,] grid,
        EkmapLoader.VisualKind[,] visualGrid,
        List<EkmapLoader.DecorEntry> decors,
        UnityEngine.Vector2 spawnWorld,
        float visionRadius,
        EkmapLoader.V2CellData[,] v2Cells,
        EkmapLoader.EkmTilesetRuntime tileset)
    {
        Filename     = filename;
        Name         = name;
        Author       = author;
        Width        = width;
        Height       = height;
        Grid         = grid;
        VisualGrid   = visualGrid;
        Decors       = decors;
        SpawnWorld   = spawnWorld;
        VisionRadius = visionRadius;
        V2Cells      = v2Cells;
        Tileset      = tileset;
    }

    // ワールド座標 → セル座標逆変換 (§4: world = origin + (x, -y))
    // wx = OriginX + cellX  →  cellX = wx - OriginX
    // wy = OriginY - cellY  →  cellY = OriginY - wy
    // v2 では solid/void を §15 解決済み V2CellData から返す
    public EkmapLoader.VisualKind GetCell(float wx, float wy)
    {
        int gx = (int)Math.Round(wx - EkmapLoader.OriginX);
        int gy = (int)Math.Round(EkmapLoader.OriginY - wy);
        if (gx < 0 || gx >= Width || gy < 0 || gy >= Height)
            return EkmapLoader.VisualKind.Void;

        if (IsV2)
        {
            var c = V2Cells[gx, gy];
            if (c.isVoid)   return EkmapLoader.VisualKind.Void;
            if (c.isSolid)  return EkmapLoader.VisualKind.WallH; // solid = 通行不可壁 (WallH として扱う)
            return EkmapLoader.VisualKind.Floor;
        }

        return VisualGrid[gx, gy];
    }

    // v2 セル直接アクセス (BackroomsLobby v2 描画ブランチ用)
    public bool TryGetV2Cell(int gx, int gy, out EkmapLoader.V2CellData cell)
    {
        if (!IsV2 || gx < 0 || gx >= Width || gy < 0 || gy >= Height)
        {
            cell = default;
            return false;
        }

        cell = V2Cells[gx, gy];
        return true;
    }

    // セル座標 → ワールド座標 (中心)
    public UnityEngine.Vector2 CellToWorld(int gx, int gy) =>
        new(EkmapLoader.OriginX + gx, EkmapLoader.OriginY - gy);

    // v2 タイルセットリソース解放 (ExitBackrooms / OnGameStart から呼ぶ)
    public void DestroyV2Tileset() => Tileset?.Destroy();
}
