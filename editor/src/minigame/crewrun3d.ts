// =============================================================
//  Crew Run 3D — スタート画面のおまけミニゲーム (本物の 3D 版)
//  three.js でカメラ・ライト・メッシュを使い、広告 (King's Return 系) の
//  「板張りの桟橋を群衆が走り、ゲートで増減 → 雑魚 → ボス」を本物の遠近で再現。
//  ゲートの数式・ステージ制などのロジックは 2D 版と共通の考え方を踏襲。
//  基本は手続き的プリミティブで組むが、bacteria のみ FBX モデルを使用 (内蔵アニメ付き)。
//  HUD/リザルトは DOM オーバーレイ。閉じる時は dispose() で three を完全破棄。
// =============================================================

import * as THREE from "three";
import { mergeGeometries, mergeVertices } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import { SimplifyModifier } from "three/examples/jsm/modifiers/SimplifyModifier.js";
import { MarchingCubes } from "three/examples/jsm/objects/MarchingCubes.js";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";
import { InstancedMesh2 } from "@three.ez/instanced-mesh";
import { FBXLoader } from "three/examples/jsm/loaders/FBXLoader.js";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import { clone as cloneSkeleton } from "three/examples/jsm/utils/SkeletonUtils.js";
import bacteriaUrl from "./assets/Bacteria.fbx?url";
import levelAmbientUrl from "./assets/level-ambient.mp3?url"; // Level! のアンビエント (ポケットサウンド素材を基にユーザー作成)
import { applyGate, type GateOp, gateLabel, MAX_CREW } from "./gate";

// ---------- bacteria FBX モデル (遅延・シングルトンキャッシュ) ----------
// 一度だけロード/正規化し、SkeletonUtils.clone で各個体へ複製する。
// 失敗してもゲームを壊さないよう catch して null をキャッシュ → 手続きフォールバック。
let bacteriaModel: THREE.Group | null = null; // 正規化済みの元モデル
let bacteriaLoadStarted = false;
let bacteriaLoadPromise: Promise<THREE.Group | null> | null = null;
function loadBacteriaModel(): Promise<THREE.Group | null> {
    if (bacteriaLoadStarted) return bacteriaLoadPromise!;
    bacteriaLoadStarted = true;
    bacteriaLoadPromise = new Promise<THREE.Group | null>((resolve) => {
        try {
            new FBXLoader().load(
                bacteriaUrl,
                (grp: THREE.Group) => {
                    try {
                        // 不気味な肉色マテリアルに統一上書き (同梱されない .jpg 参照を捨てる)
                        // 暗い赤の Backrooms 廊下で埋もれないよう、明るめの肉色 + 強めの自己発光で「光る肉塊」に。
                        const fleshMat = new THREE.MeshLambertMaterial({ color: 0xd8bcae, emissive: 0x7a4a42 });
                        // --- 軽量化: 20 メッシュ(約2万頂点)を 1 メッシュへ統合し、頂点を間引く ---
                        // (Level ! で多数湧くと重いので、ドローコール 20→1 + 頂点 ~80% 削減)
                        grp.updateMatrixWorld(true);
                        const geos: THREE.BufferGeometry[] = [];
                        grp.traverse((c) => {
                            const m = c as THREE.Mesh;
                            if (m.isMesh && m.geometry) {
                                // position だけ抜き、メッシュのワールド変換を焼き込んでから非インデックス化
                                const src = m.geometry;
                                const g2 = new THREE.BufferGeometry();
                                g2.setAttribute("position", (src.getAttribute("position") as THREE.BufferAttribute).clone());
                                if (src.index) g2.setIndex(src.index.clone());
                                g2.applyMatrix4(m.matrixWorld);
                                geos.push(g2.toNonIndexed());
                            }
                        });
                        let merged = geos.length > 1 ? mergeGeometries(geos, false) : geos[0];
                        if (merged) {
                            // 非インデックスの「頂点スープ」を溶接 → 共有頂点のインデックス付きに。
                            // これで重複頂点が潰れ (溶接だけで大幅減) 、辺の畳み込み (簡略化) も効くようになる。
                            try { merged = mergeVertices(merged, 1e-3); } catch { /* 溶接失敗は無視 */ }
                            const vcount = merged.getAttribute("position").count;
                            try {
                                // 溶接後の頂点をさらに 75% 削減 (modify の第2引数は「除去する頂点数」)
                                const simplified = new SimplifyModifier().modify(merged, Math.floor(vcount * 0.75));
                                if (simplified && simplified.getAttribute("position").count > 0) merged = simplified;
                            } catch { /* 簡略化に失敗しても統合済みジオメトリで続行 */ }
                            merged.computeVertexNormals();
                        }
                        // grp の中身を 1 メッシュへ置換
                        for (let i = grp.children.length - 1; i >= 0; i--) grp.remove(grp.children[i]);
                        if (merged) {
                            const solo = new THREE.Mesh(merged, fleshMat);
                            solo.castShadow = false;
                            solo.receiveShadow = false;
                            grp.add(solo);
                        }
                        // 高さが約 2.2 ユニットになるよう uniform スケール + 足元を y=0 へ接地
                        const box = new THREE.Box3().setFromObject(grp);
                        const size = new THREE.Vector3();
                        box.getSize(size);
                        const h = size.y || 1;
                        const s = 2.2 / h;
                        grp.scale.multiplyScalar(s);
                        const box2 = new THREE.Box3().setFromObject(grp);
                        const ctr = new THREE.Vector3();
                        box2.getCenter(ctr);
                        // x/z を中央寄せ・足元 (box2.min.y) を y=0 に合わせる
                        grp.position.x -= ctr.x;
                        grp.position.z -= ctr.z;
                        grp.position.y -= box2.min.y;
                        // プレイヤー (手前 = -Z) を向くよう正面合わせ (FBX の素の向きが不定なため 180°回す)
                        grp.rotation.y = Math.PI;
                        bacteriaModel = grp;
                        resolve(grp);
                    } catch {
                        bacteriaModel = null;
                        resolve(null);
                    }
                },
                undefined,
                () => { bacteriaModel = null; resolve(null); },
            );
        } catch {
            bacteriaModel = null;
            resolve(null);
        }
    });
    return bacteriaLoadPromise;
}

// ---------- partygoer GLTF モデル (遅延・シングルトンキャッシュ) ----------
// Backrooms「Level Fun」のパーティゴアー (陽気に踊って近づき、突然横へ掴みかかる群れ)。
// partygoer model by wilderberry5150 (CC-BY 4.0) https://skfb.ly/oJuCJ
// bacteria model: Roman Trace (出典は同梱メタ参照)。
// 静的メッシュ (アニメ/スキン無し) なので mixer は不要。テクスチャ付きマテリアルはそのまま維持する。
let partygoerModel: THREE.Group | null = null; // 正規化済みの元モデル
let partygoerLoadStarted = false;
let partygoerLoadPromise: Promise<THREE.Group | null> | null = null;
function loadPartygoerModel(): Promise<THREE.Group | null> {
    if (partygoerLoadStarted) return partygoerLoadPromise!;
    partygoerLoadStarted = true;
    partygoerLoadPromise = new Promise<THREE.Group | null>((resolve) => {
        try {
            // public は base 相対で配信される (base="./")。bin/textures は相対 URI で自動ロードされる。
            const url = import.meta.env.BASE_URL + "partygoer/scene.gltf";
            new GLTFLoader().load(
                url,
                (gltf) => {
                    try {
                        const grp = gltf.scene;
                        grp.traverse((c) => {
                            const m = c as THREE.Mesh;
                            if (m.isMesh) { m.castShadow = false; m.receiveShadow = false; }
                        });
                        // 高さが約 2.2 ユニットになるよう uniform スケール + 足元を y=0 へ接地
                        const box = new THREE.Box3().setFromObject(grp);
                        const size = new THREE.Vector3();
                        box.getSize(size);
                        const h = size.y || 1;
                        const s = 2.2 / h;
                        grp.scale.multiplyScalar(s);
                        const box2 = new THREE.Box3().setFromObject(grp);
                        const ctr = new THREE.Vector3();
                        box2.getCenter(ctr);
                        // x/z を中央寄せ・足元 (box2.min.y) を y=0 に合わせる
                        grp.position.x -= ctr.x;
                        grp.position.z -= ctr.z;
                        grp.position.y -= box2.min.y;
                        // プレイヤー (手前 = -Z) を向くよう正面合わせ (素の向きが不定なため 180°回す)
                        grp.rotation.y = Math.PI;
                        partygoerModel = grp;
                        resolve(grp);
                    } catch {
                        partygoerModel = null;
                        resolve(null);
                    }
                },
                undefined,
                () => { partygoerModel = null; resolve(null); },
            );
        } catch {
            partygoerModel = null;
            resolve(null);
        }
    });
    return partygoerLoadPromise;
}

// ---------- スコア ----------
const BEST_KEY = "ekm_crewrun_best";
function loadBest(): number {
    const v = Number(localStorage.getItem(BEST_KEY) ?? "0");
    return Number.isFinite(v) ? v : 0;
}
function saveBest(score: number): number {
    const best = Math.max(loadBest(), score);
    localStorage.setItem(BEST_KEY, String(best));
    return best;
}

// ---------- 装備テーマアーマー ----------
// クルーが装甲を着る装備システム。既存の数値アーマーLv(0-5の％軽減)に「装備レイヤー」を上乗せするハイブリッド方式。
// 種類ごとに見た目(プレート色/発光/オーラ)＋固有効果。解放は localStorage で永続(ボス撃破/Level!生還/レアドロップ等)。
type ArmorAura = "none" | "fire" | "dark" | "gold";
interface ArmorDef {
    id: string;
    name: string;
    icon: string;
    body: number; // 素体(全身)を塗り替える装甲の色 — スキンメッシュごと装甲化し、動きに完全追従させる
    metal: number; // 素体マテリアルの metalness (金属感)
    rough: number; // 素体マテリアルの roughness (艶)
    plate: number; // 上乗せ装甲プレートの基調色
    accent: number; // プレートのハイライト/縁の色
    emissive: number; // 自己発光 (炎/闇のグロー・素体とプレート共通)
    aura: ArmorAura; // 周囲に出すオーラ粒子
    extraArmor: number; // 数値アーマーLvに上乗せする実効アーマー
    firePower: number; // 火力倍率 (1=等倍)
    drainFrac: number; // 装備中に毎秒失うクルー割合 (闇のトレードオフ)
    burn: number; // 前方近接の敵を毎秒焼くダメージ係数 (炎)
    desc: string; // 効果の説明 (ショップUIに表示)
    unlockHint: string; // 未解放時にUIへ出す解放条件
}
const ARMORS: ArmorDef[] = [
    { id: "none", name: "標準アーマー", icon: "🛡", body: 0x6fa6c8, metal: 0.45, rough: 0.5, plate: 0x9fc2dd, accent: 0xe2f2ff, emissive: 0x000000, aura: "none", extraArmor: 0, firePower: 1, drainFrac: 0, burn: 0, desc: "基本の装甲。クセのないバランス型。", unlockHint: "" },
    { id: "tech", name: "テックアーマー", icon: "🤖", body: 0x8a9bb0, metal: 0.9, rough: 0.25, plate: 0x4c5d72, accent: 0x46e0ff, emissive: 0x0a2838, aura: "none", extraArmor: 2, firePower: 1, drainFrac: 0, burn: 0, desc: "重装甲メカ。被弾を大きく軽減 (実効アーマー +2)。", unlockHint: "ステージ5クリアで解放" },
    { id: "blaze", name: "燃えさかるアーマー", icon: "🔥", body: 0xc23e14, metal: 0.5, rough: 0.4, plate: 0xff5a1e, accent: 0xffe24b, emissive: 0xff3400, aura: "fire", extraArmor: 1, firePower: 1.15, drainFrac: 0, burn: 1.4, desc: "前方の敵を炎で焼き続ける＋火力×1.15。攻めの装甲。", unlockHint: "終盤のドラゴン(2巡目)撃破で解放" },
    { id: "dark", name: "闇のアーマー", icon: "🌑", body: 0x241a3a, metal: 0.62, rough: 0.34, plate: 0x352552, accent: 0xb060ff, emissive: 0x6a1ff0, aura: "dark", extraArmor: 3, firePower: 1.3, drainFrac: 0.016, burn: 0, desc: "防御+3・火力×1.3の最強格。だが装備中クルーが毎秒溶けて減る。ハイリスク・終盤も通用。", unlockHint: "Level! を生還で解放" },
    { id: "aegis", name: "黄金のイージス", icon: "⭐", body: 0xffcf4a, metal: 0.95, rough: 0.2, plate: 0xffd75e, accent: 0xfff4c0, emissive: 0x6a4d00, aura: "gold", extraArmor: 2, firePower: 1.15, drainFrac: 0, burn: 0, desc: "万能の黄金装甲。防御+2・火力×1.15、欠点なし。終盤のご褒美。", unlockHint: "終盤(S8+)の激レアドロップで入手" },
    // パーティゴアアーマー: 見た目がパーティゴアに変身・世界が白黒・パーティゴアが寝返る。だが超低耐久＆援軍なしのベテラン専用。
    { id: "party", name: "パーティゴアアーマー", icon: "🎭", body: 0xc8c8c8, metal: 0.2, rough: 0.7, plate: 0x999999, accent: 0xcccccc, emissive: 0x000000, aura: "none", extraArmor: -5, firePower: 1, drainFrac: 0, burn: 0, desc: "パーティゴアに変身。世界が白黒になり、彼らは襲わず逆に仲間に加わる。だが耐久ペラペラでロボ/タレット等の援軍も付かない。ベテラン向け。", unlockHint: "最終盤(S10+)の激レアドロップで入手" },
];
function armorById(id: string): ArmorDef { return ARMORS.find((a) => a.id === id) ?? ARMORS[0]; }
const ARMOR_UNLOCKS_KEY = "ekm_crewrun_armors";
const ARMOR_EQUIP_KEY = "ekm_crewrun_armor_equip";
function loadArmorUnlocks(): Set<string> {
    try { const a = JSON.parse(localStorage.getItem(ARMOR_UNLOCKS_KEY) ?? "[]"); return new Set(Array.isArray(a) ? a : []); } catch { return new Set(); }
}
function saveArmorUnlocks(s: Set<string>): void { localStorage.setItem(ARMOR_UNLOCKS_KEY, JSON.stringify([...s])); }
function loadArmorEquip(): string { return localStorage.getItem(ARMOR_EQUIP_KEY) ?? "none"; }
function saveArmorEquip(id: string): void { localStorage.setItem(ARMOR_EQUIP_KEY, id); }

/**
 * クルーが着る装甲プレート (騎士のシルエット: 兜＋キュイラス＋肩当て＋腰当て)。
 * 素体は別途マテリアルで装甲化するので、ここは「低可動部 (胴・肩・頭)」だけに固める＝走っても浮かない。
 * 手足は素体の金属化で表現 (剛体プレートを四肢に付けると振りに追従できず分離するため付けない)。
 */
function buildWornArmorGeometry(def: ArmorDef): THREE.BufferGeometry {
    const parts: THREE.BufferGeometry[] = [];
    const P = def.plate, A = def.accent;
    // 胴 (キュイラス): 前後に厚い胸甲 + 中央リッジ + 鎖骨ガード
    const cuir = new THREE.BoxGeometry(0.54, 0.54, 0.42); cuir.translate(0, 0.92, 0); parts.push(paint(cuir, P));
    const collar = new THREE.BoxGeometry(0.34, 0.16, 0.44); collar.translate(0, 1.12, 0); parts.push(paint(collar, A));
    const ridge = new THREE.BoxGeometry(0.08, 0.42, 0.08); ridge.translate(0, 0.92, 0.23); parts.push(paint(ridge, A));
    // 肩当て (パルドロン): 左右の大きな丸み + 縁取り
    for (const sx of [-1, 1]) {
        const pad = new THREE.BoxGeometry(0.3, 0.28, 0.42); pad.translate(sx * 0.35, 1.16, 0); parts.push(paint(pad, P));
        const trim = new THREE.BoxGeometry(0.33, 0.07, 0.45); trim.translate(sx * 0.35, 1.03, 0); parts.push(paint(trim, A));
    }
    // 腰当て (タセット)
    const skirt = new THREE.BoxGeometry(0.52, 0.22, 0.38); skirt.translate(0, 0.62, 0); parts.push(paint(skirt, P));
    // 兜 (ヘルム): 頭を覆うドーム + バイザースリット
    const helm = new THREE.BoxGeometry(0.38, 0.32, 0.38); helm.translate(0, 1.42, 0); parts.push(paint(helm, P));
    const slit = new THREE.BoxGeometry(0.32, 0.05, 0.06); slit.translate(0, 1.4, 0.19); parts.push(paint(slit, A));

    // --- 装備ごとの個性パーツ (シルエットで一目で分かるように派手めに) ---
    const spike = (w: number, h: number, x: number, y: number, z: number, rotZ: number, col: number): void => {
        const s = new THREE.BoxGeometry(w, h, w); s.rotateZ(rotZ); s.translate(x, y, z); parts.push(paint(s, col));
    };
    switch (def.id) {
        case "none": { // 控えめなトサカ
            const crest = new THREE.BoxGeometry(0.07, 0.18, 0.34); crest.translate(0, 1.62, 0); parts.push(paint(crest, A));
            break;
        }
        case "tech": { // アンテナ2本 + 胸の発光コア + 角ばった肩スパイク
            for (const sx of [-1, 1]) { const ant = new THREE.BoxGeometry(0.03, 0.34, 0.03); ant.translate(sx * 0.12, 1.72, -0.05); parts.push(paint(ant, A)); }
            const core = new THREE.BoxGeometry(0.16, 0.16, 0.06); core.translate(0, 0.96, 0.24); parts.push(paint(core, A));
            for (const sx of [-1, 1]) spike(0.12, 0.26, sx * 0.5, 1.22, 0, sx * -0.5, P);
            break;
        }
        case "blaze": { // 炎の角(斜め上のスパイク3本) + 肩の炎
            spike(0.1, 0.4, 0, 1.78, 0, 0, A);
            for (const sx of [-1, 1]) { spike(0.09, 0.34, sx * 0.16, 1.74, 0, sx * 0.5, A); spike(0.1, 0.3, sx * 0.46, 1.34, 0, sx * 0.35, A); }
            break;
        }
        case "dark": { // 禍々しい湾曲ホーン(左右) + 棘の肩
            for (const sx of [-1, 1]) { spike(0.11, 0.4, sx * 0.18, 1.74, 0, sx * 0.7, A); spike(0.12, 0.28, sx * 0.52, 1.3, 0, sx * 0.6, P); }
            const thorn = new THREE.BoxGeometry(0.08, 0.3, 0.08); thorn.translate(0, 1.76, -0.08); parts.push(paint(thorn, A));
            break;
        }
        case "aegis": { // 王冠(3つの尖り) + 肩のウィング
            for (const dx of [-0.13, 0, 0.13]) { const p = new THREE.BoxGeometry(0.07, dx === 0 ? 0.26 : 0.18, 0.07); p.translate(dx, 1.66, 0); parts.push(paint(p, A)); }
            for (const sx of [-1, 1]) { const wing = new THREE.BoxGeometry(0.06, 0.18, 0.5); wing.rotateX(0.3); wing.translate(sx * 0.48, 1.24, -0.12); parts.push(paint(wing, A)); }
            break;
        }
    }
    return mergeGeometries(parts, false)!;
}

// ---------- チューニング ----------
const LANE = 4.4; // 桟橋の半幅
const CROWD_Z = 0; // 群衆の前線 z
const EVENT_CAM_Y = 2.8; // Backrooms 廊下イベント中のカメラ高さ (天井 y≈3.9 の下＝廊下の中に居る視点)
// 歪んだ館内放送 (Backrooms イベント中に途切れ途切れで流れる)
const PA_MESSAGES = [
    "…コードブルー… 全フロア… 至急…",
    "…避難… して… くださ……",
    "…誰か… そこに… いますか…",
    "…03… 02… 01… オールクリア… ……嘘です",
    "…手術室は… もう… ありません…",
    "…ピ──────（フラットライン）",
    "…戻って… こないで…",
];
const SPAWN_Z = 78; // エンティティの出現 z (奥)
const PIER_TOP = 0.6; // 桟橋の上面 y
const MAX_INSTANCES = 300; // クルー描画上限
const START_CREW = 6;
const BASE_SPEED = 13; // z 単位/秒 (奥→手前)
const BOSS_DIST = 260; // この距離でボス戦 (走行 ~20 秒目安・装備を整えてから戦える)
const FIRE_FACTOR = 0.25; // 火力 → ダメージ毎秒の係数 (低いほど敵が固い)
const FIRE_BUFF_MULT = 2.0; // G11 火力テンプレ中の firepower 倍率
const FIRE_BUFF_TIME = 7; // G11 火力バフの持続秒
const CEIL_H = 4.5; // D 重力反転中にクルーを持ち上げる天井高さ (視覚のみ・当たり判定は不変)
const CREW_GAP = 0.42; // 隊列の個体間隔 (小さいほどギュッと密集して当たり判定が読める)
const ARMOR_MAX = 5; // アーマー上限 (溜まりすぎてハザードが無意味化するのを防ぐ)
// クリーン走行のクルー微回復 (耐性の床): 被弾せず走り続けると、失ったクルーを crewPeak 上限まで少しずつ取り戻す。
// 1 回の大被弾からの雪崩死を防ぎ、立て直しを可能にする (盾以外の生存ルート)。
const REGEN_DELAY = 3.0; // 最後の被弾からこの秒数を超えると回復開始 (被弾で止まる)
const REGEN_BASE = 1.0; // 毎秒の基本回復 (人/s)
const REGEN_FRAC = 0.015; // 現クルー比例の追加回復/秒 (大群でも体感できる)

// ---- パーティゴアー (Level Fun) 勧誘チューニング ----
// 追わずに前線で踊り、クルーを 1 人ずつ連れ去ってコンガの列に加える。撃てば取り返せる。
// 「放置すると痛いが撃てば十分取り返せる」バランス (数値は後で調整しやすいよう定数化)。
const PARTY_RECRUIT_CD = 0.7; // クルーを連れ去る間隔 (秒) — まとめて連れ去るので少し短め
const PARTY_RECRUIT_MAX = 24; // 1 体が連れ去れる上限 (倒すまで列はどんどん伸びる)
const PARTY_JOIN_CREW = 8; // パーティゴアアーマー装備中、1 体が寝返るたびに増える味方クルー数
const PARTY_CONGA_GAP = 1.1; // コンガ figure の前後間隔 (本体軌跡上の距離)
const PARTY_HOLD_Z = CROWD_Z + 16; // 前線のずっと手前でこの z に居座る (プレイヤーには近寄らない)

// ---- ボス戦オーバーホール (脱・二択) ----
const MIN_FIGHT = 5.0; // ボス HP を無抵抗 DPS で溶かしても最低この秒数は戦う (瞬殺防止の構造的な底)
const DPS_SOFT_K = 1.0; // ソフトキャップ超過分の対数圧縮の強さ
const CHIP_FRAC_PER_S = 0.05; // 確定チップが現クルーから奪える上限割合/秒 (一撃死を不可能に)
const CHIP_BASE = 1.2; // 確定チップの最低値 (小群でも手応えがある)
const HEAVY_BASE = 0.05; // 回避可能なヘビーのクルー係数
const HEAVY_FLAT = 2.5; // 回避可能なヘビーの定数項
const BOSS_ARROW_SPEED = 30; // ボス弾の固定速度 u/s (ステージ加速の影響を受けない一定テレグラフ)
const TELE_LEAD = 0.55; // 床マーカーがヘビー着弾の何秒前に光るか
const MIN_SAFE_GAP = 1.4; // どのパターンも必ずこの幅以上の安全レーンを残す
const BOSS_ARMOR_K = 0.5; // ボス命中のみ armor 軽減 lossMult = 1/(1+armor*K)
const ARMOR_RUN_K = 0.45; // 走行ハザードの armor 軽減 lossMult = 1/(1+armor*K)。上げると走行被弾が痛くなる
// Level 0 (黄色ロビー) の曲がる迷路: 行き止まり壁で横へ曲がらされる。通過時にカメラを大きくスイング。
const LEVEL0_BIOME = 9; // 黄色ロビーのバイオーム index (BIOMES/GIMMICK_SKIN の 9 番)
const TURN_SWING_TIME = 0.7; // ターン壁通過時のカメラ・スイング時間 (秒)
const TURN_SWING_MAX = 1.15; // スイングの最大ヨー角 (rad ≈ 66°)。"曲がった" 体感を出す

const C_ALLY = { body: 0x4fb4e2, dark: 0x2f8fc4, visor: 0x86d4f2 }; // 水色のお人形 (visor=明るい頭の色)
const C_ENEMY = { body: 0xe0524f, dark: 0xb53c39, visor: 0xf6c0be };
const C_ARCHER = { body: 0x2f9c57, dark: 0x215a39, visor: 0xe8c9a0 };

// --- biome scenery ---
const SIDE_NEAR = LANE + 2.0; // props start just outside the fences (collision-clear)
const SIDE_DEPTH = 26; // props fan out to ~|x|=32, density thins toward fog
const RECYCLE_NEAR = -8; // matches entity removal threshold (e.z < -8)
const RECYCLE_FAR = 120; // inside fog (60..150) so wrapped props fade in, never pop
const RECYCLE_BAND = RECYCLE_FAR - RECYCLE_NEAR; // 128 — add this on wrap (no drift)
const PROP_Y = -0.05; // ground height (same as grass plane)
const POOL_A = 64; // big upright (tree/cactus/pine/palm-mast)
const POOL_B = 96; // ground clutter (bush/rock/snowmound/buoy-rock)
const POOL_C = 28; // tall sparse landmark (log/mesa/iceberg/island)
// 188 total prop instances, FIXED forever (never scales with crew/stage)

// 0=forest 1=desert 2=snow 3=sea 4=volcano 5=graveyard 6=sky
const BIOMES = [
    { sky: 0x9ad7ef, fogCol: 0xcfe8c4, fogNear: 60, fogFar: 150,
      ground: 0x6fb43e, hemiSky: 0xeaf7ff, hemiGround: 0x5a8a3a, dirCol: 0xfff2d8, dirInt: 1.15 }, // 0 FOREST
    { sky: 0xf4d79c, fogCol: 0xe8d2a0, fogNear: 45, fogFar: 130,
      ground: 0xddb46a, hemiSky: 0xfff0d0, hemiGround: 0xc79a55, dirCol: 0xffe7b0, dirInt: 1.35 }, // 1 DESERT
    { sky: 0xcfe4f2, fogCol: 0xdde9f2, fogNear: 38, fogFar: 120,
      ground: 0xeaf2f8, hemiSky: 0xf2f8ff, hemiGround: 0xb9cad8, dirCol: 0xeef4ff, dirInt: 1.0 }, // 2 SNOW
    { sky: 0x7fc6e8, fogCol: 0xbfe2ef, fogNear: 55, fogFar: 150,
      ground: 0x2f8fc4, hemiSky: 0xdff4ff, hemiGround: 0x2f6f9a, dirCol: 0xfff4d8, dirInt: 1.2 }, // 3 SEA
    { sky: 0x3a2030, fogCol: 0x6e382a, fogNear: 38, fogFar: 118,
      ground: 0x4a2f2a, hemiSky: 0xffc6a0, hemiGround: 0x5a2820, dirCol: 0xffb070, dirInt: 1.1 }, // 4 VOLCANO
    { sky: 0x161f38, fogCol: 0x2a3450, fogNear: 34, fogFar: 108,
      ground: 0x36405a, hemiSky: 0x8aa0d0, hemiGround: 0x1e2636, dirCol: 0xb0c0e0, dirInt: 0.72 }, // 5 GRAVEYARD (night)
    { sky: 0xbfe6ff, fogCol: 0xeaf6ff, fogNear: 55, fogFar: 155,
      ground: 0xeaf2ff, hemiSky: 0xffffff, hemiGround: 0xcfe0f5, dirCol: 0xffffff, dirInt: 1.25 }, // 6 SKY (cloud)
    { sky: 0xd8dce0, fogCol: 0xdfe3e8, fogNear: 40, fogFar: 120,
      ground: 0x8a9aa8, hemiSky: 0xf0f4f8, hemiGround: 0x9aa6b0, dirCol: 0xfffdf0, dirInt: 1.0 }, // 7 AIRPORT (liminal)
    { sky: 0x141019, fogCol: 0x1e1822, fogNear: 26, fogFar: 90,
      ground: 0x322a30, hemiSky: 0x6a5a72, hemiGround: 0x18141c, dirCol: 0x8a7a92, dirInt: 0.45 }, // 8 MALL (深夜・暗い)
    { sky: 0xd8c66e, fogCol: 0xcfbe60, fogNear: 24, fogFar: 80,
      ground: 0xc2b25a, hemiSky: 0xf2e6a0, hemiGround: 0x9a8c40, dirCol: 0xfff0b0, dirInt: 1.15 }, // 9 LEVEL 0 (黄色ロビー・蛍光灯・閉所感)
] as const;

// [forest, desert, snow, sea] — ONLY colors; geometry never changes. Each spawn fn
// `new`s a fresh material per spawn, so .color.setHex(...) at spawn time is free + disposed normally.
const GIMMICK_SKIN = [
    // wall      wallBand   crate      spike      spikeBase  saw        hazardCol  frame      accent
    { wall: 0xc0894b, wallBand: 0x8a5a2b, crate: 0x9a6b3a, spike: 0xcfd6df, spikeBase: 0x4a4f57, saw: 0xc6ccd4, hazardCol: 0xff5a1e, frame: 0x6b4a2a, accent: 0x7bd6ff }, // 0 FOREST wood
    { wall: 0xd9b072, wallBand: 0xb98a45, crate: 0xc99a55, spike: 0xe8d8a8, spikeBase: 0x8a7642, saw: 0xd8c9a0, hazardCol: 0xff7a1e, frame: 0xb07c3a, accent: 0xffd23b }, // 1 DESERT sandstone
    { wall: 0xbfe0ee, wallBand: 0x8fb6cc, crate: 0xcfe4f2, spike: 0xeaf6ff, spikeBase: 0x6f93a8, saw: 0xe8f4ff, hazardCol: 0x6fd2ff, frame: 0x9ec6da, accent: 0xbfe9ff }, // 2 SNOW ice
    { wall: 0x9a6b3a, wallBand: 0x5e4022, crate: 0x7a5230, spike: 0xcfd6df, spikeBase: 0x35506a, saw: 0xc6ccd4, hazardCol: 0x2fd0c4, frame: 0x4a6f8a, accent: 0x2fd0c4 }, // 3 SEA dock/teal
    { wall: 0x4a3530, wallBand: 0x271713, crate: 0x5a3a30, spike: 0x6a4a40, spikeBase: 0x271713, saw: 0xc0a090, hazardCol: 0xff5a1e, frame: 0x8a4a30, accent: 0xff7a2e }, // 4 VOLCANO basalt/lava
    { wall: 0x6a6f7a, wallBand: 0x3c4047, crate: 0x5a5f68, spike: 0xcfd6df, spikeBase: 0x2e3138, saw: 0xc6ccd4, hazardCol: 0x8affc0, frame: 0x5a6070, accent: 0x9affd0 }, // 5 GRAVEYARD stone/bone
    { wall: 0xbfd6f0, wallBand: 0x8fb0d8, crate: 0xd0e0f5, spike: 0xeaf4ff, spikeBase: 0x9ab4d8, saw: 0xeaf4ff, hazardCol: 0xffe14d, frame: 0xa0c0e8, accent: 0xfff0a0 }, // 6 SKY crystal/cloud
    { wall: 0xcfd6de, wallBand: 0x9aa6b0, crate: 0xb0bcc8, spike: 0xeaf0f6, spikeBase: 0x6a7682, saw: 0xdfe6ee, hazardCol: 0xffd23b, frame: 0x8a96a2, accent: 0x6fd2ff }, // 7 AIRPORT panels
    { wall: 0x4a4048, wallBand: 0x2c2630, crate: 0x52464e, spike: 0x6a5e66, spikeBase: 0x241e28, saw: 0x70646c, hazardCol: 0xff5a8d, frame: 0x3a323e, accent: 0xff6ad0 }, // 8 MALL (暗い・ネオン)
    { wall: 0xd8c878, wallBand: 0xb09a44, crate: 0xc6b45e, spike: 0xeadf9a, spikeBase: 0x8a7a3a, saw: 0xd8c878, hazardCol: 0xffcf4a, frame: 0xb8a64e, accent: 0xfff0a0 }, // 9 LEVEL 0 (黄色い壁紙)
] as const;

// ---------- イージング (純関数・ゼロアロケーション) ----------
const Ease = {
    quadIn: (x: number) => x * x,
    quadOut: (x: number) => 1 - (1 - x) * (1 - x),
    smooth: (x: number) => x * x * (3 - 2 * x), // smoothstep — 体重移動の待機・チャージの立ち上がり
    backOut: (x: number, s = 1.7) => 1 + (s + 1) * Math.pow(x - 1, 3) + s * Math.pow(x - 1, 2), // overshoot — 打ち下ろしのキレ
};
function clamp01(x: number): number {
    return x < 0 ? 0 : x > 1 ? 1 : x;
}

type ItemKind = "gun" | "minigun" | "armor" | "robot" | "bonus" | "heal" | "shield" | "cannon" | "weapon" | "rocket" | "homing" | "turret" | "drones" | "rarearmor" | "partyarmor";
const ITEM_META: Record<ItemKind, { label: string; color: number }> = {
    gun: { label: "GUN", color: 0xffd75e },
    minigun: { label: "MINIGUN", color: 0xff9f43 },
    armor: { label: "ARMOR", color: 0x7bed9f },
    robot: { label: "ROBOT", color: 0xa55eea },
    bonus: { label: "+CREW", color: 0x2fae5a }, // green reinforcement crate (G5)
    heal: { label: "BOOST", color: 0x6fc8ff }, // blue buff orb (G6)
    shield: { label: "SHIELD", color: 0x7bffea }, // cyan barrier — blocks the next few hits entirely
    cannon: { label: "波動砲", color: 0x49e0ff }, // electric wave cannon — push it as cover, then it fires once
    weapon: { label: "⚡POWER", color: 0xffae3b }, // 火力テンプレ (G11) — 撃ち落とすと一定時間 firepower 倍化
    rocket: { label: "ロケキャノン", color: 0xff7a3b }, // ロケキャノン (id3) — 集めて切替えるアーセナル武器
    homing: { label: "追尾ミサイル", color: 0x9fe8ff }, // 追尾ミサイルランチャー (id5) — 飛行/シールド敵・mech ボス特効
    turret: { label: "360°タレット", color: 0xffd24a }, // 全方位 auto タレット — 背後/周回の敵の答え (耐久制)
    drones: { label: "ガンドローン", color: 0x6fe0ff }, // 群衆を旋回して外向きに撃つ飛行ドローン (台数制)
    rarearmor: { label: "✦レアアーマー", color: 0xffd75e }, // 黄金のイージスを解放する特殊レアドロップ
    partyarmor: { label: "🎭ゴアの仮面", color: 0xff5ec0 }, // パーティゴアアーマーを解放する最終盤の激レアドロップ
};
const SHIELD_GRANT = 3; // シールド 1 個で追加される「完全ブロック回数」
const CANNON_MAX_STOCK = 3; // 波動砲のストック上限 (取得で +1・召喚で -1)
const CANNON_SHIELD_HP = 6; // 召喚した波動砲の盾耐久 (前面で受け止めるたび減り・0 で砕けて発射不能)

// ---------- 武器アーセナル定義 (集めて所持・切替える 4 種) ----------
// id: 0 拳銃 / 1 ライフル / 2 ミニガン / 3 ロケキャノン。
// mult=firepower 倍率 / interval=発射間隔 / bullets=同時弾数 / color=弾色 / trail=トレイル有無。
// ロケキャノンは sustained 火力 0 (mult 0)・代わりに rocketCdT 周期で巨大ブラストを 1 発撃つ。
interface WeaponDef {
    name: string;
    icon: string;
    mult: number;
    interval: number;
    bullets: number;
    color: number;
    trail: boolean;
}
const WEAPON_DEFS: WeaponDef[] = [
    { name: "拳銃", icon: "🔫", mult: 1.0, interval: 0.08, bullets: 1, color: 0xfff27a, trail: false },
    { name: "ライフル", icon: "🪖", mult: 2.2, interval: 0.05, bullets: 2, color: 0xffe066, trail: false },
    { name: "ミニガン", icon: "🔥", mult: 4.0, interval: 0.03, bullets: 4, color: 0xffae3b, trail: true },
    { name: "ロケキャノン", icon: "🚀", mult: 0.0, interval: 0.0, bullets: 0, color: 0xff7a3b, trail: true },
    { name: "ブーメラン", icon: "🪃", mult: 2.5, interval: 0.0, bullets: 0, color: 0x8fffcf, trail: true },
    { name: "追尾ミサイル", icon: "🎯", mult: 0.0, interval: 0.0, bullets: 0, color: 0x9fe8ff, trail: true },
];
// ミニガン (id2) の過熱メーター: 連射で満タン→一時ロック。銃 (id0) との差別化。
const MINIGUN_WEAPON = 2;
const MINIGUN_HEAT_RATE = 0.5; // 連射で満タンになる速さ (~2s 連射で過熱)
const MINIGUN_COOL_RATE = 0.7; // 非射撃時の冷却速度
const MINIGUN_OVERHEAT_LOCK = 2.2; // 過熱後の発射ロック秒 (この間に冷えて再開)
const ROCKET_WEAPON = 3; // ロケキャノンの武器 id
const ROCKET_COOLDOWN = 4.0; // ロケキャノンの発射周期 (秒) — この遅さが「使い物にならない」肝
const ROCKET_AOE_HALF = 3.5; // ブラストの横半径 (wx ±)

// ---------- 追尾ミサイルランチャー (id5): ロケキャノン系の低レート。ホーミングで狙いにくい敵を叩く ----------
const HOMING_WEAPON = 5; // 追尾ミサイルの武器 id (mult 0 = 継続火力なし)
const HOMING_COOLDOWN = 3.0; // 一斉発射の周期 (秒)・遅い
const HOMING_VOLLEY = 3; // 1 斉射の発数
const HOMING_BOSS_FRAC = 0.05; // 1 発あたりのボス HP 割合ダメージ (mech には特効で倍率)
const HOMING_SUPER_MUL = 2.6; // mech ボス / シールドロボへの特効倍率

// ---------- ブーメラン (id4): 投げて戻ってくるまで次は投げられない貫通武器 ----------
const BOOMERANG_WEAPON = 4; // ブーメランの武器 id (mult 0 = 継続火力なし)
const BOOM_SPEED = 34; // 投擲 (往路) の速度
const BOOM_RETURN_SPEED = 30; // 復路 (戻り) の速度
const BOOM_MAX_LIFE = 3.0; // 往路の自動折返しまでの寿命 (秒)
const BOOM_RANGE_Z = SPAWN_Z * 0.7; // この z を超えたら自動で折り返す (奥行き到達距離)
const BOOM_HIT_R = 1.1; // ダメージ判定の半径 (wx/z)
const BOOM_CATCH_R = 1.2; // 復路でプレイヤーに戻ったと見なすキャッチ半径
const BOOM_HIT_DMG = (fire: number) => fire * FIRE_FACTOR * 4; // 1 接触あたりのダメージ (瞬間接触なので一撃を重く)

// 援軍メカのホーミングミサイル (フレーバー + 小ダメージ。火力ブーストが本体なので控えめに)
const MISSILE_INTERVAL = 1.6; // 1 発撃ってから次までの基本間隔 (秒)・機体数で前倒し
const MISSILE_SPEED = 26; // 巡航速度 (units/s)
const MISSILE_TURN = 5.5; // 旋回率 (rad/s)・低いほど大きく弧を描く
const MISSILE_LIFE = 2.4; // 寿命 (秒)・対象消失後も少し直進して消える
const MISSILE_HIT_R = 1.0; // 起爆する対象への近接半径
const MISSILE_AOE_R = 1.5; // 巻き込み AoE 半径

const MECH_MELEE_DUR = 0.95; // mech ボスの近接(拳/蹴り)の総尺。アニメと着弾タイミングの基準。

// 360°タレット (展開型コンパニオン): 群衆に追従し、最寄りの敵を全方位 (前/後/横/飛行) に auto 射撃。
// 耐久は発射と背後攻撃の受け止めで消耗し、0 で破壊 (再取得で再展開)。背後攻撃 (flyer/周回敵) の答え。
const TURRET_MAX_HP = 120; // 耐久 (満タン)
const TURRET_SHOT_COST = 1.1; // 1 発あたりの耐久消費
const TURRET_BACK_COST = 16; // 背後攻撃を 1 回受け止める耐久消費
const TURRET_FIRE_INT = 0.38; // 発射間隔 (秒)
const TURRET_RANGE = 34; // 索敵半径

// 周回ガンドローン (タレットの飛行版): 群衆の周りを旋回し外向きに自動射撃するコンパニオン (台数制)。
const DRONE_MAX = 4; // 最大台数
const DRONE_FIRE_INT = 0.5; // 各機の発射間隔
const DRONE_ORBIT_R = 4.2; // 周回半径
const ROBOT_PD_INT = 0.5; // ロボのポイントディフェンス: 敵ミサイル迎撃の基準間隔 (実間隔 = この値 / robots)
const SMILER_LUNGE_DUR = 1.15; // smiler の消灯(必殺)突進の総尺。

// 分割射撃 (地上と空を同時に狙う) のバランス。空中の敵は当てにくい。
const AIR_DMG_MUL = 0.6; // 空中の敵への実効ダメージ係数 (命中減・撃ち落とすのに手間取る)
const GROUND_FIRE_SHARE = 0.75; // 両対象が居るとき地上へ回す火力配分 (空に火力を吸われすぎない)
const AIR_FIRE_SHARE = 0.45; // 両対象が居るとき空へ回す火力配分

// ---------- ジオメトリ生成 ----------

/** geo に単色の頂点カラー属性を付与する (merge 時に色を保つため)。 */
function paint(geo: THREE.BufferGeometry, hex: number): THREE.BufferGeometry {
    const c = new THREE.Color(hex);
    const n = geo.attributes.position.count;
    const arr = new Float32Array(n * 3);
    for (let i = 0; i < n; i++) {
        arr[i * 3] = c.r;
        arr[i * 3 + 1] = c.g;
        arr[i * 3 + 2] = c.b;
    }
    geo.setAttribute("color", new THREE.BufferAttribute(arr, 3));
    if (!geo.attributes.uv) {
        // merge には全ジオメトリの属性集合が揃っている必要がある
        geo.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(n * 2), 2));
    }
    return geo;
}

/** ローポリのクルー (足元 y=0・前方 +z) を 1 個の頂点カラー付きジオメトリで作る。 */
function buildCrewGeometry(col: { body: number; dark: number; visor: number }): THREE.BufferGeometry {
    const parts: THREE.BufferGeometry[] = [];
    // 胴 (カプセル)
    const body = new THREE.CapsuleGeometry(0.34, 0.5, 4, 10);
    body.translate(0, 0.62, 0);
    parts.push(paint(body, col.body));
    // バックパック
    const bp = new THREE.BoxGeometry(0.34, 0.5, 0.22);
    bp.translate(0, 0.66, -0.3);
    parts.push(paint(bp, col.dark));
    // バイザー
    const vis = new THREE.BoxGeometry(0.4, 0.22, 0.16);
    vis.translate(0, 0.82, 0.3);
    parts.push(paint(vis, col.visor));
    // 脚 2 本
    for (const dx of [-0.16, 0.16]) {
        const leg = new THREE.BoxGeometry(0.18, 0.26, 0.2);
        leg.translate(dx, 0.13, 0.02);
        parts.push(paint(leg, col.dark));
    }
    return mergeGeometries(parts, false)!;
}

/**
 * 援軍の飛行メカ (味方版)。敵の巨大 mecha と同じ系統の意匠 (V 字バイザー/肩アーマー/
 * バックスラスター) を、味方とわかる青/シアン/白パレットで構成。脚は無く、下後方に向いた
 * ジェットノズル (コーン) でホバリングし、肩と腕にミサイルポッドを背負う。足元 y=0・正面 +z。
 */
function buildRobotGeometry(): THREE.BufferGeometry {
    const P: THREE.BufferGeometry[] = [];
    const B = 0x3aa0ff, A = 0x9fe8ff, W = 0xeef6ff, G = 0x2a3550, C = 0x7af2ff, O = 0xff8a3b;
    const box = (w: number, h: number, d: number, x: number, y: number, z: number, col: number, rx = 0, rz = 0) => {
        const g = new THREE.BoxGeometry(w, h, d);
        if (rx) g.rotateX(rx); if (rz) g.rotateZ(rz);
        g.translate(x, y, z); P.push(paint(g, col));
    };
    const cone = (r: number, h: number, x: number, y: number, z: number, col: number, rx = 0, rz = 0) => {
        const g = new THREE.ConeGeometry(r, h, 12);
        if (rx) g.rotateX(rx); if (rz) g.rotateZ(rz);
        g.translate(x, y, z); P.push(paint(g, col));
    };
    // 胴 (前傾気味のコックピット)
    box(0.9, 0.78, 0.62, 0, 1.18, 0, B);                       // 胸
    box(0.34, 0.5, 0.5, 0, 1.18, 0.08, A);                     // 胸中央 (シアン)
    box(0.52, 0.18, 0.12, 0, 1.3, 0.34, C);                    // コックピット発光帯
    box(0.66, 0.46, 0.52, 0, 0.74, 0, G);                      // 腰 (ダークフレーム)
    // 頭・バイザー
    box(0.36, 0.3, 0.36, 0, 1.66, 0.02, W);                    // 頭
    box(0.3, 0.09, 0.06, 0, 1.66, 0.2, C);                     // シアンの単眼バイザー帯
    for (const sx of [-1, 1]) box(0.28, 0.05, 0.04, 0.12 * sx, 1.82, 0.1, A, 0, 0.5 * sx); // V 字アンテナ
    // 肩アーマー + ミサイルポッド
    for (const sx of [-1, 1]) {
        box(0.4, 0.34, 0.5, 0.62 * sx, 1.34, 0, W);            // 肩アーマー
        box(0.26, 0.12, 0.28, 0.64 * sx, 1.52, 0, B);          // 肩上面アクセント
        box(0.22, 0.2, 0.42, 0.66 * sx, 1.54, -0.06, G);       // 肩ミサイルポッド
        for (const px of [-0.06, 0.06]) box(0.05, 0.05, 0.12, 0.66 * sx + px, 1.54, 0.18, O); // ポッドの弾頭 (橙)
    }
    // 腕 (細身・前腕にもポッド)
    for (const sx of [-1, 1]) {
        const dx = 0.66 * sx;
        box(0.2, 0.4, 0.24, dx, 1.0, 0.04, G);                 // 上腕
        box(0.26, 0.36, 0.3, dx, 0.62, 0.04, W);               // 前腕アーマー
        box(0.16, 0.18, 0.34, dx, 0.66, 0.0, B);               // 前腕ミサイルポッド
    }
    // 小ぶりの後退翼
    for (const sx of [-1, 1]) box(0.66, 0.06, 0.34, 0.62 * sx, 1.06, -0.28, A, 0, 0.32 * sx);
    // バックパック + 下後方ジェットノズル (ジェット炎の発生点)
    box(0.5, 0.46, 0.2, 0, 1.18, -0.34, G);                    // バックパック
    for (const sx of [-1, 1]) {
        cone(0.13, 0.4, 0.18 * sx, 0.58, -0.34, G, 2.7);       // ノズル本体 (下後方へ向ける)
        cone(0.1, 0.14, 0.2 * sx, 0.42, -0.42, C, 2.7);        // ノズル内側 (シアン熱)
    }
    return mergeGeometries(P, false)!;
}

/**
 * ガンダム風のごつい mecha (巨大ボス専用)。トリコロール (白/青/赤/黄) + ダークフレーム。
 * V 字アンテナ・胸ダクト・肩アーマー・スカート・装甲脚・バックパックスラスター。足元 y=0・正面 +z。
 */
function buildGiantRobotGeometry(): THREE.BufferGeometry {
    const P: THREE.BufferGeometry[] = [];
    const W = 0xeef2f6, B = 0x2a55c8, R = 0xcf3330, Y = 0xf2c20e, G = 0x3c4250, C = 0x7af2ff;
    const box = (w: number, h: number, d: number, x: number, y: number, z: number, col: number, rx = 0, ry = 0, rz = 0) => {
        const g = new THREE.BoxGeometry(w, h, d);
        if (rx) g.rotateX(rx); if (ry) g.rotateY(ry); if (rz) g.rotateZ(rz);
        g.translate(x, y, z); P.push(paint(g, col));
    };
    const cyl = (rt: number, rb: number, h: number, x: number, y: number, z: number, col: number, rx = 0, rz = 0) => {
        const g = new THREE.CylinderGeometry(rt, rb, h, 12);
        if (rx) g.rotateX(rx); if (rz) g.rotateZ(rz);
        g.translate(x, y, z); P.push(paint(g, col));
    };
    // 脚 (左右) — ごつい装甲
    for (const sx of [-1, 1]) {
        const dx = 0.32 * sx;
        box(0.52, 0.28, 0.82, dx, 0.14, 0.14, B);              // 足 (前に長い)
        box(0.2, 0.18, 0.34, dx, 0.34, -0.12, G);              // 足首
        box(0.48, 0.6, 0.5, dx, 0.66, 0, B);                   // 脛アーマー
        box(0.52, 0.18, 0.18, dx, 0.7, 0.27, W);               // 脛前ダクト
        box(0.36, 0.22, 0.44, dx, 1.0, 0.06, Y);               // 膝当て
        box(0.42, 0.42, 0.44, dx, 1.24, 0, W);                 // 腿
        box(0.24, 0.22, 0.32, dx, 1.0, 0, G);                  // 膝関節
    }
    // 腰・スカート
    box(0.72, 0.3, 0.52, 0, 1.5, 0, W);                         // 骨盤
    box(0.22, 0.18, 0.12, 0, 1.48, 0.28, Y);                    // ベルトバックル
    box(0.36, 0.36, 0.14, 0, 1.38, 0.30, R, 0.25);             // 前スカート
    for (const sx of [-1, 1]) box(0.18, 0.36, 0.42, 0.44 * sx, 1.4, 0, W); // サイドスカート
    // 胴
    box(0.52, 0.32, 0.44, 0, 1.72, 0, G);                       // 腹 (ダークフレーム)
    box(0.98, 0.52, 0.56, 0, 2.04, 0, W);                       // 胸
    box(0.32, 0.48, 0.52, 0, 2.04, 0.06, R);                    // 胸中央 (赤)
    box(0.2, 0.2, 0.1, 0, 2.1, 0.31, C);                        // コックピット (シアン)
    for (const sx of [-1, 1]) box(0.17, 0.28, 0.14, 0.31 * sx, 1.98, 0.26, Y); // 胸ダクト (黄)
    // 肩アーマー (大きいパウルドロン)
    for (const sx of [-1, 1]) {
        box(0.5, 0.48, 0.62, 0.8 * sx, 2.08, 0, W);            // 肩
        box(0.34, 0.16, 0.34, 0.84 * sx, 2.3, 0, B);          // 肩上面アクセント
        box(0.1, 0.22, 0.4, 0.99 * sx, 2.05, 0, R);           // 肩サイドのライン
    }
    // 腕
    for (const sx of [-1, 1]) {
        const dx = 0.82 * sx;
        box(0.28, 0.42, 0.32, dx, 1.78, 0, G);                // 上腕 (ダーク)
        box(0.36, 0.5, 0.38, dx, 1.38, 0, W);                 // 前腕アーマー
        box(0.1, 0.22, 0.06, (dx + 0.2 * sx), 1.42, 0, R);    // 前腕サイド
        box(0.32, 0.26, 0.36, dx, 1.06, 0, G);                // 拳
    }
    // 頭
    box(0.2, 0.12, 0.2, 0, 2.34, 0, G);                        // 首
    box(0.36, 0.34, 0.38, 0, 2.52, 0, W);                      // 頭
    box(0.28, 0.2, 0.06, 0, 2.48, 0.19, G);                    // フェイスマスク
    box(0.24, 0.07, 0.04, 0, 2.5, 0.22, C);                    // ツインアイ (バイザー)
    for (const sx of [-1, 1]) box(0.06, 0.16, 0.18, 0.19 * sx, 2.5, 0, Y); // 側頭ダクト
    box(0.07, 0.14, 0.05, 0, 2.68, 0.15, R);                   // 額クレスト
    for (const sx of [-1, 1]) box(0.3, 0.05, 0.04, 0.13 * sx, 2.7, 0.13, Y, 0, 0, 0.5 * sx); // V 字アンテナ
    // バックパック + スラスター
    box(0.52, 0.54, 0.22, 0, 2.04, -0.36, G);                  // バックパック
    for (const sx of [-1, 1]) {
        cyl(0.1, 0.13, 0.52, 0.16 * sx, 2.26, -0.5, G, -0.3);  // スラスター
        cyl(0.13, 0.1, 0.12, 0.18 * sx, 2.5, -0.58, R, -0.3);  // ノズル先端 (赤熱)
    }
    return mergeGeometries(P, false)!;
}

// 走りの 1 サイクル (8 コマ) の関節角。右脚基準・左脚は半サイクル (p+4) ずらし。
// 【走って見せる肝＝接地脚と遊脚の非対称】(3D ランサイクルの定石・要調査結果):
//   frame 0-3 = この脚の「接地〜蹴り出し」: 膝はほぼ伸ばし (KNEE 小)、HIP が単調に流れて
//     足が体の下を後方へ流れる (トレッドミルの接地)。
//   frame 4-7 = この脚の「遊脚」: 膝を深く畳み (KNEE 大) 足を持ち上げて素早く前へ振り出す。
//   左右対称の振り子 (旧実装) は「行進」に見えるのが根本原因だった。
const RUN_HIP = [-0.52, -0.14, 0.31, 0.70, 0.66, 0.38, -0.17, -0.61];
const RUN_KNEE = [0.21, 0.49, 0.31, 0.17, 1.31, 1.66, 1.83, 0.96];

/** 8 キーのサイクル配列を任意 phase(0..1) で線形補間して滑らかに取り出す。 */
function sampleCycle(arr: number[], phase: number): number {
    const n = arr.length;
    const f = (((phase % 1) + 1) % 1) * n;
    const i0 = Math.floor(f) % n;
    const i1 = (i0 + 1) % n;
    const t = f - Math.floor(f);
    return arr[i0] * (1 - t) + arr[i1] * t;
}

// =============================================================
//  連続素体クルー (継ぎ目なしスキンメッシュ + ランタイムスケルタルアニメ)
//  ・MarchingCubes(メタボール) で頭〜胴〜手足を融合した「ひとつなぎ」の一枚皮を 1 回だけ生成。
//  ・約 11 ボーンの簡易スケルトンを組み、各頂点を最近傍 2 ボーン区間でブレンドしてスキン。
//  ・走りサイクル (RUN_HIP/RUN_KNEE) を Quaternion トラックの AnimationClip に移植。
//  ・描画は InstancedMesh2 の per-instance スキニングで、個体ごとに位相をずらしてロックステップを解消。
//  メタボールの重みで変形が破綻する場合に備え、buildSkinnedCrew は内部で try し、
//  零頂点なら太い連続プリミティブ方式 (buildCrewBodyPrimitive) にフォールバックする。
// =============================================================

// ボーン名 (rest 骨格・クリップ・ウェイト割当で共有)。手ボーンは将来の武器ポーズ用に含める。
const CREW_BONE_NAMES = [
    "hips", "spine", "head",
    "thighL", "shinL", "thighR", "shinR",
    "upperArmL", "foreArmL", "handL",
    "upperArmR", "foreArmR", "handR",
] as const;
type CrewBoneName = (typeof CREW_BONE_NAMES)[number];

// 各ボーンの rest ワールド座標 (足元 y=0・前方 +z・現クルーと同程度の身長)。
// メタボール配置・スキンウェイト・ボーン rest 位置の三者で同じ値を使い整合させる。
const CREW_BONE_REST: Record<CrewBoneName, [number, number, number]> = {
    // Mob Control 風の「背が高めでスリム」体型。胴は細く長く、手足はスリムな長いチューブ。
    hips: [0, 0.52, 0],
    spine: [0, 0.80, 0],
    head: [0, 1.10, 0],
    thighL: [0.095, 0.50, 0], shinL: [0.10, 0.25, 0],
    thighR: [-0.095, 0.50, 0], shinR: [-0.10, 0.25, 0],
    // 腕は肩から体側へスリムに長く下りる (胴に近づけて肩で滑らかに融合させる)。
    upperArmL: [0.20, 0.78, 0], foreArmL: [0.23, 0.56, 0], handL: [0.25, 0.36, 0],
    upperArmR: [-0.20, 0.78, 0], foreArmR: [-0.23, 0.56, 0], handR: [-0.25, 0.36, 0],
};
// 親子関係 (root=hips)。setBonesAt が parent.matrixWorld を辿るので親順に並べる。
const CREW_BONE_PARENT: Record<CrewBoneName, CrewBoneName | null> = {
    hips: null, spine: "hips", head: "spine",
    thighL: "hips", shinL: "thighL", thighR: "hips", shinR: "thighR",
    upperArmL: "spine", foreArmL: "upperArmL", handL: "foreArmL",
    upperArmR: "spine", foreArmR: "upperArmR", handR: "foreArmR",
};
// スキンウェイトに使う「肉付きの骨区間」: [親ジョイント, 子ジョイント, 半径]。
// 各頂点は最も近い区間 2 本を距離フォールオフでブレンドして子側ボーンへ割り当てる。
type BoneSeg = { bone: CrewBoneName; a: THREE.Vector3; b: THREE.Vector3; r: number };

/** メタボール 1 球 = [x,y,z(ワールド), strength, 半径(融合の太さ)]。res 正規化前のワールド座標で持つ。 */
type Ball = [number, number, number, number, number];

/**
 * rest 骨格に沿ってメタボール球を配置。
 * 参考画像の「ずんぐり幼児体型」を狙う: 大きい丸頭 + 丸い胴に、胴幅の 1/3 ほど太い
 * 「スタブ状」の腕・脚が短く生える。各肢は数珠繋ぎの細球をやめ、少数 (2〜3 個) の
 * 大きく重なるボールで構成して糸状化を防ぐ。手足の先端は丸い膨らみで終わる。
 */
function crewBallLayout(): Ball[] {
    const J = CREW_BONE_REST;
    const balls: Ball[] = [];
    const add = (p: [number, number, number], s: number, r: number) => balls.push([p[0], p[1], p[2], s, r]);
    // 頭 (大きい丸頭でマスコットらしく)
    add([J.head[0], J.head[1] + 0.02, J.head[2]], 1.0, 0.25);
    // 胴 (背骨を 3 点・細く長い樽に。スリム体型 → 肢が滑らかなチューブとして出る)
    add(J.spine, 0.9, 0.175);
    add([0, (J.spine[1] + J.hips[1]) / 2, 0], 0.9, 0.185);
    add(J.hips, 0.92, 0.19);
    // スリムな長いチューブ肢ヘルパ: 区間を n+1 個の球で滑らかに埋める (球を密に並べ均一な太さの管に)。
    const limb = (a: [number, number, number], b: [number, number, number], n: number, s: number, r: number) => {
        for (let k = 0; k <= n; k++) {
            const t = k / n;
            add([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t], s, r);
        }
    };
    // 腕 (肩→肘→手)。胴脇からスリムに長く下りる。肩で胴と滑らかに融合 (各区間 2 球で均一なチューブ)。
    limb(J.upperArmL, J.foreArmL, 2, 0.85, 0.085);
    limb(J.foreArmL, J.handL, 2, 0.85, 0.082);
    add(J.handL, 0.88, 0.092); // 手 = 丸い終端 (将来の武器アタッチ位置を保持)
    limb(J.upperArmR, J.foreArmR, 2, 0.85, 0.085);
    limb(J.foreArmR, J.handR, 2, 0.85, 0.082);
    add(J.handR, 0.88, 0.092);
    // 脚 (腿→脛→足元)。スリムな長い管。股で胴と滑らかに融合・足元を丸く。
    limb(J.thighL, J.shinL, 2, 0.9, 0.105);
    limb(J.shinL, [J.shinL[0], 0.05, 0.03], 2, 0.9, 0.10);
    add([J.shinL[0], 0.05, 0.05], 0.9, 0.11); // 足 = 丸い終端
    limb(J.thighR, J.shinR, 2, 0.9, 0.105);
    limb(J.shinR, [J.shinR[0], 0.05, 0.03], 2, 0.9, 0.10);
    add([J.shinR[0], 0.05, 0.05], 0.9, 0.11);
    return balls;
}

/** スキンウェイト割当に使う骨区間 (肉付き区間)。 */
function crewBoneSegments(): BoneSeg[] {
    const J = CREW_BONE_REST;
    const v = (p: [number, number, number]) => new THREE.Vector3(p[0], p[1], p[2]);
    // 半径は素体の太いスタブ肢に合わせて広めに取り、肢の表面頂点が正しいボーンに束ねられるように。
    return [
        { bone: "head", a: v(J.head), b: v([J.head[0], J.head[1] + 0.16, J.head[2]]), r: 0.25 },
        { bone: "spine", a: v(J.spine), b: v(J.head), r: 0.18 },
        { bone: "hips", a: v(J.hips), b: v(J.spine), r: 0.19 },
        { bone: "thighL", a: v(J.thighL), b: v(J.shinL), r: 0.11 },
        { bone: "shinL", a: v(J.shinL), b: v([J.shinL[0], 0.05, 0.03]), r: 0.105 },
        { bone: "thighR", a: v(J.thighR), b: v(J.shinR), r: 0.11 },
        { bone: "shinR", a: v(J.shinR), b: v([J.shinR[0], 0.05, 0.03]), r: 0.105 },
        { bone: "upperArmL", a: v(J.upperArmL), b: v(J.foreArmL), r: 0.09 },
        { bone: "foreArmL", a: v(J.foreArmL), b: v(J.handL), r: 0.088 },
        { bone: "handL", a: v(J.handL), b: v([J.handL[0] + 0.03, J.handL[1] - 0.03, 0]), r: 0.092 },
        { bone: "upperArmR", a: v(J.upperArmR), b: v(J.foreArmR), r: 0.09 },
        { bone: "foreArmR", a: v(J.foreArmR), b: v(J.handR), r: 0.088 },
        { bone: "handR", a: v(J.handR), b: v([J.handR[0] - 0.03, J.handR[1] - 0.03, 0]), r: 0.092 },
    ];
}

/** 点 p から線分 ab への最短距離 (区間端でクランプ)。 */
function pointSegDist(p: THREE.Vector3, a: THREE.Vector3, b: THREE.Vector3): number {
    const abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
    const apx = p.x - a.x, apy = p.y - a.y, apz = p.z - a.z;
    const ab2 = abx * abx + aby * aby + abz * abz || 1e-6;
    let t = (apx * abx + apy * aby + apz * abz) / ab2;
    t = t < 0 ? 0 : t > 1 ? 1 : t;
    const dx = apx - abx * t, dy = apy - aby * t, dz = apz - abz * t;
    return Math.sqrt(dx * dx + dy * dy + dz * dz);
}

/**
 * 一枚皮ジオメトリ (position/normal/uv) に skinIndex/skinWeight を付与する。
 * 各頂点で全骨区間への距離を測り、最も近い 2 区間を距離フォールオフ (近いほど重い) で
 * ブレンド。メタボールは区間に沿って置いたので最近傍ブレンドで素直に決まる。
 */
function attachCrewSkinWeights(geo: THREE.BufferGeometry, segs: BoneSeg[], boneIndexOf: Record<CrewBoneName, number>): void {
    const pos = geo.getAttribute("position");
    const n = pos.count;
    const skinIndex = new Uint16Array(n * 4);
    const skinWeight = new Float32Array(n * 4);
    const p = new THREE.Vector3();
    for (let i = 0; i < n; i++) {
        p.set(pos.getX(i), pos.getY(i), pos.getZ(i));
        // 最近 2 区間を探す
        let bi0 = 0, bi1 = 0, d0 = Infinity, d1 = Infinity;
        for (let s = 0; s < segs.length; s++) {
            const d = pointSegDist(p, segs[s].a, segs[s].b) / segs[s].r; // 半径で正規化
            if (d < d0) { d1 = d0; bi1 = bi0; d0 = d; bi0 = s; }
            else if (d < d1) { d1 = d; bi1 = s; }
        }
        // 距離フォールオフ: 近い方を強く。w = 1/(d^2+eps) で滑らかにブレンド。
        const w0 = 1 / (d0 * d0 + 1e-3);
        const w1 = 1 / (d1 * d1 + 1e-3);
        const sum = w0 + w1 || 1;
        const o = i * 4;
        skinIndex[o] = boneIndexOf[segs[bi0].bone];
        skinIndex[o + 1] = boneIndexOf[segs[bi1].bone];
        skinWeight[o] = w0 / sum;
        skinWeight[o + 1] = w1 / sum;
        // 残り 2 本は未使用 (index 0 / weight 0)
    }
    geo.setAttribute("skinIndex", new THREE.Uint16BufferAttribute(skinIndex, 4));
    geo.setAttribute("skinWeight", new THREE.Float32BufferAttribute(skinWeight, 4));
}

// 素体方式の切替 (優先順: SDF > メタボール > プリミティブ)。
// USE_SDF_BODY=true(既定): カプセル距離関数を smooth-min で結合し Marching Cubes で一枚皮化。
//   関節だけ滑らかにフィレットされ、肢は管の形を保つ (Mob Control 風の理想)。
// USE_METABALL_BODY=true: 旧メタボール (肢が胴に吸収されのっぺり)。素体研究用に残置。
// 両 false: 太い連続プリミティブ (カプセル・継ぎ目は出るが確実)。
const USE_SDF_BODY: boolean = true;
const USE_METABALL_BODY: boolean = false;

/** 丸い円錐 (両端が球で半径 r1→r2 にテーパーするカプセル) の符号付き距離。IQ の sdRoundCone。 */
function sdRoundCone(
    px: number, py: number, pz: number,
    ax: number, ay: number, az: number, bx: number, by: number, bz: number, r1: number, r2: number,
): number {
    const bax = bx - ax, bay = by - ay, baz = bz - az;
    const l2 = bax * bax + bay * bay + baz * baz || 1e-9;
    const rr = r1 - r2;
    const a2 = l2 - rr * rr;
    const il2 = 1 / l2;
    const pax = px - ax, pay = py - ay, paz = pz - az;
    const y = pax * bax + pay * bay + paz * baz;
    const z = y - l2;
    const xx = pax * l2 - bax * y, xy = pay * l2 - bay * y, xz = paz * l2 - baz * y;
    const x2 = xx * xx + xy * xy + xz * xz;
    const y2 = y * y * l2;
    const z2 = z * z * l2;
    const k = Math.sign(rr) * rr * rr * x2;
    if (Math.sign(z) * a2 * z2 > k) return Math.sqrt(x2 + z2) * il2 - r2;
    if (Math.sign(y) * a2 * y2 < k) return Math.sqrt(x2 + y2) * il2 - r1;
    return (Math.sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
}

/** 多項式 smooth-min。k=ブレンド幅 (関節フィレットの強さ)。k→0 で通常の min。 */
function sdSmin(a: number, b: number, k: number): number {
    const h = Math.max(0, Math.min(1, 0.5 + 0.5 * (b - a) / k));
    return a * h + b * (1 - h) - k * h * (1 - h);
}

/** メタボールから継ぎ目なし素体ジオメトリ (position/normal/uv) を生成。失敗時は null。 */
function buildCrewBodyMetaball(): THREE.BufferGeometry | null {
    try {
        const res = 56; // スリムな長いチューブ肢を滑らかに出すため高め (一度きりの生成)。
        const march = new MarchingCubes(res, new THREE.MeshBasicMaterial(), true, false, 200000);
        march.isolation = 80;
        march.init(res);
        march.reset();
        const balls = crewBallLayout();
        // ワールド座標 (おおむね x:-0.5..0.5, y:0..1.1, z:-0.3..0.3) を 0..1 フィールドへ写像。
        // MarchingCubes の addBall は 0..1 座標・subtract は穴あけ強度。中央 0.5 に素体中心を置く。
        const FW = 1.8; // フィールドが覆うワールド幅 (背の高いスリム体型が収まるよう広め)
        const BCY = 0.6; // 素体の身長中心 (背が高くなったので上げる)
        const cx = 0.5, cy = 0.5, cz = 0.5;
        for (const [x, y, z, s, r] of balls) {
            const fx = cx + x / FW;
            const fy = cy + (y - BCY) / FW; // 身長中心を field 中央へ
            const fz = cz + z / FW;
            // strength は半径相関 (大きい球ほど強く)、subtract は融合の鋭さ
            march.addBall(fx, fy, fz, s * (r * 6), 12);
        }
        march.update();
        const cnt = (march as unknown as { count: number }).count;
        if (!cnt || cnt < 3) return null;
        const sp = march.geometry.getAttribute("position").array as Float32Array;
        const sn = march.geometry.getAttribute("normal").array as Float32Array;
        const posArr = new Float32Array(cnt * 3); posArr.set(sp.subarray(0, cnt * 3));
        const norArr = new Float32Array(cnt * 3); norArr.set(sn.subarray(0, cnt * 3));
        let geo = new THREE.BufferGeometry();
        geo.setAttribute("position", new THREE.BufferAttribute(posArr, 3));
        geo.setAttribute("normal", new THREE.BufferAttribute(norArr, 3));
        // MarchingCubes ローカル空間 (おおむね 0..1) → ワールドへ逆写像 (FW 倍, 中心戻し)
        const m = new THREE.Matrix4().makeTranslation(-cx, -cy, -cz);
        geo.applyMatrix4(m);
        geo.scale(FW, FW, FW);
        geo.translate(0, BCY, 0); // field 中央 → 身長中心へ戻す
        // 溶接 (変形時のクラック防止に必須)
        geo = mergeVertices(geo, 1e-3);
        // 接地のみ (身長正規化はしない: メッシュ座標をボーン rest 座標と一致させ、
        //  スキニングのピボットずれを防ぐ。レイアウトが既に身長 ~1.3 を出している)。
        geo.computeBoundingBox();
        geo.translate(0, -geo.boundingBox!.min.y, 0); // 足元を y=0 へ
        // ポリゴン削減 (数百〜2000 三角に収める)
        const vc = geo.getAttribute("position").count;
        if (vc > 1400) {
            try {
                const simplified = new SimplifyModifier().modify(geo, Math.floor((vc - 1100)));
                if (simplified && simplified.getAttribute("position").count > 0) geo = simplified;
            } catch { /* 簡略化失敗は統合済みで続行 */ }
        }
        geo.computeVertexNormals();
        if (!geo.attributes.uv) {
            geo.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(geo.getAttribute("position").count * 2), 2));
        }
        return geo;
    } catch {
        return null;
    }
}

/**
 * SDF + smooth-min 素体。カプセル距離関数を smin で結合し Marching Cubes で一枚皮化。
 * 関節だけ K 幅でフィレットされ、肢は管の形を保つ (メタボールの「肢が胴に吸収」問題を回避)。
 */
function buildCrewBodySDF(): THREE.BufferGeometry | null {
    try {
        const J = CREW_BONE_REST;
        const ft = (x: number): [number, number, number] => [x, 0.05, 0.05]; // 足先
        // テーパーするカプセル群 [ax,ay,az, bx,by,bz, r1,r2]
        const cones: number[][] = [
            [J.hips[0], J.hips[1], J.hips[2], J.spine[0], J.spine[1], J.spine[2], 0.185, 0.155], // 胴 (腰→胸)
            [J.spine[0], J.spine[1], J.spine[2], 0, 0.99, 0, 0.155, 0.12], // 首
            [J.upperArmL[0], J.upperArmL[1], J.upperArmL[2], J.foreArmL[0], J.foreArmL[1], J.foreArmL[2], 0.082, 0.072],
            [J.foreArmL[0], J.foreArmL[1], J.foreArmL[2], J.handL[0], J.handL[1], J.handL[2], 0.072, 0.065],
            [J.upperArmR[0], J.upperArmR[1], J.upperArmR[2], J.foreArmR[0], J.foreArmR[1], J.foreArmR[2], 0.082, 0.072],
            [J.foreArmR[0], J.foreArmR[1], J.foreArmR[2], J.handR[0], J.handR[1], J.handR[2], 0.072, 0.065],
            [J.thighL[0], J.thighL[1], J.thighL[2], J.shinL[0], J.shinL[1], J.shinL[2], 0.105, 0.088],
            [J.shinL[0], J.shinL[1], J.shinL[2], ...ft(J.shinL[0]), 0.088, 0.072],
            [J.thighR[0], J.thighR[1], J.thighR[2], J.shinR[0], J.shinR[1], J.shinR[2], 0.105, 0.088],
            [J.shinR[0], J.shinR[1], J.shinR[2], ...ft(J.shinR[0]), 0.088, 0.072],
        ];
        // 球群 [cx,cy,cz, r]
        const spheres: number[][] = [
            [J.head[0], J.head[1] + 0.02, J.head[2], 0.245], // 頭
            [J.handL[0], J.handL[1], J.handL[2], 0.078], [J.handR[0], J.handR[1], J.handR[2], 0.078], // 手
            [...ft(J.shinL[0]), 0.085], [...ft(J.shinR[0]), 0.085], // 足
        ];
        const K = 0.05; // フィレット幅 (関節の丸まり)
        const sdf = (px: number, py: number, pz: number): number => {
            let d = sdRoundCone(px, py, pz, cones[0][0], cones[0][1], cones[0][2], cones[0][3], cones[0][4], cones[0][5], cones[0][6], cones[0][7]);
            for (let i = 1; i < cones.length; i++) {
                const c = cones[i];
                d = sdSmin(d, sdRoundCone(px, py, pz, c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]), K);
            }
            for (let i = 0; i < spheres.length; i++) {
                const s = spheres[i];
                const dx = px - s[0], dy = py - s[1], dz = pz - s[2];
                d = sdSmin(d, Math.sqrt(dx * dx + dy * dy + dz * dz) - s[3], K);
            }
            return d;
        };
        const res = 56;
        const march = new MarchingCubes(res, new THREE.MeshBasicMaterial(), false, false, 300000);
        march.init(res);
        march.isolation = 0;
        const FW = 1.8, BCY = 0.6, size = res, size2 = res * res;
        const field = (march as unknown as { field: Float32Array }).field;
        for (let z = 0; z < size; z++) {
            const wz = (z / size - 0.5) * FW;
            for (let y = 0; y < size; y++) {
                const wy = (y / size - 0.5) * FW + BCY;
                const yo = z * size2 + y * size;
                for (let x = 0; x < size; x++) {
                    field[yo + x] = -sdf((x / size - 0.5) * FW, wy, wz); // 内側(sdf<0)→正
                }
            }
        }
        march.update();
        const cnt = (march as unknown as { count: number }).count;
        if (!cnt || cnt < 3) return null;
        const sp = march.geometry.getAttribute("position").array as Float32Array;
        const sn = march.geometry.getAttribute("normal").array as Float32Array;
        const posArr = new Float32Array(cnt * 3); posArr.set(sp.subarray(0, cnt * 3));
        const norArr = new Float32Array(cnt * 3); norArr.set(sn.subarray(0, cnt * 3));
        let geo = new THREE.BufferGeometry();
        geo.setAttribute("position", new THREE.BufferAttribute(posArr, 3));
        geo.setAttribute("normal", new THREE.BufferAttribute(norArr, 3));
        // MarchingCubes 出力は [-1,1] 空間 (中心 0)。場を wx=(x/size-0.5)*FW で書いたので
        // 出力 → ワールドは ×(FW/2) + y へ BCY。中心移動は不要 (既に 0 中心)。
        geo.scale(FW / 2, FW / 2, FW / 2);
        geo.translate(0, BCY, 0);
        geo = mergeVertices(geo, 1e-3);
        geo.computeBoundingBox();
        geo.translate(0, -geo.boundingBox!.min.y, 0); // 足元を y=0 へ
        const vc = geo.getAttribute("position").count;
        if (vc > 2600) {
            try {
                const simplified = new SimplifyModifier().modify(geo, Math.floor(vc - 2200));
                if (simplified && simplified.getAttribute("position").count > 0) geo = simplified;
            } catch { /* 簡略化失敗は続行 */ }
        }
        geo.computeVertexNormals();
        if (!geo.attributes.uv) geo.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(geo.getAttribute("position").count * 2), 2));
        return geo;
    } catch {
        return null;
    }
}

/**
 * フォールバック: 太い連続プリミティブ素体。各パーツを重なり気味に置いて merge+weld+smooth-normal。
 * 継ぎ目シルエットは出るが weight が確実 (生成時点でそのボーンに割り当て・関節付近のみブレンド)。
 * 戻り値の geometry には skinIndex/skinWeight を既に付与済み (boneIndexOf 必須)。
 */
function buildCrewBodyPrimitive(boneIndexOf: Record<CrewBoneName, number>): THREE.BufferGeometry {
    const J = CREW_BONE_REST;
    const parts: { geo: THREE.BufferGeometry; bone: CrewBoneName }[] = [];
    const seg = (bone: CrewBoneName, a: [number, number, number], b: [number, number, number], r: number) => {
        const dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
        const len = Math.sqrt(dx * dx + dy * dy + dz * dz) || 1e-3;
        const cap = new THREE.CapsuleGeometry(r, Math.max(0.001, len), 6, 12);
        // カプセルは +Y。区間方向へ回し中点へ
        const up = new THREE.Vector3(0, 1, 0);
        const dir = new THREE.Vector3(dx, dy, dz).normalize();
        const q = new THREE.Quaternion().setFromUnitVectors(up, dir);
        cap.applyQuaternion(q);
        cap.translate((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2);
        parts.push({ geo: cap, bone });
    };
    // 球パーツ ヘルパ (指定ボーンへ 100% ウェイト)。重なり気味に置いて継ぎ目を滑らかに。
    const ball = (bone: CrewBoneName, p: [number, number, number], r: number) => {
        const s = new THREE.SphereGeometry(r, 14, 12); s.translate(p[0], p[1], p[2]);
        parts.push({ geo: s, bone });
    };
    const mid = (a: [number, number, number], b: [number, number, number], t: number): [number, number, number] =>
        [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
    // Mob Control 風: スリム＆背高。頭=大きい球、胴=テーパーした重なり球、手足=スリムな管。
    // 関節 (首/肩/股) にブリッジ球を入れてパーツ間を滑らかに繋ぐ (継ぎ目の谷を埋める)。
    ball("head", [J.head[0], J.head[1] + 0.01, J.head[2]], 0.25); // 頭
    ball("spine", mid(J.spine, J.head, 0.55), 0.15);             // 首ブリッジ (頭→胴)
    ball("spine", J.spine, 0.18);                                 // 胸
    ball("hips", mid(J.hips, J.spine, 0.5), 0.185);              // 腹
    ball("hips", J.hips, 0.19);                                   // 骨盤
    ball("spine", J.upperArmL, 0.115); ball("spine", J.upperArmR, 0.115); // 肩ブリッジ (胴→腕)
    ball("hips", J.thighL, 0.13); ball("hips", J.thighR, 0.13);  // 股ブリッジ (胴→脚)
    // 腕 (スリムな管 + 丸い手で終端)
    seg("upperArmL", J.upperArmL, J.foreArmL, 0.085); seg("foreArmL", J.foreArmL, J.handL, 0.082);
    seg("upperArmR", J.upperArmR, J.foreArmR, 0.085); seg("foreArmR", J.foreArmR, J.handR, 0.082);
    ball("handL", J.handL, 0.092); ball("handR", J.handR, 0.092);
    // 脚 (スリムな管 + 丸い足で終端)
    seg("thighL", J.thighL, J.shinL, 0.105); seg("shinL", J.shinL, [J.shinL[0], 0.05, 0.04], 0.10);
    seg("thighR", J.thighR, J.shinR, 0.105); seg("shinR", J.shinR, [J.shinR[0], 0.05, 0.04], 0.10);
    ball("shinL", [J.shinL[0], 0.05, 0.05], 0.11); ball("shinR", [J.shinR[0], 0.05, 0.05], 0.11);
    // 各パーツの頂点に「そのボーン 100%」の skinIndex/skinWeight を焼いてから merge。
    const geos: THREE.BufferGeometry[] = [];
    for (const part of parts) {
        const g = part.geo.index ? part.geo.toNonIndexed() : part.geo;
        const cnt = g.getAttribute("position").count;
        const si = new Uint16Array(cnt * 4);
        const sw = new Float32Array(cnt * 4);
        const bi = boneIndexOf[part.bone];
        for (let i = 0; i < cnt; i++) { si[i * 4] = bi; sw[i * 4] = 1; }
        g.setAttribute("skinIndex", new THREE.Uint16BufferAttribute(si, 4));
        g.setAttribute("skinWeight", new THREE.Float32BufferAttribute(sw, 4));
        if (!g.attributes.uv) g.setAttribute("uv", new THREE.BufferAttribute(new Float32Array(cnt * 2), 2));
        if (g.attributes.normal) g.deleteAttribute("normal");
        geos.push(g);
    }
    let merged = geos.length > 1 ? mergeGeometries(geos, false)! : geos[0];
    try { merged = mergeVertices(merged, 1e-3); } catch { /* 溶接失敗は無視 */ }
    merged.computeVertexNormals();
    // 接地: 足元 y=0 へ
    merged.computeBoundingBox();
    merged.translate(0, -merged.boundingBox!.min.y, 0);
    return merged;
}

/** 走りサイクル (RUN_HIP/RUN_KNEE) を Quaternion(X 軸) トラックの AnimationClip に移植。 */
function buildCrewRunClip(): { clip: THREE.AnimationClip; duration: number } {
    const N = 16; // サンプル数 (滑らかさ)
    const duration = 0.6; // 1 サイクル秒
    const times = new Float32Array(N + 1);
    for (let k = 0; k <= N; k++) times[k] = (k / N) * duration;
    // X 軸回りの回転を Quaternion 配列 (xyzw*(N+1)) に。phase は 0..1。
    const quatTrack = (angleAt: (phase: number) => number): Float32Array => {
        const arr = new Float32Array((N + 1) * 4);
        const q = new THREE.Quaternion();
        const ax = new THREE.Vector3(1, 0, 0);
        for (let k = 0; k <= N; k++) {
            q.setFromAxisAngle(ax, angleAt(k / N));
            arr[k * 4] = q.x; arr[k * 4 + 1] = q.y; arr[k * 4 + 2] = q.z; arr[k * 4 + 3] = q.w;
        }
        return arr;
    };
    const tracks: THREE.KeyframeTrack[] = [];
    const push = (boneName: CrewBoneName, angleAt: (phase: number) => number) => {
        tracks.push(new THREE.QuaternionKeyframeTrack(`${boneName}.quaternion`, times as unknown as number[], quatTrack(angleAt) as unknown as number[]));
    };
    // 右脚基準・左脚は半サイクルずれ。脛は hip+knee の畳み。
    // 腕は脚と逆位相のポンプ (-hip*1.15)、肘は約 90° 固定 + 前振りで深く。
    const hip = (ph: number) => sampleCycle(RUN_HIP, ph);
    const knee = (ph: number) => sampleCycle(RUN_KNEE, ph);
    push("thighR", (ph) => hip(ph));
    push("shinR", (ph) => knee(ph)); // 子ボーンなので膝角そのものが畳み
    push("thighL", (ph) => hip(ph + 0.5));
    push("shinL", (ph) => knee(ph + 0.5));
    // 腕 (右脚と逆位相 = 左脚位相と同相に振ると自然な対角)
    const sh = (ph: number) => -hip(ph) * 1.15;
    const el = (ph: number) => 1.2 + 0.3 * Math.max(0, sh(ph)); // 肘 ~90° + 前振りで深く
    push("upperArmR", (ph) => sh(ph + 0.5)); // 右腕は右脚と逆位相
    push("foreArmR", (ph) => el(ph + 0.5));
    push("upperArmL", (ph) => sh(ph));
    push("foreArmL", (ph) => el(ph));
    const clip = new THREE.AnimationClip("crewRun", duration, tracks);
    return { clip, duration };
}

/** 雑魚モンスター (人型でないトゲ団子)。クルーと一目で区別できる赤い塊。 */
function buildBlobGeometry(): THREE.BufferGeometry {
    const parts: THREE.BufferGeometry[] = [];
    // Icosahedron は非インデックス、Cone/Box はインデックス。mergeGeometries は混在を
    // 受け付けず null を返す → 全パーツを非インデックスに揃えてからマージする。
    const add = (geo: THREE.BufferGeometry, hex: number): void => {
        parts.push(paint(geo.index ? geo.toNonIndexed() : geo, hex));
    };
    const body = new THREE.IcosahedronGeometry(0.44, 0);
    body.translate(0, 0.46, 0);
    add(body, 0xd14b3a);
    // トゲ (上〜斜め前に突き出す)
    const spikes: [number, number, number][] = [
        [0, 0.95, 0],
        [0.36, 0.6, 0.18],
        [-0.36, 0.6, 0.18],
        [0.22, 0.55, -0.34],
        [-0.22, 0.55, -0.34],
    ];
    for (const [x, y, z] of spikes) {
        const sp = new THREE.ConeGeometry(0.12, 0.34, 5);
        sp.translate(x, y + 0.16, z);
        add(sp, 0x7a241a);
    }
    // 怒り目
    for (const dx of [-0.16, 0.16]) {
        const eye = new THREE.BoxGeometry(0.12, 0.12, 0.08);
        eye.translate(dx, 0.52, 0.38);
        add(eye, 0xfff2c0);
    }
    return mergeGeometries(parts, false)!;
}

/** 武器ジオメトリ (tier 0 ピストル / 1 ライフル / 2 ミニガン)。前方 +z に銃身。 */
function buildWeaponGeometry(tier: number): THREE.BufferGeometry {
    const parts: THREE.BufferGeometry[] = [];
    if (tier <= 0) {
        const barrel = new THREE.BoxGeometry(0.09, 0.09, 0.34);
        barrel.translate(0, 0.6, 0.34);
        parts.push(paint(barrel, 0x23262c));
        const grip = new THREE.BoxGeometry(0.1, 0.2, 0.1);
        grip.translate(0, 0.5, 0.16);
        parts.push(paint(grip, 0x14161a));
    } else if (tier === 1) {
        const barrel = new THREE.BoxGeometry(0.1, 0.1, 0.6);
        barrel.translate(0, 0.62, 0.45);
        parts.push(paint(barrel, 0x23262c));
        const stock = new THREE.BoxGeometry(0.12, 0.16, 0.26);
        stock.translate(0, 0.58, 0.1);
        parts.push(paint(stock, 0x14161a));
    } else if (tier === 2) {
        const housing = new THREE.BoxGeometry(0.28, 0.26, 0.4);
        housing.translate(0, 0.6, 0.3);
        parts.push(paint(housing, 0x14161a));
        for (const dx of [-0.07, 0, 0.07]) {
            const b = new THREE.CylinderGeometry(0.04, 0.04, 0.5, 6);
            b.rotateX(Math.PI / 2);
            b.translate(dx, 0.62, 0.6);
            parts.push(paint(b, 0x2b2f36));
        }
        const mag = new THREE.BoxGeometry(0.14, 0.3, 0.14);
        mag.translate(-0.22, 0.5, 0.28);
        parts.push(paint(mag, 0x6a3fbf));
    } else if (tier === 3) {
        // tier 3 ロケキャノン: tier2 より大きくゴツい砲身 ＋ 太い弾頭。
        const tube = new THREE.CylinderGeometry(0.17, 0.17, 0.8, 8);
        tube.rotateX(Math.PI / 2);
        tube.translate(0, 0.62, 0.55);
        parts.push(paint(tube, 0x2b2f36));
        const grip = new THREE.BoxGeometry(0.16, 0.24, 0.18);
        grip.translate(0, 0.5, 0.22);
        parts.push(paint(grip, 0x14161a));
        // 弾頭 (砲口から覗く赤い円錐)
        const tip = new THREE.ConeGeometry(0.16, 0.34, 8);
        tip.rotateX(Math.PI / 2);
        tip.translate(0, 0.62, 1.02);
        parts.push(paint(tip, 0xff7a3b));
    } else if (tier === 4) {
        // tier 4 ブーメラン: 手に持つ「く」の字型の小道具 (2 本のボックスを交差させる)。
        const armA = new THREE.BoxGeometry(0.1, 0.08, 0.42);
        armA.rotateY(0.6);
        armA.translate(0.06, 0.6, 0.3);
        parts.push(paint(armA, 0x8fffcf));
        const armB = new THREE.BoxGeometry(0.1, 0.08, 0.42);
        armB.rotateY(-0.6);
        armB.translate(-0.06, 0.6, 0.3);
        parts.push(paint(armB, 0x6fe0b0));
    } else {
        // tier 5 追尾ミサイルランチャー: 肩載せの箱型ポッド ＋ 4 連の発射口 ＋ 上部の照準。
        const pod = new THREE.BoxGeometry(0.3, 0.24, 0.5);
        pod.translate(0, 0.62, 0.32);
        parts.push(paint(pod, 0x2b3540));
        for (const [dx, dy] of [[-0.07, 0.07], [0.07, 0.07], [-0.07, -0.07], [0.07, -0.07]] as const) {
            const tube = new THREE.CylinderGeometry(0.05, 0.05, 0.5, 6);
            tube.rotateX(Math.PI / 2);
            tube.translate(dx, 0.62 + dy, 0.56);
            parts.push(paint(tube, 0x9fe8ff));
        }
        const sight = new THREE.BoxGeometry(0.06, 0.1, 0.1);
        sight.translate(0, 0.78, 0.34);
        parts.push(paint(sight, 0x14161a));
        const grip = new THREE.BoxGeometry(0.12, 0.2, 0.12);
        grip.translate(0, 0.5, 0.2);
        parts.push(paint(grip, 0x14161a));
    }
    return mergeGeometries(parts, false)!;
}

// ---------- アイテムの3Dモデル (拾い物・横向きプロフィール) ----------

function metalMat(hex: number): THREE.MeshLambertMaterial {
    return new THREE.MeshLambertMaterial({ color: hex });
}

/** ライフル銃 (銃身は X 方向)。 */
function buildGunModel(): THREE.Object3D {
    const g = new THREE.Group();
    const dark = 0x2b2f36;
    const barrel = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.13, 0.13), metalMat(dark));
    barrel.position.x = 0.35;
    g.add(barrel);
    const receiver = new THREE.Mesh(new THREE.BoxGeometry(0.55, 0.26, 0.18), metalMat(0x3a3f48));
    g.add(receiver);
    const mag = new THREE.Mesh(new THREE.BoxGeometry(0.16, 0.36, 0.14), metalMat(0xffd75e));
    mag.position.set(-0.05, -0.3, 0);
    g.add(mag);
    const stock = new THREE.Mesh(new THREE.BoxGeometry(0.45, 0.22, 0.13), metalMat(0x14161a));
    stock.position.x = -0.45;
    g.add(stock);
    const scope = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.1, 0.1), metalMat(0x14161a));
    scope.position.set(0.05, 0.2, 0);
    g.add(scope);
    return g;
}

/** ミニガン (太い多銃身)。 */
function buildMinigunModel(): THREE.Object3D {
    const g = new THREE.Group();
    const housing = new THREE.Mesh(new THREE.BoxGeometry(0.55, 0.42, 0.5), metalMat(0x14161a));
    g.add(housing);
    for (const dy of [-0.12, 0, 0.12]) {
        for (const dz of [-0.12, 0.12]) {
            const b = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, 1.1, 8), metalMat(0x2b2f36));
            b.rotation.z = Math.PI / 2;
            b.position.set(0.7, dy, dz);
            g.add(b);
        }
    }
    const plate = new THREE.Mesh(new THREE.CylinderGeometry(0.22, 0.22, 0.12, 8), metalMat(0xff9f43));
    plate.rotation.z = Math.PI / 2;
    plate.position.x = 0.28;
    g.add(plate);
    const mag = new THREE.Mesh(new THREE.BoxGeometry(0.2, 0.4, 0.2), metalMat(0x6a3fbf));
    mag.position.set(-0.25, -0.3, 0);
    g.add(mag);
    return g;
}

/** ロケキャノン (太い砲身＋赤い弾頭・横向き)。 */
function buildRocketLauncherModel(): THREE.Object3D {
    const g = new THREE.Group();
    const tube = new THREE.Mesh(new THREE.CylinderGeometry(0.22, 0.22, 1.5, 10), metalMat(0x2b2f36));
    tube.rotation.z = Math.PI / 2;
    tube.position.x = 0.2;
    g.add(tube);
    const back = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.26, 0.3, 10), metalMat(0x14161a));
    back.rotation.z = Math.PI / 2;
    back.position.x = -0.6;
    g.add(back);
    const head = new THREE.Mesh(new THREE.ConeGeometry(0.22, 0.5, 10), metalMat(0xff7a3b));
    head.rotation.z = -Math.PI / 2;
    head.position.x = 0.95;
    g.add(head);
    const grip = new THREE.Mesh(new THREE.BoxGeometry(0.16, 0.34, 0.16), metalMat(0x14161a));
    grip.position.set(0.05, -0.32, 0);
    g.add(grip);
    return g;
}

/** 追尾ミサイルランチャーのアイテム表示モデル: 箱型ポッド + 4 連発射口 + 上部照準。 */
function buildHomingLauncherModel(): THREE.Object3D {
    const g = new THREE.Group();
    const pod = new THREE.Mesh(new THREE.BoxGeometry(1.1, 0.8, 0.8), metalMat(0x2b3540));
    g.add(pod);
    for (const dy of [-0.2, 0.2]) for (const dz of [-0.2, 0.2]) {
        const tube = new THREE.Mesh(new THREE.CylinderGeometry(0.13, 0.13, 0.7, 8), metalMat(0x9fe8ff));
        tube.rotation.z = Math.PI / 2;
        tube.position.set(0.6, dy, dz);
        g.add(tube);
    }
    const sight = new THREE.Mesh(new THREE.TorusGeometry(0.18, 0.04, 6, 14), metalMat(0xff4d4d));
    sight.position.set(0.1, 0.5, 0);
    g.add(sight);
    const grip = new THREE.Mesh(new THREE.BoxGeometry(0.18, 0.36, 0.18), metalMat(0x14161a));
    grip.position.set(0, -0.5, 0);
    g.add(grip);
    return g;
}

/** 360°タレットのアイテム表示モデル: 三脚 + 旋回ヘッド + 砲身 + 金トリム + 光るコア。 */
function buildTurretItemModel(): THREE.Object3D {
    const g = new THREE.Group();
    for (let i = 0; i < 3; i++) {
        const a = (i / 3) * Math.PI * 2;
        const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 0.7, 6), metalMat(0x3c4a5e));
        leg.position.set(Math.cos(a) * 0.32, -0.35, Math.sin(a) * 0.32);
        leg.rotation.z = Math.cos(a) * 0.28; leg.rotation.x = -Math.sin(a) * 0.28;
        g.add(leg);
    }
    const head = new THREE.Mesh(new THREE.BoxGeometry(0.6, 0.42, 0.6), metalMat(0x3c4a5e));
    head.position.y = 0.1; g.add(head);
    const core = new THREE.Mesh(new THREE.SphereGeometry(0.14, 12, 10), new THREE.MeshBasicMaterial({ color: 0xffe24a }));
    core.position.set(0, 0.16, 0); g.add(core);
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.12, 0.8, 12), metalMat(0xc8a23a));
    barrel.rotation.x = Math.PI / 2; barrel.position.set(0, 0.12, 0.5); g.add(barrel);
    for (const dx of [-0.4, 0.4]) {
        const drum = new THREE.Mesh(new THREE.CylinderGeometry(0.15, 0.15, 0.3, 12), metalMat(0xc8a23a));
        drum.rotation.z = Math.PI / 2; drum.position.set(dx, 0.12, -0.05); g.add(drum);
    }
    return g;
}

/** ガンドローンのアイテム表示モデル: 小さな飛行ドローン (本体 + 目 + フィン + 砲身)。 */
function buildDroneItemModel(): THREE.Object3D {
    const g = new THREE.Group();
    const hull = new THREE.Mesh(new THREE.SphereGeometry(0.5, 14, 12), metalMat(0x46566a));
    hull.scale.set(1, 0.7, 1); g.add(hull);
    const eye = new THREE.Mesh(new THREE.SphereGeometry(0.18, 10, 10), new THREE.MeshBasicMaterial({ color: 0x6fe0ff }));
    eye.position.set(0, 0.12, 0.3); g.add(eye);
    for (const s of [-1, 1]) {
        const fin = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.08, 0.2), metalMat(0x9fc8e8));
        fin.position.set(s * 0.55, 0.15, 0); g.add(fin);
    }
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.1, 0.7, 10), metalMat(0x9fc8e8));
    barrel.rotation.x = Math.PI / 2; barrel.position.set(0, -0.05, 0.5); g.add(barrel);
    return g;
}

/** アーマー (六角の盾＋十字エンブレム)。 */
function buildArmorModel(): THREE.Object3D {
    const g = new THREE.Group();
    const shield = new THREE.Mesh(new THREE.CylinderGeometry(0.62, 0.62, 0.16, 6), metalMat(0x7bed9f));
    shield.rotation.x = Math.PI / 2;
    g.add(shield);
    const rim = new THREE.Mesh(new THREE.TorusGeometry(0.6, 0.07, 8, 6), metalMat(0x3fae6a));
    rim.position.z = 0.02;
    g.add(rim);
    const v = new THREE.Mesh(new THREE.BoxGeometry(0.12, 0.7, 0.04), metalMat(0xffffff));
    v.position.z = 0.1;
    g.add(v);
    const h = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.12, 0.04), metalMat(0xffffff));
    h.position.z = 0.1;
    g.add(h);
    return g;
}

/** シールド (光る半球バリア + 中央のエネルギーコア)。armor の盾とは別物の「バブル」。 */
function buildShieldModel(): THREE.Object3D {
    const g = new THREE.Group();
    const dome = new THREE.Mesh(
        new THREE.SphereGeometry(0.6, 14, 10, 0, Math.PI * 2, 0, Math.PI * 0.6),
        new THREE.MeshBasicMaterial({ color: 0x7bffea, transparent: true, opacity: 0.4, side: THREE.DoubleSide }),
    );
    dome.position.y = -0.1;
    g.add(dome);
    const rim = new THREE.Mesh(new THREE.TorusGeometry(0.58, 0.05, 8, 24), metalMat(0x2fd8c0));
    rim.rotation.x = Math.PI / 2;
    rim.position.y = -0.1;
    g.add(rim);
    const core = new THREE.Mesh(new THREE.OctahedronGeometry(0.22, 0), new THREE.MeshBasicMaterial({ color: 0xeafffb }));
    core.position.y = 0.12;
    g.add(core);
    return g;
}

/** 波動砲 (エレキの大砲台)。前向きの砲身 + 青いコイルリング + 光る砲口。 */
function buildCannonModel(): THREE.Object3D {
    const g = new THREE.Group();
    const base = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.4, 0.9), metalMat(0x3a4452));
    base.position.y = 0.3; g.add(base);
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.26, 0.3, 1.0, 12), metalMat(0x5a6678));
    barrel.rotation.x = Math.PI / 2; barrel.position.set(0, 0.5, 0.5); g.add(barrel);
    for (const z of [0.2, 0.45, 0.7]) {
        const coil = new THREE.Mesh(new THREE.TorusGeometry(0.32, 0.05, 8, 16), new THREE.MeshBasicMaterial({ color: 0x49e0ff }));
        coil.rotation.x = Math.PI / 2; coil.position.set(0, 0.5, z); g.add(coil);
    }
    const muzzle = new THREE.Mesh(new THREE.SphereGeometry(0.22, 12, 10), new THREE.MeshBasicMaterial({ color: 0xaef6ff, transparent: true, opacity: 0.85 }));
    muzzle.position.set(0, 0.5, 1.05); g.add(muzzle);
    return g;
}

/** 召喚用の巨大 SF キャノン: 太い主砲身・エネルギーコイル・サイドベント・冷却フィン・先端発光リング。
 *  青白エネルギー色。前面で「盾＆主砲」に見えるシルエット。最後に muzzle を追加する点は buildCannonModel と
 *  同じ約束 (updateCannon が children 末尾を脈動発光させるため)。 */
function buildGiantCannonModel(): THREE.Object3D {
    const g = new THREE.Group();
    const hullMat = metalMat(0x2c3a4c);
    const trimMat = metalMat(0x4a5e76);
    const glowMat = new THREE.MeshBasicMaterial({ color: 0x49e0ff });
    // 台座 (ワイドで盾らしい)
    const base = new THREE.Mesh(new THREE.BoxGeometry(1.3, 0.55, 1.1), hullMat);
    base.position.y = 0.35; g.add(base);
    // 前面シールドプレート (盾らしい大きな面)
    const plate = new THREE.Mesh(new THREE.BoxGeometry(1.5, 1.0, 0.16), trimMat);
    plate.position.set(0, 0.7, 0.55); g.add(plate);
    const plateGlow = new THREE.Mesh(new THREE.BoxGeometry(1.1, 0.62, 0.04), new THREE.MeshBasicMaterial({ color: 0x49e0ff, transparent: true, opacity: 0.55 }));
    plateGlow.position.set(0, 0.72, 0.64); g.add(plateGlow);
    // 太い主砲身
    const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.42, 0.5, 1.6, 16), trimMat);
    barrel.rotation.x = Math.PI / 2; barrel.position.set(0, 0.8, 0.9); g.add(barrel);
    // エネルギーコイル (砲身に巻く)
    for (const z of [0.45, 0.75, 1.05, 1.35]) {
        const coil = new THREE.Mesh(new THREE.TorusGeometry(0.5, 0.07, 8, 18), glowMat);
        coil.rotation.x = Math.PI / 2; coil.position.set(0, 0.8, z); g.add(coil);
    }
    // サイドベント (左右の張り出し)
    for (const dx of [-0.78, 0.78]) {
        const vent = new THREE.Mesh(new THREE.BoxGeometry(0.28, 0.7, 0.8), hullMat);
        vent.position.set(dx, 0.7, 0.7); g.add(vent);
        const slit = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.5, 0.5), new THREE.MeshBasicMaterial({ color: 0x49e0ff, transparent: true, opacity: 0.7 }));
        slit.position.set(dx + (dx < 0 ? 0.15 : -0.15), 0.7, 0.7); g.add(slit);
    }
    // 冷却フィン (上面に並ぶ)
    for (const dx of [-0.3, 0, 0.3]) {
        const fin = new THREE.Mesh(new THREE.BoxGeometry(0.12, 0.32, 0.7), trimMat);
        fin.position.set(dx, 1.25, 0.4); g.add(fin);
    }
    // 先端の発光リング
    const ring = new THREE.Mesh(new THREE.TorusGeometry(0.46, 0.1, 10, 22), new THREE.MeshBasicMaterial({ color: 0xaef6ff, transparent: true, opacity: 0.9 }));
    ring.rotation.x = Math.PI / 2; ring.position.set(0, 0.8, 1.72); g.add(ring);
    // 砲口コア (最後 = updateCannon が脈動発光させる末尾 child)
    const muzzle = new THREE.Mesh(new THREE.SphereGeometry(0.4, 16, 12), new THREE.MeshBasicMaterial({ color: 0xaef6ff, transparent: true, opacity: 0.85 }));
    muzzle.position.set(0, 0.8, 1.78); g.add(muzzle);
    return g;
}

/** 病院器具モデル: kind 0=車椅子 / 1=点滴スタンド / 2=ストレッチャー。襲撃の障害物と廊下の静物で共用。 */
function buildHospitalPropModel(kind: number): THREE.Group {
    const grp = new THREE.Group();
    if (kind === 0) { // 車椅子
        const seat = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.1, 0.7), new THREE.MeshLambertMaterial({ color: 0x3a3a44 }));
        seat.position.y = 0.6; grp.add(seat);
        const back = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.8, 0.1), new THREE.MeshLambertMaterial({ color: 0x2a2a34 }));
        back.position.set(0, 1.0, -0.3); grp.add(back);
        for (const dx of [-0.42, 0.42]) {
            const w = new THREE.Mesh(new THREE.TorusGeometry(0.32, 0.05, 6, 14), new THREE.MeshLambertMaterial({ color: 0x111114 }));
            w.position.set(dx, 0.32, 0); grp.add(w);
        }
    } else if (kind === 1) { // 点滴スタンド (血のパック)
        const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 2.3, 6), new THREE.MeshLambertMaterial({ color: 0x8a8a92 }));
        pole.position.y = 1.15; grp.add(pole);
        const bag = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.42, 0.1), new THREE.MeshLambertMaterial({ color: 0x9a1f1f }));
        bag.position.set(0.22, 1.95, 0); grp.add(bag);
        const base = new THREE.Mesh(new THREE.CylinderGeometry(0.42, 0.42, 0.1, 8), new THREE.MeshLambertMaterial({ color: 0x6a6a72 }));
        base.position.y = 0.05; grp.add(base);
    } else { // ストレッチャー (血の染みたシーツ)
        const bed = new THREE.Mesh(new THREE.BoxGeometry(0.95, 0.2, 2.0), new THREE.MeshLambertMaterial({ color: 0x4a4a54 }));
        bed.position.y = 0.85; grp.add(bed);
        const sheet = new THREE.Mesh(new THREE.BoxGeometry(0.97, 0.1, 1.4), new THREE.MeshLambertMaterial({ color: 0x7a2424 }));
        sheet.position.y = 0.97; grp.add(sheet);
        for (const dz of [-0.8, 0.8]) for (const dx of [-0.42, 0.42]) {
            const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.85, 6), new THREE.MeshLambertMaterial({ color: 0x222226 }));
            leg.position.set(dx, 0.42, dz); grp.add(leg);
        }
    }
    return grp;
}

/** アイテム種別に応じた表示モデル。 */
function buildItemModel(item: ItemKind): THREE.Object3D {
    switch (item) {
        case "gun":
            return buildGunModel();
        case "minigun":
            return buildMinigunModel();
        case "armor":
        case "rarearmor":
        case "partyarmor":
            return buildArmorModel();
        case "shield":
            return buildShieldModel();
        case "cannon":
            return buildCannonModel();
        case "rocket":
            return buildRocketLauncherModel();
        case "homing":
            return buildHomingLauncherModel();
        case "turret":
            return buildTurretItemModel();
        case "drones":
            return buildDroneItemModel();
        default: {
            // robot (+ bonus/heal never reach here — they build their own meshes inline)
            const m = new THREE.Mesh(buildRobotGeometry(), new THREE.MeshLambertMaterial({ vertexColors: true }));
            m.scale.setScalar(0.55);
            m.position.y = -0.55;
            return m;
        }
    }
}

// ---------- テクスチャ ----------

/** 草原の中の土の小道テクスチャ (走行レーン)。砂土ベース＋ムラ＋小石。 */
function makeTrailTexture(): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = 128;
    c.height = 128;
    const x = c.getContext("2d")!;
    x.fillStyle = "#c9a86b";
    x.fillRect(0, 0, 128, 128);
    // 土のムラ
    for (let i = 0; i < 70; i++) {
        x.fillStyle = Math.random() < 0.5 ? "rgba(176,142,86,0.45)" : "rgba(216,190,144,0.45)";
        x.beginPath();
        x.arc(Math.random() * 128, Math.random() * 128, 4 + Math.random() * 11, 0, 6.28);
        x.fill();
    }
    // 小石
    for (let i = 0; i < 34; i++) {
        x.fillStyle = Math.random() < 0.5 ? "#9c8050" : "#ece0c0";
        const s = 1.5 + Math.random() * 2.5;
        x.fillRect(Math.random() * 128, Math.random() * 128, s, s);
    }
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    return tex;
}

/** 木の横木フェンス (透過)。横レール2本＋等間隔の縦支柱。U=道に沿う方向で繰り返す。 */
function makeFenceTexture(): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = 128;
    c.height = 128;
    const x = c.getContext("2d")!;
    x.clearRect(0, 0, 128, 128); // 透明背景
    // 横レール 2 本 (V 方向＝高さ)。U 全体に連続。
    for (const [y0, h] of [[34, 20], [78, 20]] as [number, number][]) {
        x.fillStyle = "#c8924e";
        x.fillRect(0, y0, 128, h);
        x.fillStyle = "#a3702f"; // 下側の陰
        x.fillRect(0, y0 + h - 5, 128, 5);
    }
    // 縦支柱 (U の先頭にだけ描く → repeat で等間隔に並ぶ)
    x.fillStyle = "#b07d3f";
    x.fillRect(2, 6, 18, 116);
    x.fillStyle = "#8a5a2b";
    x.fillRect(16, 6, 4, 116);
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    return tex;
}

// ---------- バイオームのシーナリー (装飾プロップ) ----------
// 全プロップは paint() の頂点カラーで色を持ち、mergeGeometries で 1 ジオメトリに焼く。
// Icosahedron(非インデックス) と Cone/Box/Cylinder/Sphere(インデックス) を混在させるため
// buildBlobGeometry と同様に全パーツを非インデックスに揃えてからマージする。
function mergeProps(items: [THREE.BufferGeometry, number][]): THREE.BufferGeometry {
    const parts: THREE.BufferGeometry[] = [];
    for (const [geo, hex] of items) parts.push(paint(geo.index ? geo.toNonIndexed() : geo, hex));
    return mergeGeometries(parts, false)!;
}

/** POOL A: 大きい縦長プロップ (森=木 / 砂漠=サボテン / 雪=樹氷 / 海=ヤシ)。 */
function biomePropA(biome: number): THREE.BufferGeometry {
    const it: [THREE.BufferGeometry, number][] = [];
    const at = (g: THREE.BufferGeometry, x: number, y: number, z = 0): THREE.BufferGeometry => {
        g.translate(x, y, z);
        return g;
    };
    if (biome === 0) {
        // 森: 丸/もみの木
        it.push([at(new THREE.CylinderGeometry(0.18, 0.26, 2.2, 6), 0, 1.1), 0x6b4a2a]);
        it.push([at(new THREE.ConeGeometry(1.1, 1.6, 7), 0, 2.4), 0x3f7d35]);
        it.push([at(new THREE.ConeGeometry(0.85, 1.3, 7), 0, 3.2), 0x4f9442]);
        it.push([at(new THREE.ConeGeometry(0.55, 1.0, 7), 0, 3.9), 0x3f7d35]);
    } else if (biome === 1) {
        // 砂漠: サワロサボテン
        it.push([at(new THREE.CylinderGeometry(0.34, 0.4, 2.6, 7), 0, 1.3), 0x4f8f54]);
        const armA = new THREE.CylinderGeometry(0.16, 0.18, 0.7, 6);
        armA.rotateZ(Math.PI / 2);
        it.push([at(armA, -0.45, 1.6), 0x3d7242]);
        it.push([at(new THREE.CylinderGeometry(0.16, 0.18, 0.8, 6), -0.8, 2.1), 0x4f8f54]);
        const armB = new THREE.CylinderGeometry(0.16, 0.18, 0.7, 6);
        armB.rotateZ(-Math.PI / 2);
        it.push([at(armB, 0.45, 1.9), 0x3d7242]);
        it.push([at(new THREE.CylinderGeometry(0.16, 0.18, 0.8, 6), 0.8, 2.4), 0x4f8f54]);
        it.push([at(new THREE.SphereGeometry(0.12, 8, 6), -0.8, 2.5), 0xf2a0c0]);
        it.push([at(new THREE.SphereGeometry(0.12, 8, 6), 0.8, 2.8), 0xf2a0c0]);
    } else if (biome === 2) {
        // 雪: 樹氷した松 (もみの木に白い雪帽子)
        it.push([at(new THREE.CylinderGeometry(0.18, 0.26, 2.2, 6), 0, 1.1), 0x5a4632]);
        it.push([at(new THREE.ConeGeometry(1.1, 1.6, 7), 0, 2.4), 0x356b4a]);
        it.push([at(new THREE.ConeGeometry(1.2, 0.25, 7), 0, 3.25), 0xffffff]);
        it.push([at(new THREE.ConeGeometry(0.85, 1.3, 7), 0, 3.2), 0x2c5a3e]);
        it.push([at(new THREE.ConeGeometry(0.95, 0.25, 7), 0, 3.9), 0xffffff]);
        it.push([at(new THREE.ConeGeometry(0.55, 1.0, 7), 0, 3.9), 0x356b4a]);
        it.push([at(new THREE.ConeGeometry(0.65, 0.25, 7), 0, 4.45), 0xffffff]);
    } else if (biome === 3) {
        // 海: ヤシ
        it.push([at(new THREE.CylinderGeometry(0.2, 0.2, 0.7, 6), -0.05, 0.35), 0x9c7a4a]);
        it.push([at(new THREE.CylinderGeometry(0.18, 0.2, 0.7, 6), 0.05, 1.05), 0x9c7a4a]);
        it.push([at(new THREE.CylinderGeometry(0.14, 0.18, 0.7, 6), 0.18, 1.75), 0x9c7a4a]);
        for (let i = 0; i < 6; i++) {
            const a = (i / 6) * Math.PI * 2;
            const frond = new THREE.ConeGeometry(0.25, 1.4, 4);
            frond.rotateZ(Math.PI / 6); // 垂れ下げ
            frond.rotateY(a);
            it.push([at(frond, 0.18 + Math.cos(a) * 0.6, 2.0, Math.sin(a) * 0.6), i % 2 ? 0x4f9c5a : 0x3d7a44]);
        }
        it.push([at(new THREE.SphereGeometry(0.14, 8, 6), 0.18, 2.0), 0x6b4a2a]);
        it.push([at(new THREE.SphereGeometry(0.14, 8, 6), 0.4, 1.95), 0x6b4a2a]);
    } else if (biome === 4) {
        // 火山: 焼け焦げた枯れ木 (黒い幹 + 赤熱の裂け目)
        it.push([at(new THREE.CylinderGeometry(0.16, 0.28, 2.6, 6), 0, 1.3), 0x2a1c18]);
        const fork = (dx: number, h: number, lean: number) => {
            const br = new THREE.CylinderGeometry(0.07, 0.13, h, 5);
            br.rotateZ(lean);
            it.push([at(br, dx, 2.3 + h * 0.35), 0x241612]);
        };
        fork(-0.4, 1.1, 0.5); fork(0.45, 1.3, -0.45); fork(0.05, 1.0, 0.05);
        it.push([at(new THREE.SphereGeometry(0.12, 6, 5), 0.05, 1.0), 0xff6a2e]); // 根元の残り火
        it.push([at(new THREE.SphereGeometry(0.1, 6, 5), -0.18, 0.55), 0xffa040]);
    } else if (biome === 5) {
        // 墓地: ねじれた枯れ木 (月夜のシルエット)
        it.push([at(new THREE.CylinderGeometry(0.14, 0.24, 2.4, 6), 0, 1.2), 0x2c2a32]);
        for (let i = 0; i < 5; i++) {
            const a = (i / 5) * Math.PI * 2;
            const br = new THREE.CylinderGeometry(0.05, 0.1, 1.0 + (i % 2) * 0.5, 5);
            br.rotateZ((i % 2 ? 1 : -1) * 0.7);
            br.rotateY(a);
            it.push([at(br, Math.cos(a) * 0.3, 2.3 + (i % 3) * 0.25, Math.sin(a) * 0.3), 0x26242c]);
        }
    } else if (biome === 6) {
        // 空: 浮遊するクリスタル柱
        const shard = new THREE.ConeGeometry(0.5, 2.4, 6);
        it.push([at(shard, 0, 1.4), 0xbfe0ff]);
        const tip = new THREE.ConeGeometry(0.32, 1.0, 6);
        tip.rotateZ(Math.PI);
        it.push([at(tip, 0, 0.5), 0x9fc8f0]);
        it.push([at(new THREE.OctahedronGeometry(0.34, 0), 0.0, 3.0), 0xeaf6ff]);
        const cloud = new THREE.SphereGeometry(0.7, 8, 6);
        cloud.scale(1.5, 0.5, 1.2);
        it.push([at(cloud, 0, 0.1), 0xffffff]);
    } else if (biome === 7) {
        // 空港: 案内サインの柱
        it.push([at(new THREE.CylinderGeometry(0.1, 0.1, 2.8, 8), 0, 1.4), 0x8a96a2]);
        it.push([at(new THREE.BoxGeometry(1.5, 0.7, 0.12), 0, 2.5), 0x2a3a4a]); // 暗いサイン板
        it.push([at(new THREE.BoxGeometry(1.3, 0.1, 0.14), 0, 2.75), 0xffd23b]); // 黄ストライプ
        it.push([at(new THREE.BoxGeometry(0.6, 0.4, 0.13), -0.4, 2.45), 0x6fd2ff]); // 青ピクト
    } else if (biome === 8) {
        // モール: 観葉植物の鉢
        it.push([at(new THREE.CylinderGeometry(0.42, 0.52, 0.6, 10), 0, 0.3), 0xb09878]); // 鉢
        it.push([at(new THREE.SphereGeometry(0.75, 8, 6), 0, 1.05), 0x4f8f54]);
        it.push([at(new THREE.SphereGeometry(0.55, 8, 6), 0.32, 1.55), 0x5fa564]);
        it.push([at(new THREE.SphereGeometry(0.5, 8, 6), -0.3, 1.45), 0x3d7a44]);
    } else {
        // 予備 (未定義バイオーム) — 森の木で代替
        it.push([at(new THREE.CylinderGeometry(0.18, 0.26, 2.2, 6), 0, 1.1), 0x6b4a2a]);
        it.push([at(new THREE.ConeGeometry(1.0, 1.6, 7), 0, 2.4), 0x3f7d35]);
    }
    return mergeProps(it);
}

/** POOL B: 地面のクラッター (森=茂み / 砂漠=枯木と岩 / 雪=雪玉 / 海=海岩/ブイ)。 */
function biomePropB(biome: number): THREE.BufferGeometry {
    const it: [THREE.BufferGeometry, number][] = [];
    const at = (g: THREE.BufferGeometry, x: number, y: number, z = 0): THREE.BufferGeometry => {
        g.translate(x, y, z);
        return g;
    };
    if (biome === 0) {
        // 森: 茂み (潰した球を 2〜3 個)
        const b1 = new THREE.SphereGeometry(0.5, 6, 5);
        b1.scale(1, 0.6, 1);
        it.push([at(b1, -0.2, 0.3), 0x3f7d35]);
        const b2 = new THREE.SphereGeometry(0.5, 6, 5);
        b2.scale(1, 0.6, 1);
        it.push([at(b2, 0.25, 0.32, 0.1), 0x3f7d35]);
        const b3 = new THREE.SphereGeometry(0.4, 6, 5);
        b3.scale(1, 0.6, 1);
        it.push([at(b3, 0.0, 0.46), 0x6fae3d]);
    } else if (biome === 1) {
        // 砂漠: 乾いた低木/岩
        it.push([at(new THREE.IcosahedronGeometry(0.5, 0), 0, 0.4), 0xc98a52]);
        for (let i = 0; i < 3; i++) {
            const tw = new THREE.ConeGeometry(0.04, 0.6, 4);
            tw.rotateZ((i - 1) * 0.4);
            it.push([at(tw, (i - 1) * 0.18, 0.6), 0xa88c5a]);
        }
    } else if (biome === 2) {
        // 雪: 雪の岩
        it.push([at(new THREE.IcosahedronGeometry(0.6, 0), 0, 0.45), 0x8794a2]);
        const cap = new THREE.SphereGeometry(0.6, 8, 5);
        cap.scale(1, 0.4, 1);
        it.push([at(cap, 0, 0.72), 0xffffff]);
    } else if (biome === 3) {
        // 海: 海岩 + ブイ
        it.push([at(new THREE.IcosahedronGeometry(0.5, 0), -0.3, 0.4), 0x6f7d7a]);
        it.push([at(new THREE.CapsuleGeometry(0.3, 0.4, 4, 8), 0.4, 0.6), 0xe0524f]);
        const band = new THREE.CylinderGeometry(0.31, 0.31, 0.16, 8);
        it.push([at(band, 0.4, 0.6), 0xffffff]);
    } else if (biome === 4) {
        // 火山: 赤熱した溶岩岩
        it.push([at(new THREE.IcosahedronGeometry(0.6, 0), 0, 0.45), 0x33211c]);
        const glow = new THREE.IcosahedronGeometry(0.3, 0);
        it.push([at(glow, 0.18, 0.6), 0xff5a1e]);
        it.push([at(new THREE.SphereGeometry(0.12, 6, 5), -0.25, 0.5), 0xffb040]);
    } else if (biome === 5) {
        // 墓地: 墓石
        const stone = new THREE.BoxGeometry(0.7, 0.9, 0.16);
        it.push([at(stone, 0, 0.55), 0x8a8f98]);
        const top = new THREE.CylinderGeometry(0.35, 0.35, 0.16, 10, 1, false, 0, Math.PI);
        top.rotateX(Math.PI / 2);
        it.push([at(top, 0, 1.0), 0x9aa0a8]);
        it.push([at(new THREE.SphereGeometry(0.4, 6, 5).scale(1, 0.4, 1) as THREE.BufferGeometry, 0, 0.15), 0x4a5240]); // 土盛り
    } else if (biome === 6) {
        // 空: ふわふわ雲
        const c1 = new THREE.SphereGeometry(0.5, 7, 6); c1.scale(1.3, 0.7, 1);
        it.push([at(c1, -0.3, 0.45), 0xffffff]);
        const c2 = new THREE.SphereGeometry(0.55, 7, 6); c2.scale(1.3, 0.7, 1);
        it.push([at(c2, 0.3, 0.5), 0xeef6ff]);
        const c3 = new THREE.SphereGeometry(0.42, 7, 6); c3.scale(1.3, 0.7, 1);
        it.push([at(c3, 0.0, 0.62), 0xffffff]);
    } else if (biome === 7) {
        // 空港: ベンチ (連結シート)
        it.push([at(new THREE.BoxGeometry(1.6, 0.12, 0.5), 0, 0.45), 0x4a5562]); // 座面
        it.push([at(new THREE.BoxGeometry(1.6, 0.4, 0.1), 0, 0.7, -0.2), 0x5a6672]); // 背
        for (const dx of [-0.6, 0.6]) it.push([at(new THREE.BoxGeometry(0.1, 0.45, 0.45), dx, 0.22), 0x6a7682]); // 脚
    } else if (biome === 8) {
        // モール: プランター + ベンチ
        it.push([at(new THREE.BoxGeometry(1.2, 0.5, 0.6), 0, 0.25), 0xb09878]); // 木箱プランター
        it.push([at(new THREE.SphereGeometry(0.5, 8, 6).scale(1, 0.6, 1) as THREE.BufferGeometry, 0, 0.6), 0x5fa564]); // 植栽
        it.push([at(new THREE.SphereGeometry(0.3, 8, 6), 0.3, 0.85), 0x4f8f54]);
    } else {
        it.push([at(new THREE.IcosahedronGeometry(0.5, 0), 0, 0.4), 0x6f7d7a]); // 予備の岩
    }
    return mergeProps(it);
}

/** POOL C: 背の高い疎なランドマーク (森=倒木 / … / 空=浮島 / 空港=ゲートカウンター / モール=エスカレーター)。 */
function biomePropC(biome: number): THREE.BufferGeometry {
    const it: [THREE.BufferGeometry, number][] = [];
    const at = (g: THREE.BufferGeometry, x: number, y: number, z = 0): THREE.BufferGeometry => {
        g.translate(x, y, z);
        return g;
    };
    if (biome === 0) {
        // 森: 倒木
        const log = new THREE.CylinderGeometry(0.3, 0.34, 2.4, 7);
        log.rotateZ(Math.PI / 2);
        it.push([at(log, 0, 0.4), 0x6b4a2a]);
        const capA = new THREE.CylinderGeometry(0.3, 0.3, 0.1, 7);
        capA.rotateZ(Math.PI / 2);
        it.push([at(capA, 1.2, 0.4), 0x4a331d]);
        const capB = new THREE.CylinderGeometry(0.34, 0.34, 0.1, 7);
        capB.rotateZ(Math.PI / 2);
        it.push([at(capB, -1.2, 0.4), 0x4a331d]);
    } else if (biome === 1) {
        // 砂漠: メサ (段重ねの箱)
        it.push([at(new THREE.BoxGeometry(3, 1.4, 2), 0, 0.7), 0xb5743f]);
        it.push([at(new THREE.BoxGeometry(2.4, 0.8, 1.6), 0, 1.8), 0xc98a52]);
    } else if (biome === 2) {
        // 雪: 氷山/雪山
        it.push([at(new THREE.ConeGeometry(1.4, 3, 5), 0, 1.5), 0xdfeaf4]);
        it.push([at(new THREE.ConeGeometry(0.5, 0.8, 5), 0, 3.1), 0xffffff]);
        const base = new THREE.SphereGeometry(1.2, 8, 6);
        base.scale(1.4, 0.4, 1.4);
        it.push([at(base, 0, 0.25), 0x8794a2]);
    } else if (biome === 3) {
        // 海: 砂の島 + ヤシのシルエット
        const mound = new THREE.SphereGeometry(3, 10, 6);
        mound.scale(1, 0.3, 1);
        it.push([at(mound, 0, 0.0), 0xe6cf94]);
        const cap = new THREE.SphereGeometry(1, 8, 6);
        it.push([at(cap, 0, 0.8), 0x4f9c5a]);
        it.push([at(new THREE.CylinderGeometry(0.16, 0.2, 1.6, 6), 1.4, 1.4), 0x9c7a4a]);
        const frond = new THREE.SphereGeometry(0.8, 8, 6);
        frond.scale(1, 0.4, 1);
        it.push([at(frond, 1.4, 2.3), 0x4f9c5a]);
    } else if (biome === 4) {
        // 火山: 溶岩尖塔 (赤熱の縞)
        it.push([at(new THREE.ConeGeometry(1.2, 3.4, 6), 0, 1.7), 0x2e1e1a]);
        it.push([at(new THREE.ConeGeometry(0.5, 1.0, 6), 0.3, 3.2), 0x3a2620]);
        const lava = new THREE.ConeGeometry(0.4, 0.8, 6);
        it.push([at(lava, 0, 3.0), 0xff5a1e]); // 火口の輝き
        const base = new THREE.SphereGeometry(1.3, 8, 6);
        base.scale(1.4, 0.35, 1.4);
        it.push([at(base, 0, 0.2), 0x33211c]);
        it.push([at(new THREE.SphereGeometry(0.2, 6, 5), -0.6, 0.4), 0xff7a2e]);
    } else if (biome === 5) {
        // 墓地: 霊廟 (石造りの小屋 + 屋根)
        it.push([at(new THREE.BoxGeometry(2.2, 2.0, 2.2), 0, 1.0), 0x6a6f7a]);
        const roof = new THREE.ConeGeometry(1.9, 1.1, 4);
        roof.rotateY(Math.PI / 4);
        it.push([at(roof, 0, 2.55), 0x4c5158]);
        it.push([at(new THREE.BoxGeometry(0.7, 1.3, 0.2), 0, 0.65, 1.1), 0x2e3138]); // 扉
        for (const dx of [-1.4, 1.4]) it.push([at(new THREE.CylinderGeometry(0.18, 0.2, 1.4, 6), dx, 0.7, 1.0), 0x7a808a]);
    } else if (biome === 6) {
        // 空: 雲に乗る浮島
        const cloud = new THREE.SphereGeometry(3, 10, 6);
        cloud.scale(1, 0.3, 1);
        it.push([at(cloud, 0, 0.0), 0xffffff]);
        const rock = new THREE.ConeGeometry(1.6, 2.0, 6);
        rock.rotateZ(Math.PI);
        it.push([at(rock, 0, -0.6), 0xcdd8ea]);
        const grass = new THREE.SphereGeometry(1.5, 8, 6);
        grass.scale(1, 0.4, 1);
        it.push([at(grass, 0, 0.5), 0xbfe0ff]);
        it.push([at(new THREE.OctahedronGeometry(0.5, 0), 0.4, 1.3), 0xeaf6ff]);
    } else if (biome === 7) {
        // 空港: ゲートカウンター + 大きな柱
        it.push([at(new THREE.BoxGeometry(2.6, 1.1, 1.0), 0, 0.55), 0xcfd6de]); // カウンター
        it.push([at(new THREE.BoxGeometry(2.7, 0.15, 1.1), 0, 1.15), 0x6fd2ff]); // 天面の青ライン
        it.push([at(new THREE.CylinderGeometry(0.4, 0.4, 3.6, 10), 1.8, 1.8), 0xdfe6ee]); // 白い柱
        it.push([at(new THREE.BoxGeometry(1.2, 0.5, 0.1), 0, 1.9), 0x2a3a4a]); // モニタ
    } else if (biome === 8) {
        // モール: エスカレーター + 手すり
        const ramp = new THREE.BoxGeometry(1.4, 0.4, 3.4);
        ramp.rotateX(-0.5);
        it.push([at(ramp, 0, 1.2), 0xb8bcc4]); // 斜めの段
        for (const dx of [-0.7, 0.7]) {
            const rail = new THREE.BoxGeometry(0.1, 0.1, 3.6);
            rail.rotateX(-0.5);
            it.push([at(rail, dx, 1.9), 0x2a2e34]);
        }
        it.push([at(new THREE.BoxGeometry(1.6, 0.6, 1.0), 0, 0.3, -1.6), 0x8a7a60]); // 下り口
    } else {
        it.push([at(new THREE.BoxGeometry(2, 1.2, 1.6), 0, 0.6), 0xb5743f]); // 予備のメサ
    }
    return mergeProps(it);
}

/** バイオームごとの走行レーン テクスチャ (makeTrailTexture と同じ 128px キャンバス)。 */
function makeBiomeLaneTexture(i: number): THREE.CanvasTexture {
    if (i === 0) return makeTrailTexture(); // 森=現行の土
    const c = document.createElement("canvas");
    c.width = 128;
    c.height = 128;
    const x = c.getContext("2d")!;
    if (i === 1) {
        // 砂
        x.fillStyle = "#d9b878";
        x.fillRect(0, 0, 128, 128);
        for (let k = 0; k < 16; k++) {
            x.strokeStyle = k % 2 ? "rgba(201,160,96,0.5)" : "rgba(238,216,168,0.5)";
            x.lineWidth = 2;
            x.beginPath();
            const y = k * 8 + Math.random() * 4;
            x.moveTo(0, y);
            x.bezierCurveTo(40, y - 4, 88, y + 4, 128, y);
            x.stroke();
        }
        for (let k = 0; k < 24; k++) {
            x.fillStyle = "#9c8050";
            const s = 1.5 + Math.random() * 2;
            x.fillRect(Math.random() * 128, Math.random() * 128, s, s);
        }
    } else if (i === 2) {
        // 雪
        x.fillStyle = "#e4ecf5";
        x.fillRect(0, 0, 128, 128);
        for (let k = 0; k < 50; k++) {
            x.fillStyle = Math.random() < 0.5 ? "rgba(205,217,230,0.5)" : "rgba(255,255,255,0.6)";
            x.beginPath();
            x.arc(Math.random() * 128, Math.random() * 128, 4 + Math.random() * 10, 0, 6.28);
            x.fill();
        }
        for (let k = 0; k < 14; k++) {
            x.fillStyle = "#b8c4d2";
            x.beginPath();
            x.arc(Math.random() * 128, Math.random() * 128, 2 + Math.random() * 3, 0, 6.28);
            x.fill();
        }
    } else if (i === 3) {
        // 桟橋 (横板)
        x.fillStyle = "#b88a4e";
        x.fillRect(0, 0, 128, 128);
        for (let k = 0; k < 8; k++) {
            const y0 = k * 16;
            x.fillStyle = k % 2 ? "#b88a4e" : "#9c6f38";
            x.fillRect(0, y0, 128, 14);
            x.fillStyle = "#5a3f1e"; // 隙間
            x.fillRect(0, y0 + 14, 128, 2);
            for (let n = 0; n < 4; n++) {
                x.fillStyle = "#3a2812"; // 釘
                x.fillRect(12 + n * 32, y0 + 6, 3, 3);
            }
        }
    } else if (i === 4) {
        // 火山: 黒い玄武岩 + 赤熱の裂け目
        x.fillStyle = "#2a1c18";
        x.fillRect(0, 0, 128, 128);
        for (let k = 0; k < 30; k++) {
            x.fillStyle = Math.random() < 0.5 ? "#1a100d" : "#3a2620";
            const s = 6 + Math.random() * 16;
            x.fillRect(Math.random() * 128, Math.random() * 128, s, s);
        }
        for (let k = 0; k < 5; k++) {
            // 溶岩の裂け目 (発光)
            const grad = x.createLinearGradient(0, 0, 128, 0);
            grad.addColorStop(0, "rgba(255,90,30,0)");
            grad.addColorStop(0.5, "rgba(255,140,40,0.9)");
            grad.addColorStop(1, "rgba(255,90,30,0)");
            x.strokeStyle = grad;
            x.lineWidth = 2 + Math.random() * 2;
            x.beginPath();
            const y = Math.random() * 128;
            x.moveTo(0, y);
            x.bezierCurveTo(40, y + (Math.random() - 0.5) * 30, 88, y + (Math.random() - 0.5) * 30, 128, y);
            x.stroke();
        }
    } else if (i === 5) {
        // 墓地: 暗い土の小道 + 落ち葉
        x.fillStyle = "#3a3344";
        x.fillRect(0, 0, 128, 128);
        for (let k = 0; k < 40; k++) {
            x.fillStyle = Math.random() < 0.5 ? "rgba(40,36,52,0.6)" : "rgba(60,56,74,0.5)";
            x.beginPath();
            x.arc(Math.random() * 128, Math.random() * 128, 3 + Math.random() * 8, 0, 6.28);
            x.fill();
        }
        for (let k = 0; k < 12; k++) {
            x.fillStyle = "#5a4a3a"; // 枯れ葉
            x.fillRect(Math.random() * 128, Math.random() * 128, 3, 2);
        }
    } else if (i === 6) {
        // 空: 虹色の雲の橋
        x.fillStyle = "#eef6ff";
        x.fillRect(0, 0, 128, 128);
        const cols = ["#ffd0d8", "#ffe6c0", "#fff6c0", "#d6ffd0", "#d0ecff", "#e0d6ff"];
        for (let k = 0; k < 6; k++) {
            x.fillStyle = cols[k];
            x.globalAlpha = 0.5;
            x.fillRect(0, k * 21, 128, 21);
        }
        x.globalAlpha = 1;
        for (let k = 0; k < 20; k++) {
            x.fillStyle = "rgba(255,255,255,0.7)";
            x.beginPath();
            x.arc(Math.random() * 128, Math.random() * 128, 4 + Math.random() * 7, 0, 6.28);
            x.fill();
        }
    } else if (i === 7) {
        // 空港: グレーの大判タイル + 目地
        x.fillStyle = "#9aa6b2";
        x.fillRect(0, 0, 128, 128);
        x.strokeStyle = "#7a8692"; x.lineWidth = 2;
        for (let k = 0; k <= 128; k += 32) { x.beginPath(); x.moveTo(0, k); x.lineTo(128, k); x.stroke(); x.beginPath(); x.moveTo(k, 0); x.lineTo(k, 128); x.stroke(); }
        for (let k = 0; k < 60; k++) { x.fillStyle = "rgba(255,255,255,0.12)"; x.fillRect(Math.random() * 128, Math.random() * 128, 2, 2); } // テカリ
    } else {
        // モール (深夜): 暗い市松タイル + ネオンの薄い反射
        for (let r = 0; r < 4; r++) for (let cc = 0; cc < 4; cc++) {
            x.fillStyle = (r + cc) % 2 ? "#322a32" : "#262028";
            x.fillRect(cc * 32, r * 32, 32, 32);
        }
        x.strokeStyle = "rgba(10,8,12,0.6)"; x.lineWidth = 1;
        for (let k = 0; k <= 128; k += 32) { x.beginPath(); x.moveTo(0, k); x.lineTo(128, k); x.stroke(); x.beginPath(); x.moveTo(k, 0); x.lineTo(k, 128); x.stroke(); }
        for (let k = 0; k < 16; k++) { x.fillStyle = Math.random() < 0.5 ? "rgba(255,106,208,0.12)" : "rgba(120,210,255,0.1)"; x.fillRect(Math.random() * 128, Math.random() * 128, 4, 4); } // ネオンの床反射
    }
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    return tex;
}

/** バイオームごとのフェンス テクスチャ (makeFenceTexture と同じ透過 128px キャンバス)。 */
function makeBiomeFenceTexture(i: number): THREE.CanvasTexture {
    if (i === 0) return makeFenceTexture(); // 森=現行の木
    const c = document.createElement("canvas");
    c.width = 128;
    c.height = 128;
    const x = c.getContext("2d")!;
    x.clearRect(0, 0, 128, 128);
    const palette = i === 1
        ? { rail: "#d8b87a", shade: "#b8965a", post: "#c4a468", postDark: "#a07c44" } // 砂漠=漂白柱
        : i === 2
          ? { rail: "#c8924e", shade: "#a3702f", post: "#b07d3f", postDark: "#8a5a2b" } // 雪=木 (上に雪)
          : i === 4
            ? { rail: "#3a2620", shade: "#1a100d", post: "#4a3028", postDark: "#241612" } // 火山=黒い柵
            : i === 5
              ? { rail: "#5a606a", shade: "#3c4047", post: "#6a7078", postDark: "#3a3e44" } // 墓地=鉄柵
              : i === 6
                ? { rail: "#cfe0f5", shade: "#a0c0e8", post: "#dfeeff", postDark: "#b0cce8" } // 空=結晶柵
                : i === 7
                  ? { rail: "#c6ced8", shade: "#9aa6b0", post: "#aab4be", postDark: "#7a8692" } // 空港=金属手すり
                  : i === 8
                    ? { rail: "#d0bfa4", shade: "#a89878", post: "#c0ac8c", postDark: "#90805f" } // モール=木目バリア
                    : { rail: "#8a9aa6", shade: "#6a7a86", post: "#7a8a96", postDark: "#5a6a76" }; // 海=灰青のドック
    for (const [y0, h] of [[34, 20], [78, 20]] as [number, number][]) {
        x.fillStyle = palette.rail;
        x.fillRect(0, y0, 128, h);
        x.fillStyle = palette.shade;
        x.fillRect(0, y0 + h - 5, 128, 5);
        if (i === 2) {
            x.fillStyle = "#ffffff"; // 氷=レール上端に雪
            x.fillRect(0, y0, 128, 4);
        }
        if (i === 4) {
            x.fillStyle = "rgba(255,110,40,0.5)"; // 溶岩=レール下端に赤熱
            x.fillRect(0, y0 + h - 2, 128, 2);
        }
    }
    if (i === 5) {
        // 鉄柵: 縦の細い棒を等間隔に
        x.fillStyle = palette.rail;
        for (let n = 0; n < 8; n++) x.fillRect(8 + n * 16, 24, 3, 80);
    }
    if (i === 3) {
        // ドック: ロープの垂れ
        x.strokeStyle = "#c9b48a";
        x.lineWidth = 3;
        x.beginPath();
        x.moveTo(0, 40);
        x.quadraticCurveTo(64, 64, 128, 40);
        x.stroke();
    }
    x.fillStyle = palette.post;
    x.fillRect(2, 6, 18, 116);
    x.fillStyle = palette.postDark;
    x.fillRect(16, 6, 4, 116);
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    return tex;
}

/** バイオームの遠景シルエット帯 (森=樹冠 / 砂漠=砂丘 / 雪=峰 / 海=水平線)。透過 canvas。 */
function makeBiomeBackdropTexture(i: number): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = 512;
    c.height = 128;
    const x = c.getContext("2d")!;
    x.clearRect(0, 0, 512, 128);
    if (i === 0) {
        // 樹冠
        x.fillStyle = "rgba(58,110,52,0.55)";
        for (let k = 0; k < 26; k++) {
            const cx = k * 20 + Math.random() * 8;
            const r = 14 + Math.random() * 14;
            x.beginPath();
            x.arc(cx, 110, r, Math.PI, 0);
            x.fill();
        }
    } else if (i === 1) {
        // 砂丘
        x.fillStyle = "rgba(196,154,90,0.5)";
        x.beginPath();
        x.moveTo(0, 128);
        for (let px = 0; px <= 512; px += 16) x.lineTo(px, 90 + Math.sin(px * 0.02) * 22 + Math.sin(px * 0.06) * 8);
        x.lineTo(512, 128);
        x.closePath();
        x.fill();
    } else if (i === 2) {
        // 峰
        x.fillStyle = "rgba(160,178,196,0.5)";
        x.beginPath();
        x.moveTo(0, 128);
        for (let px = 0; px <= 512; px += 64) x.lineTo(px, 50 + Math.random() * 40);
        x.lineTo(512, 128);
        x.closePath();
        x.fill();
        x.fillStyle = "rgba(255,255,255,0.5)"; // 雪の冠
        for (let px = 0; px <= 512; px += 64) {
            x.beginPath();
            x.moveTo(px - 14, 70);
            x.lineTo(px, 50);
            x.lineTo(px + 14, 70);
            x.fill();
        }
    } else if (i === 3) {
        // 海の水平線
        x.fillStyle = "rgba(64,148,180,0.4)";
        x.fillRect(0, 96, 512, 32);
        x.fillStyle = "rgba(120,200,220,0.35)";
        for (let k = 0; k < 40; k++) x.fillRect(Math.random() * 512, 96 + Math.random() * 28, 6 + Math.random() * 14, 2);
    } else if (i === 4) {
        // 火山: 連なる火山の稜線 + 火口の輝き
        x.fillStyle = "rgba(40,22,18,0.6)";
        x.beginPath();
        x.moveTo(0, 128);
        for (let px = 0; px <= 512; px += 64) {
            const peak = 46 + Math.random() * 30;
            x.lineTo(px - 24, 128 - peak * 0.4);
            x.lineTo(px, 128 - peak);
            x.lineTo(px + 24, 128 - peak * 0.4);
        }
        x.lineTo(512, 128);
        x.closePath();
        x.fill();
        x.fillStyle = "rgba(255,110,40,0.7)"; // 火口
        for (let px = 32; px <= 512; px += 64) x.fillRect(px - 6, 128 - (46 + Math.random() * 8), 12, 4);
    } else if (i === 5) {
        // 墓地: 墓石と十字のシルエット
        x.fillStyle = "rgba(30,28,38,0.7)";
        for (let k = 0; k < 22; k++) {
            const cx = k * 24 + Math.random() * 8;
            const h = 26 + Math.random() * 30;
            if (Math.random() < 0.3) {
                x.fillRect(cx - 2, 128 - h, 4, h); // 十字の縦
                x.fillRect(cx - 8, 128 - h * 0.8, 16, 4); // 十字の横
            } else {
                x.fillRect(cx - 7, 128 - h, 14, h); // 墓石
                x.beginPath();
                x.arc(cx, 128 - h, 7, Math.PI, 0); // 丸い頭
                x.fill();
            }
        }
    } else if (i === 6) {
        // 空: 遠くの雲の連なり
        x.fillStyle = "rgba(255,255,255,0.55)";
        for (let k = 0; k < 30; k++) {
            const cx = k * 18 + Math.random() * 8;
            const r = 12 + Math.random() * 18;
            x.beginPath();
            x.arc(cx, 96 + Math.random() * 16, r, Math.PI, 0);
            x.fill();
        }
    } else if (i === 7) {
        // 空港: 並ぶ大窓 (ターミナルのカーテンウォール) のシルエット
        x.fillStyle = "rgba(120,140,160,0.4)";
        x.fillRect(0, 70, 512, 58);
        x.fillStyle = "rgba(180,205,225,0.5)"; // ガラス
        for (let px = 6; px < 512; px += 26) x.fillRect(px, 76, 18, 44);
        x.fillStyle = "rgba(90,105,120,0.5)"; // 桟
        for (let px = 0; px < 512; px += 26) x.fillRect(px, 70, 4, 58);
    } else {
        // モール (深夜): 暗い店舗ファサード + 点いたままのネオン看板だけが光る
        x.fillStyle = "rgba(30,24,32,0.7)";
        x.fillRect(0, 60, 512, 68);
        const neon = ["rgba(255,106,208,0.95)", "rgba(120,210,255,0.9)", "rgba(255,220,120,0.9)"];
        for (let px = 8; px < 512; px += 48) {
            x.fillStyle = "rgba(20,16,24,0.7)"; x.fillRect(px, 78, 40, 50); // 暗い間口
            x.fillStyle = neon[(px / 48 | 0) % 3]; x.fillRect(px + 4, 70, 32, 8); // 光る看板
            x.fillStyle = "rgba(255,255,255,0.15)"; x.fillRect(px + 6, 90, 28, 30); // 薄いショーウィンドウ
        }
    }
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    return tex;
}

/** 病院の床: 汚れたリノリウムの市松タイル + 目地 + 擦れ + わずかな血染み。Backrooms イベント専用。 */
function makeHospitalFloorTexture(): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = 128;
    c.height = 128;
    const x = c.getContext("2d")!;
    const TS = 32;
    for (let gy = 0; gy < 128; gy += TS) for (let gx = 0; gx < 128; gx += TS) {
        x.fillStyle = ((gx + gy) / TS) % 2 ? "#c6c9bf" : "#d2d5cb"; // 淡い臨床的な白緑の市松
        x.fillRect(gx, gy, TS, TS);
    }
    x.strokeStyle = "#9a9d92"; x.lineWidth = 2; // 目地
    for (let k = 0; k <= 128; k += TS) {
        x.beginPath(); x.moveTo(0, k); x.lineTo(128, k); x.stroke();
        x.beginPath(); x.moveTo(k, 0); x.lineTo(k, 128); x.stroke();
    }
    for (let k = 0; k < 30; k++) { // 擦れ/汚れ
        x.fillStyle = "rgba(80,80,70,0.14)";
        x.beginPath(); x.arc(Math.random() * 128, Math.random() * 128, 3 + Math.random() * 9, 0, 6.28); x.fill();
    }
    for (let k = 0; k < 4; k++) { // 古い血染み (疎)
        x.fillStyle = "rgba(70,18,16,0.4)";
        x.beginPath(); x.arc(Math.random() * 128, Math.random() * 128, 4 + Math.random() * 8, 0, 6.28); x.fill();
    }
    const tex = new THREE.CanvasTexture(c);
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    return tex;
}

/** テキスト/アイコンを描いた透過 canvas → Sprite/Plane 用テクスチャ。 */
function makeLabelTexture(
    text: string,
    opts: { fill?: string; bg?: string; size?: number; glow?: string; outline?: string } = {},
): THREE.CanvasTexture {
    const size = opts.size ?? 96;
    const c = document.createElement("canvas");
    c.width = 256;
    c.height = 128;
    const x = c.getContext("2d")!;
    if (opts.bg) {
        x.fillStyle = opts.bg;
        x.fillRect(0, 0, 256, 128);
    }
    x.font = `700 ${size}px system-ui, sans-serif`;
    x.textAlign = "center";
    x.textBaseline = "middle";
    // glow halo pass (only when requested) — two stamps for a stronger bloom
    if (opts.glow) {
        x.save();
        x.shadowColor = opts.glow;
        x.shadowBlur = size * 0.5;
        x.fillStyle = opts.glow;
        x.fillText(text, 128, 64);
        x.fillText(text, 128, 64);
        x.restore();
    }
    x.lineWidth = size * (opts.glow ? 0.16 : 0.14);
    x.strokeStyle = opts.outline ?? "#06101c";
    x.strokeText(text, 128, 64);
    if (opts.glow) {
        const g = x.createLinearGradient(0, 18, 0, 110);
        g.addColorStop(0, opts.fill ?? "#ffffff");
        g.addColorStop(1, "#bfe9ff");
        x.fillStyle = g;
    } else {
        x.fillStyle = opts.fill ?? "#ffffff";
    }
    x.fillText(text, 128, 64);
    const tex = new THREE.CanvasTexture(c);
    tex.anisotropy = 4;
    return tex;
}

// ---------- パーティクル用テクスチャ (加算合成前提の白/グラデ。SpriteMaterial で色付け) ----------
function makeGlowTexture(): THREE.CanvasTexture {
    // 放射状の白→透明 (コア・スパーク・閃光)
    const c = document.createElement("canvas");
    c.width = c.height = 64;
    const x = c.getContext("2d")!;
    const g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    g.addColorStop(0, "rgba(255,255,255,1)");
    g.addColorStop(0.4, "rgba(255,255,255,0.55)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    x.fillStyle = g;
    x.fillRect(0, 0, 64, 64);
    return new THREE.CanvasTexture(c);
}
function makeRingTexture(): THREE.CanvasTexture {
    // 透明→白→透明の輪 (衝撃波)
    const c = document.createElement("canvas");
    c.width = c.height = 64;
    const x = c.getContext("2d")!;
    const g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    g.addColorStop(0, "rgba(255,255,255,0)");
    g.addColorStop(0.62, "rgba(255,255,255,0)");
    g.addColorStop(0.78, "rgba(255,255,255,1)");
    g.addColorStop(0.9, "rgba(255,255,255,0.5)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    x.fillStyle = g;
    x.fillRect(0, 0, 64, 64);
    return new THREE.CanvasTexture(c);
}
/** 縦の加算グラデ (上＝明・下＝暗) を縦に2回繰り返した「流れるエネルギー帯」テクスチャ。 */
function makeFieldTexture(): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = 8;
    c.height = 128;
    const x = c.getContext("2d")!;
    const g = x.createLinearGradient(0, 0, 0, 64); // one period in top half
    g.addColorStop(0.0, "rgba(255,255,255,0.05)");
    g.addColorStop(0.45, "rgba(255,255,255,0.9)");
    g.addColorStop(0.55, "rgba(255,255,255,1.0)");
    g.addColorStop(1.0, "rgba(255,255,255,0.05)");
    x.fillStyle = g;
    x.fillRect(0, 0, 8, 64);
    x.fillStyle = g;
    x.fillRect(0, 64, 8, 64); // repeat so scroll is seamless
    const t = new THREE.CanvasTexture(c);
    t.anisotropy = 2;
    return t;
}
/** 柔らかい白の上向きシェブロン (加算合成・SpriteMaterial で色付け)。 */
function makeChevronTexture(): THREE.CanvasTexture {
    const c = document.createElement("canvas");
    c.width = c.height = 64;
    const x = c.getContext("2d")!;
    x.strokeStyle = "rgba(255,255,255,1)";
    x.lineWidth = 9;
    x.lineCap = "round";
    x.lineJoin = "round";
    x.shadowColor = "rgba(255,255,255,0.9)";
    x.shadowBlur = 8;
    x.beginPath();
    x.moveTo(14, 42);
    x.lineTo(32, 22);
    x.lineTo(50, 42);
    x.stroke(); // ∧
    return new THREE.CanvasTexture(c);
}
function makeSoftTexture(): THREE.CanvasTexture {
    // 広いふんわり放射 (煙・泡・塵)
    const c = document.createElement("canvas");
    c.width = c.height = 64;
    const x = c.getContext("2d")!;
    const g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    g.addColorStop(0, "rgba(255,255,255,0.85)");
    g.addColorStop(0.5, "rgba(255,255,255,0.4)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    x.fillStyle = g;
    x.fillRect(0, 0, 64, 64);
    return new THREE.CanvasTexture(c);
}
function makeStreakTexture(): THREE.CanvasTexture {
    // 縦のふんわり棒 (羽根・泡の縁・光の柱)
    const c = document.createElement("canvas");
    c.width = 16;
    c.height = 64;
    const x = c.getContext("2d")!;
    const g = x.createLinearGradient(0, 0, 0, 64);
    g.addColorStop(0, "rgba(255,255,255,0)");
    g.addColorStop(0.5, "rgba(255,255,255,1)");
    g.addColorStop(1, "rgba(255,255,255,0)");
    x.fillStyle = g;
    x.fillRect(4, 0, 8, 64);
    return new THREE.CanvasTexture(c);
}

// ---------- ゲート op 生成 ----------
function randGoodOp(diff: number, stage: number): GateOp {
    // 倍率(×2)は序盤ほど稀でステージ/難度で増える。序盤は小さめの加算が主体。
    // ×3 は終盤(S5+)のみ。これで序盤に増えすぎず、終盤は一気に膨らむ。
    const pMul = Math.min(0.45, 0.05 + (stage - 1) * 0.09 + diff * 0.12);
    if (Math.random() < pMul) return { kind: "*", value: stage >= 5 && Math.random() < 0.3 ? 3 : 2 };
    return { kind: "+", value: 3 + Math.floor(diff * 12) };
}
function randBadOp(diff: number, stage: number): GateOp {
    // 損は強め: 割り算と大きめの減算が中心
    const r = Math.random();
    if (r < 0.5) return { kind: "/", value: 2 + (stage >= 4 && diff > 0.5 ? 1 : 0) };
    if (r < 0.9) return { kind: "-", value: 8 + Math.floor(diff * 40) };
    return { kind: "+", value: 2 };
}
function makeGatePair(diff: number, stage: number): { left: GateOp; right: GateOp } {
    // 両得は少なめ、両損・混在を多めにして安易に増えないように
    const mode = Math.random();
    if (mode < 0.18) return { left: randGoodOp(diff, stage), right: randGoodOp(diff, stage) };
    if (mode < 0.43) return { left: randBadOp(diff, stage), right: randBadOp(diff, stage) };
    const goodLeft = Math.random() < 0.5;
    return {
        left: goodLeft ? randGoodOp(diff, stage) : randBadOp(diff, stage),
        right: goodLeft ? randBadOp(diff, stage) : randGoodOp(diff, stage),
    };
}

// ---------- エンティティ ----------
interface EntBase {
    obj: THREE.Object3D;
    z: number;
    wx: number;
    resolved: boolean;
    elite?: boolean; // エリート敵: 光る・高 HP・高コイン (撃破でコイン×3)
    coin?: number; // 撃破時のコイン (未設定なら type ごとの既定)
}
interface GateEnt extends EntBase {
    type: "gate";
    leftOp: GateOp;
    rightOp: GateOp;
}
interface MobEnt extends EntBase {
    type: "mob";
    hp: number;
    max: number;
    bar: THREE.Sprite;
    halfW: number; // 当たり幅 (HP では変えない・固定)
    weak: boolean; // true=一撃で死ぬ序盤の数押し雑魚
}
interface ArcherEnt extends EntBase {
    type: "archer";
    hp: number;
    max: number;
    bar: THREE.Sprite;
    shootTimer: number;
    bomber: boolean; // true なら着弾点に広範囲爆風を落とす砲撃手
    spread?: boolean; // true なら 3 方向に扇状弾を撃つ
}
interface HazardEnt extends EntBase {
    type: "hazard";
    halfW: number; // 当たり幅 (スパイク=細い / ドラゴンブレス=太い)
    fire: boolean; // true ならドラゴンブレス (炎の壁)
    sweepAmp: number; // >0 なら横に往復する回転ノコギリ
    sweepPhase: number; // 往復の位相
    spin?: THREE.Object3D; // 回転させる刃
    // --- NEW (all optional; absent = legacy behaviour) ---
    sweepFreq?: number; // override the hardcoded 1.8 sweep rate (G1 uses 1.4, G3 uses 1.1)
    swingHead?: THREE.Object3D; // G1: pivot child to rock in sync with e.wx (NOT free-spin)
    swingAmpZ?: number; // G1: max rock angle (rad)
    crusher?: { left: THREE.Object3D; right: THREE.Object3D; gap0: number; gap1: number }; // G2
    gapWall?: { gapHalf: number; segL: THREE.Object3D; segR: THREE.Object3D }; // G3
    pull?: number; // G7 strength (steer-bias toward e.wx)
    drift?: number; // G8 accel (lateral momentum injection)
    slam?: THREE.Object3D; // 落下プレス/隕石の塊 (z 進行で y を下げ、着弾で AoE 爆発)
    eye?: number; // 竜巻の致死中心半径 (pull と併用・中心に入ると被害)
    sky?: { fuse: number; block: THREE.Object3D }; // 頭上隕石: 真上から降る (前面遮蔽を貫通・横へ逃げる)
    // --- G9 電流フェンス: 片側から伸縮する電撃帯。伸びている側に居ると被弾、縮む瞬間に通る ---
    arc?: { side: number; minHalf: number; maxHalf: number; freq: number; phase: number; bars: THREE.Object3D };
    // --- G10 崩落床 (落とし穴): 前方区画が抜ける。撃って足場を固める or 横へずれて回避 ---
    pit?: { open: boolean; lid: THREE.Object3D; openAt: number; armorHp: number; armorMax: number };
    // --- A 巨大転がり球: 中央帯を占める大球。両サイドへ流れて避ける (転がりスピン用の子ノード) ---
    boulder?: THREE.Object3D;
    wobblePhase?: number; // A: e.wx の予告横揺れ位相
    // --- B ワープゲート: 通過でラン進捗を前後にずらす (キルなし)。色で前進(緑)/逆流(赤)を予告 ---
    warp?: { dir: 1 | -1; amount: number; done?: boolean };
    gate?: THREE.Object3D; // B: スピンさせるリング
    // --- C/D ゾーン帯: 前線到達で操作反転 / 重力反転の一時モディファイアを起動する床帯 ---
    zone?: "invert" | "grav";
    // --- Level 0 ターン壁: 行き止まり (side 側だけ開いた通路)。開口へ寄れば無傷、塞がれた幅に重なった列が削れる ---
    turnWall?: { side: -1 | 1; swung: boolean };
}
// 突進する大型敵 (charger=俊足 / giant=ギガントロボ / dragon=ホバリング突進 / bacteria=うごめく肉塊)
interface BruiserEnt extends EntBase {
    type: "bruiser";
    variant: "charger" | "giant" | "dragon" | "bacteria" | "stalker" | "partygoer" | "flyer" | "shieldbot" | "orbiter";
    hp: number;
    max: number;
    bar: THREE.Sprite;
    halfW: number;
    speedMul: number; // 接近速度の倍率
    telegraph?: number; // dragon: >0 の間はホバリングして前進せず (突進前の溜め)
    dragonPhase?: "approach" | "hold" | "charge"; // dragon: 接近→溜め→高速突進
    wings?: THREE.Object3D[]; // dragon: 羽ばたかせる翼
    glow?: THREE.Sprite; // dragon: 溜め中に光るオーラ
    lift?: THREE.Object3D; // dragon: ホバリングの上下を乗せる子ノード (placeEntity の y リセットを回避)
    // --- flyer (飛来する強襲兵): クルー上空を弧で越え、背後に回り込んで一閃する ---
    flyPhase?: "approach" | "over" | "behind" | "strike"; // 飛行フェーズ
    flyLift?: THREE.Object3D; // 高度 (上下の弧) を乗せる子ノード (placeEntity の y リセットを回避)
    flyShadow?: THREE.Mesh; // flyer: 真下の床に落とす影 (高度に応じて拡縮/濃淡 → 「飛んでいる」を読ませる)
    // shieldbot (道中の敵ロボ): 前面シールド + ミサイル + 接近でジェット退避。
    shieldHp?: number; shieldMax?: number; shieldMesh?: THREE.Mesh; shieldBroken?: boolean;
    botFireT?: number; // 次のミサイル発射までの残り秒
    botEscaping?: boolean; // 接近されてジェットで上空退避中 (貫通しない)
    // orbiter (周回する浮遊敵): 群衆の周りを旋回し任意方向(背後含む)から撃ってくる終盤の敵。
    orbiting?: boolean; orbitA?: number; orbitR?: number; orbitT?: number; orbFireT?: number;
    flyStrikeT?: number; // strike フェーズの溜め (テレグラフ) 残り時間
    flyDone?: boolean; // 突撃済み → 後方へ飛び去って撤去待ち
    writhe?: THREE.Object3D[]; // bacteria: うごめかせる肉塊/触手 (フォールバック手続き版のみ)
    mixer?: THREE.AnimationMixer; // bacteria: FBX 内蔵アニメ再生用ミキサー
    pounceCd?: number; // bacteria: 次のとびかかりまでのクールダウン
    pounceT?: number; // bacteria: とびかかり中の残り時間 (>0 で溜め→跳躍中)
    pounceBaseScale?: number; // bacteria: 跳躍 squash の基準スケール (実モデルは ~0.004 と極小なので相対で掛ける)
    pounceFromX?: number; pounceFromZ?: number; // bacteria: 跳躍開始位置 (放物線の補間元)
    pounceToX?: number; pounceToZ?: number; // bacteria: 着地点 (跳躍開始時にロック・補間先)
    pounceTell?: THREE.Mesh; // bacteria: 着地点の床予告リング (scene 直付け・着地/撤去で破棄)
    pounceExploded?: boolean; // bacteria: 着地爆散済みガード (二重発火防止)
    // --- partygoer (Backrooms Level Fun): 踊って近づき突然横へダート(掴みかかり)する群れ ---
    partyBaseScale?: number; // 正規化後の基準スケール (演出は必ずこの相対で掛ける・絶対1で巨大化バグ回避)
    partyLaneX?: number; // ダンスのスウェイ基準にする lane 上の中心 x
    danceSeed?: number; // 個体ごとの位相 (群れがバラバラに揺れるよう)
    dartCd?: number; // (legacy・未使用) かつてのダート用クールダウン。参照残置のため optional 放置。
    dartT?: number; // (legacy・未使用) かつてのダート残り時間。
    dartFromX?: number; // (legacy・未使用) かつてのダート開始 wx。
    // --- 勧誘リワーク (Level Fun): 追わずに前線で踊り、クルーを 1 人ずつ連れ去ってコンガの列に加える ---
    congaHolder?: THREE.Object3D; // 連れ去ったクルー figure を載せる子ノード (本体の踊り回転を打ち消して使う)
    conga?: THREE.Object3D[]; // 連れ去ったクルーの視覚 figure (背後にコンガの列で並ぶ)
    congaTrail?: { x: number; z: number }[]; // 本体のワールド位置履歴 (figure を遅延追従させる軌跡)
    recruited?: number; // 連れ去った人数 (撃破時にこの数だけ crew を取り返す)
    recruitCd?: number; // 次に 1 人連れ去るまでのクールダウン
    recruitDone?: boolean; // 上限まで連れ去った → 列を連れて退場フェーズへ
    // --- D 多腕の徘徊体 (脱二択中ボス): 低速高 HP で迫りつつ、薙ぎ払い(横)/叩きつけ(縦 AoE)/雑魚散布を時間差で重ねる ---
    stalk?: {
        hold: number; // 接近を止める基準 z (ここまで来たら居座って攻撃を回す)
        phase: 0 | 1 | 2; // HP 閾値で上がる: 攻撃頻度が増す
        atkT: number; // 次の攻撃までのクールダウン
        seq: number; // 攻撃シーケンスの index (左薙ぎ→叩き→右薙ぎ→雑魚… と非対称に回す)
        sweepT: number; // 薙ぎ払いの予告残り時間 (>0 で予告中・0 で発動)
        sweepDir: number; // 薙ぎの向き (-1=左から / +1=右から)
        sweepTell: THREE.Object3D; // 薙ぎ予告の床ライン
        slamT: number; // 叩きつけの予告残り時間
        slamX: number; // 叩きつけの着弾 wx
        slamTell: THREE.Object3D; // 叩きつけ予告のリング
        arms: THREE.Object3D[]; // 振り回す腕 (純視覚)
    };
}
// 奥から直進してくるロケット弾 (避けるか撃ち落とす・着弾で爆発)
interface RocketEnt extends EntBase {
    type: "rocket";
    hp: number;
    max: number;
    bar: THREE.Sprite;
    halfW: number;
    speedMul: number;
}
// アイテム台: 耐久値を持つ破壊対象。撃って壊せば入手、壊し損ねて触れたら直撃で消滅。
type StandTier = "crate" | "steel" | "vault" | "barrel";
interface ItemEnt extends EntBase {
    type: "item";
    item: ItemKind;
    stand: StandTier;
    hp: number;
    max: number;
    bar: THREE.Sprite;
    halfW: number; // 接触当たり幅
    spin: THREE.Object3D; // 回転表示するモデル
    opened: boolean; // 破壊して取得済みか
    bonus?: number; // NEW: crew/firepower payload for "bonus"/"heal" kinds
}
type Entity = GateEnt | MobEnt | ArcherEnt | HazardEnt | BruiserEnt | RocketEnt | ItemEnt;
// HP バーを持つ全エンティティ (撃てる対象)
type HpEnt = MobEnt | ArcherEnt | BruiserEnt | RocketEnt | ItemEnt;

type Phase = "running" | "battle" | "clear" | "result" | "shop";

// 個体として保持するクルー (rx/rz=隊列内の現在相対位置・lerp で詰め直す)。
// spd/amp/swy/lean/ph/land は生成時に一度だけ焼く有機的な個体差 (これが棒立ちを壊す)。
type Member = {
    rx: number; ry: number; rz: number; ph: number;
    spd: number; amp: number; swy: number; lean: number; land: number;
    sc: number; // 個体ごとの身長スケール (lockstep のクローン感を壊す)
    phaseOffset: number; // 走りクリップの個体位相オフセット (0..1)・ロックステップ解消
};

// プールされたパーティクル 1 個分の状態 (全 VFX がこの 1 種に流れる)。
type Particle = {
    sp: THREE.Sprite; alive: boolean; life: number; max: number;
    vel: THREE.Vector3; grav: number; drag: number;
    size0: number; size1: number; spin: number;
    col0: THREE.Color; col1: THREE.Color; add: boolean; hump: boolean;
};

// ボスの種類。攻撃演出とモデルが変わる (HP・総ドレイン量は共通でクリア可能性を保つ)。
type BossKind = "king" | "dragon" | "angel" | "leviathan" | "chronos" | "mech" | "smiler";
const BOSS_ROTATION: BossKind[] = ["king", "dragon", "mech", "chronos", "angel", "smiler", "leviathan"];
const BOSS_NAME: Record<BossKind, string> = { king: "KING", dragon: "🐉 DRAGON", angel: "👼 SERAPH", leviathan: "🌊 LEVIATHAN", chronos: "⏳ CHRONOS", mech: "🤖 OVERSEER", smiler: "😀 THE SMILER" };
// アニメ用に保持するボスの可動パーツ (idle/attack で揺らす子参照)。
type BossParts = {
    body?: THREE.Object3D; head?: THREE.Object3D; tail?: THREE.Object3D;
    torso?: THREE.Object3D; // 王: 腰 pivot (上半身まとめて捻り/前傾させる full-body 用)
    cape?: THREE.Object3D; halo?: THREE.Object3D; fins?: THREE.Object3D[];
    armL?: THREE.Object3D; armR?: THREE.Object3D; // 肩 pivot (王=笏腕の関節リグ / 天使の祝福腕)
    elbowL?: THREE.Object3D; elbowR?: THREE.Object3D; // 肘 pivot (FK 2 関節目・腕のしなり)
    handL?: THREE.Object3D; handR?: THREE.Object3D; // 手 pivot (王は右手に笏を握る)
    legL?: THREE.Object3D; legR?: THREE.Object3D; // mech: 蹴りに使う脚 pivot
    jet?: THREE.Object3D; // mech: ジェットパックの炎を出す尻ノード / smiler: 顔の発光参照
    claws?: THREE.Object3D[]; // 竜の前爪 / 海獣の爪 (連続 sine 揺れ)
};
// ボスモデル組み立ての戻り値。tell=攻撃時に光る予兆パーツ、jaw=開閉する顎 (無ければ null)。
type BossBuild = { obj: THREE.Group; bar: THREE.Sprite; spin: THREE.Object3D[]; tell: THREE.Object3D | null; jaw: THREE.Object3D | null; parts: BossParts };

export interface CrewRunHandle {
    destroy(): void;
}

/** Crew Run 3D を host 内に起動。onExit は「もどる」/閉じるで呼ぶ。 */
export async function launchCrewRun(host: HTMLElement, onExit: () => void): Promise<CrewRunHandle> {
    const game = new CrewRun3D(host, onExit);
    game.start();
    // 開発時のみ: バランス検証 (F2 デバッグ表示 / 自動テスト) から内部状態へ触れるフック。
    if (import.meta.env.DEV) (globalThis as Record<string, unknown>).__crewRun = game;
    // HMR でこのモジュールが差し替わるとき、古いゲームの AudioContext/アンビエントが
    // dispose されず鳴り続ける (開発中に "Level! アンビエントがずっと流れる" 原因)。明示的に破棄する。
    import.meta.hot?.dispose(() => game.dispose());
    return { destroy: () => game.dispose() };
}

/**
 * 合成 WebAudio による効果音 + ループ BGM (アセット不要)。AudioContext はユーザー操作 (起動クリック)
 * 後に resume する。全イベントは play(name) で短い合成音を鳴らし、BGM はフェーズで mood を切替える。
 */
type AudioMood = "run" | "boss" | "level" | "shop";
class GameAudio {
    private ctx: AudioContext | null = null;
    private master!: GainNode;
    private sfxGain!: GainNode;
    private musicGain!: GainNode;
    private muted = false;
    private musicTimer: number | null = null;
    private step = 0;
    private mood: AudioMood = "run";
    private lastShoot = 0;

    private ensure(): boolean {
        if (this.ctx) { if (this.ctx.state === "suspended") void this.ctx.resume(); return true; }
        try {
            const AC = (window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext);
            this.ctx = new AC();
            this.master = this.ctx.createGain(); this.master.gain.value = this.muted ? 0 : 0.6; this.master.connect(this.ctx.destination);
            this.sfxGain = this.ctx.createGain(); this.sfxGain.gain.value = 0.9; this.sfxGain.connect(this.master);
            this.musicGain = this.ctx.createGain(); this.musicGain.gain.value = 0.3; this.musicGain.connect(this.master);
            return true;
        } catch { this.ctx = null; return false; }
    }

    resume(): void { this.ensure(); }
    setMuted(m: boolean): void { this.muted = m; if (this.master) this.master.gain.value = m ? 0 : 0.6; }
    toggleMute(): boolean { this.setMuted(!this.muted); return this.muted; }
    isMuted(): boolean { return this.muted; }

    private tone(freq: number, dur: number, type: OscillatorType, gain: number, slideTo?: number, dest?: AudioNode): void {
        if (!this.ctx) return;
        const t = this.ctx.currentTime;
        const o = this.ctx.createOscillator(); o.type = type; o.frequency.setValueAtTime(freq, t);
        if (slideTo) o.frequency.exponentialRampToValueAtTime(Math.max(1, slideTo), t + dur);
        const g = this.ctx.createGain();
        g.gain.setValueAtTime(0.0001, t);
        g.gain.exponentialRampToValueAtTime(gain, t + 0.008);
        g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        o.connect(g); g.connect(dest ?? this.sfxGain); o.start(t); o.stop(t + dur + 0.03);
    }

    private noise(dur: number, gain: number, filterFreq: number, slideTo?: number): void {
        if (!this.ctx) return;
        const t = this.ctx.currentTime;
        const n = Math.max(1, Math.floor(this.ctx.sampleRate * dur));
        const buf = this.ctx.createBuffer(1, n, this.ctx.sampleRate);
        const d = buf.getChannelData(0); for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
        const src = this.ctx.createBufferSource(); src.buffer = buf;
        const f = this.ctx.createBiquadFilter(); f.type = "lowpass"; f.frequency.setValueAtTime(filterFreq, t);
        if (slideTo) f.frequency.exponentialRampToValueAtTime(Math.max(60, slideTo), t + dur);
        const g = this.ctx.createGain(); g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        src.connect(f); f.connect(g); g.connect(this.sfxGain); src.start(t); src.stop(t + dur + 0.03);
    }

    /** 名前付き効果音を鳴らす (合成・短い)。 */
    play(name: string): void {
        if (!this.ensure() || !this.ctx) return;
        switch (name) {
            case "shoot": {
                const now = this.ctx.currentTime; if (now - this.lastShoot < 0.05) return; this.lastShoot = now;
                this.tone(420 + Math.random() * 60, 0.06, "square", 0.1, 180); this.noise(0.04, 0.05, 2000); break;
            }
            case "kill": this.tone(300, 0.08, "triangle", 0.12, 140); this.noise(0.07, 0.07, 1400, 400); break;
            case "boom": this.tone(120, 0.4, "sine", 0.24, 40); this.noise(0.4, 0.2, 1200, 120); break;
            case "coin": this.tone(880, 0.05, "square", 0.1); this.tone(1320, 0.12, "square", 0.1, undefined); break;
            case "pickup": [523, 659, 784, 1046].forEach((f, i) => this.tone(f, 0.1, "triangle", 0.12 - i * 0.005, undefined)); break;
            case "shield": this.tone(880, 0.18, "sine", 0.12, 1320); break;
            case "shieldbreak": this.noise(0.3, 0.18, 3200, 600); this.tone(700, 0.2, "sawtooth", 0.1, 200); break;
            case "overheat": this.tone(300, 0.5, "sawtooth", 0.16, 90); this.noise(0.5, 0.08, 800, 200); break;
            case "cool": this.tone(500, 0.12, "sine", 0.1, 900); break;
            case "turret": this.tone(700, 0.05, "square", 0.08, 380); break;
            case "boss": this.tone(80, 0.9, "sawtooth", 0.2, 60); this.tone(160, 0.9, "sine", 0.1, 120); break;
            case "phase": this.tone(440, 0.12, "square", 0.12); this.tone(659, 0.16, "square", 0.1); break;
            case "level": this.tone(220, 0.7, "sawtooth", 0.18, 200); break;
            case "pounce": this.tone(200, 0.22, "sawtooth", 0.1, 620); break;
            case "hit": this.tone(180, 0.1, "square", 0.1, 90); break;
            case "cannon": this.tone(60, 0.7, "sawtooth", 0.26, 220); this.tone(180, 0.6, "square", 0.14, 600); this.noise(0.6, 0.12, 2400, 300); break; // 波動砲: 低音うなり + 電子チャージ
            case "bossatk": this.tone(140, 0.3, "sawtooth", 0.16, 70); this.tone(90, 0.32, "square", 0.1, 50); break; // ボス攻撃: 不穏な低音
            case "break": this.noise(0.32, 0.2, 4500, 700); this.tone(360, 0.2, "square", 0.12, 110); break; // 壁/破壊: ガラガラ砕け
        }
    }

    // ----- Level! アンビエント (ユーザー作の mp3 をループ) -----
    private ambientEl: HTMLAudioElement | null = null;
    private ambientUrl = "";
    setAmbientUrl(url: string): void { this.ambientUrl = url; }
    private playAmbient(): void {
        if (!this.ensure() || !this.ctx || !this.ambientUrl) return;
        if (!this.ambientEl) {
            this.ambientEl = new Audio(this.ambientUrl);
            this.ambientEl.loop = true;
            try { this.ctx.createMediaElementSource(this.ambientEl).connect(this.musicGain); } catch { /* 経路化不可なら要素直再生 */ }
        }
        this.musicGain.gain.value = 0.7; // アンビエントはしっかり聞かせる
        try { this.ambientEl.currentTime = 0; } catch { /* ignore */ }
        void this.ambientEl.play().catch(() => {});
    }
    private stopAmbient(): void { if (this.ambientEl) this.ambientEl.pause(); }
    private ambientActive(): boolean { return !!this.ambientEl && !this.ambientEl.paused; }

    setMood(m: AudioMood): void {
        if (this.mood === m) return;
        this.mood = m; this.step = 0;
        // Level! は専用アンビエント (mp3) をループ。それ以外は合成 BGM へ戻す。
        if (m === "level") this.playAmbient();
        else this.stopAmbient();
    }
    startMusic(): void {
        if (!this.ensure() || this.musicTimer != null) return;
        this.musicTimer = window.setInterval(() => this.musicStep(), 250); // ~ 8 分音符
    }
    stopMusic(): void { if (this.musicTimer != null) { clearInterval(this.musicTimer); this.musicTimer = null; } }

    private musicStep(): void {
        if (!this.ctx || this.muted) return;
        if (this.mood === "level") {
            // Level! は基本アンビエント(mp3)。読込前/失敗時のフォールバックだけ、悲しく不気味な合成ドローン。
            if (this.ambientActive()) return; // mp3 が鳴っていれば合成は出さない
            const s = this.step++;
            this.musicGain.gain.value = 0.22;
            // 低い持続ドローン + たまに不協和な半音上の音 (生理的な不安)。
            const drone = [49, 52, 46, 49][((s / 4) | 0) % 4]; // とても低い
            this.tone(drone, 1.6, "sine", 0.6, undefined, this.musicGain);
            this.tone(drone * 1.06, 1.6, "sine", 0.25, undefined, this.musicGain); // わずかにうなる不協和
            if (s % 6 === 3) this.tone([330, 311, 277][(s / 6 | 0) % 3], 1.2, "triangle", 0.16, undefined, this.musicGain); // 遠くで悲しいベル
            return;
        }
        const moods: Record<AudioMood, { scale: number[]; bass: number[]; type: OscillatorType; mg: number }> = {
            run: { scale: [262, 294, 330, 392, 440], bass: [131, 131, 98, 110], type: "triangle", mg: 0.3 },
            boss: { scale: [196, 233, 262, 311, 349], bass: [98, 98, 87, 73], type: "sawtooth", mg: 0.28 },
            level: { scale: [], bass: [], type: "sine", mg: 0.2 },
            shop: { scale: [330, 392, 440, 523, 587], bass: [165, 165, 196, 220], type: "triangle", mg: 0.28 },
        };
        const m = moods[this.mood];
        this.musicGain.gain.value = m.mg;
        const s = this.step++;
        const note = m.scale[(s * 2) % m.scale.length] * (s % 8 < 4 ? 1 : 2);
        this.tone(note, 0.2, m.type, 0.4, undefined, this.musicGain);
        if (s % 2 === 0) this.tone(m.bass[((s / 2) | 0) % m.bass.length], 0.38, "triangle", 0.55, undefined, this.musicGain);
    }

    dispose(): void { this.stopMusic(); this.stopAmbient(); this.ambientEl = null; if (this.ctx) { void this.ctx.close().catch(() => {}); this.ctx = null; } }
}

class CrewRun3D {
    private readonly host: HTMLElement;
    private readonly onExit: () => void;

    private readonly renderer: THREE.WebGLRenderer;
    private readonly scene = new THREE.Scene();
    private readonly camera: THREE.PerspectiveCamera;
    private readonly clock = new THREE.Clock();

    // 共有ジオメトリ/マテリアル
    private gaitPhase = 0; // 連続歩行サイクル位相 (0..1) — スピード連動で進み、体の沈みとクリップ時刻を駆動
    private readonly enemyGeo = buildCrewGeometry(C_ENEMY);
    private readonly archerGeo = buildCrewGeometry(C_ARCHER);
    private readonly blobGeo = buildBlobGeometry();
    private readonly crewMat = new THREE.MeshLambertMaterial({ vertexColors: true });
    private readonly woodMat: THREE.MeshLambertMaterial;
    private readonly dummy = new THREE.Object3D();

    // --- 連続素体クルー (スキンメッシュ + InstancedMesh2 per-instance スキニング) ---
    // crewBodyGeo: 継ぎ目なし素体 (skinIndex/skinWeight 付与済み・全インスタンス共有)。
    // crewSkinMat: vertexColors を使わない単色 (青) スキニング用マテリアル。
    private crowdMesh!: InstancedMesh2;
    private crewBodyGeo!: THREE.BufferGeometry;
    // おもちゃプラスチック質感: 低 roughness + 環境マップ反射で艶を出す (Mob Control 風)。
    private readonly crewSkinMat = new THREE.MeshStandardMaterial({ color: C_ALLY.body, roughness: 0.32, metalness: 0.0, envMapIntensity: 0.85 });
    // 巨大ロボ (ガンダム風): 金属メカ。vertexColors + 高 metalness + 環境マップで反射。
    private readonly giantMat = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.34, metalness: 0.55, envMapIntensity: 1.0 });
    private crewSkeleton!: THREE.Skeleton;
    private crewBones!: THREE.Bone[];
    private crewRigRoot!: THREE.Object3D; // root bone の親 (matrixWorld=identity を提供)
    private crewMixerRoot!: THREE.Object3D; // mixer のターゲット (= crewRigRoot)
    private crewMixer!: THREE.AnimationMixer;
    private crewClipDur = 0.6;
    private crewVisibleCount = 0; // 現在 visible にしているインスタンス数 (差分トグル用)
    private crewArmed = false; // 武器所持中 (weaponMesh あり) → 腕を前方の構えポーズにする (第二弾)
    // 構え (両手で前方に銃を抱える) の腕ボーン回転。bakeCrewBuckets で走りの腕に上書きする。
    private static readonly AIM_UPPER_L = new THREE.Quaternion().setFromEuler(new THREE.Euler(-1.5, 0.3, 0));
    private static readonly AIM_FORE_L = new THREE.Quaternion().setFromEuler(new THREE.Euler(-0.2, 0.28, 0));
    private static readonly AIM_UPPER_R = new THREE.Quaternion().setFromEuler(new THREE.Euler(-1.5, -0.3, 0));
    private static readonly AIM_FORE_R = new THREE.Quaternion().setFromEuler(new THREE.Euler(-0.2, -0.28, 0));
    private static readonly PHASE_BUCKETS = 24; // 位相を量子化し、バケット単位でスケルトン評価 (300 体対策)
    // バケットごとに 1 回だけ mixer 評価し、各ボーンの matrixWorld をスナップショット。
    // 個体は属するバケットのスナップショットを復元して setBonesAt(false) するだけ (matrix 連鎖を再計算しない)。
    private crewBucketBones: THREE.Matrix4[][] = [];

    private weaponMesh: THREE.InstancedMesh | null = null;
    private robotMesh: THREE.InstancedMesh | null = null;
    private pierTex!: THREE.CanvasTexture;
    private fenceTex!: THREE.CanvasTexture;

    // --- バイオームのシーナリー (buildWorld のローカルから昇格・applyBiome で in-place 更新) ---
    private hemi!: THREE.HemisphereLight;
    private dir!: THREE.DirectionalLight;
    private grassMat!: THREE.MeshLambertMaterial; // 400x600 の地面プレーン
    private laneMat!: THREE.MeshLambertMaterial; // map = pierTex (旧 trailMat)
    private fenceMat!: THREE.MeshLambertMaterial; // map = fenceTex
    private fog!: THREE.Fog; // this.scene.fog への型付きハンドル
    // バイオームごとに焼いたスクロール テクスチャ (8 枚・セッション中常駐)
    private laneTexes: THREE.CanvasTexture[] = []; // [dirt, sand, snow, boardwalk]
    private fenceTexes: THREE.CanvasTexture[] = []; // [wood, bleached, ice, dock]
    // 遠景シルエット帯 (1 枚の quad に map を差し替え)
    private backdropMat: THREE.MeshBasicMaterial | null = null;
    private backdropTexes: THREE.CanvasTexture[] = []; // [treeline, dunes, peaks, horizon]
    // シーナリーのプロップ プール (3 InstancedMesh・バイオーム ジオメトリをポインタで差し替え)
    private propMat!: THREE.MeshLambertMaterial; // 1 枚共有・vertexColors (crewMat とは別)
    private propPools: {
        mesh: THREE.InstancedMesh;
        data: Float32Array; // stride 5: [x, z, scale, rotY, side] * count
        count: number;
        geoms: THREE.BufferGeometry[]; // [forest, desert, snow, sea]
    }[] = [];
    private biomeIdx = 0; // 0=forest 1=desert 2=snow 3=sea
    private get skin() {
        return GIMMICK_SKIN[this.biomeIdx];
    }

    private readonly entities: Entity[] = [];
    private readonly bullets: { mesh: THREE.Mesh; vel: THREE.Vector3; life: number; trailCol?: number; trailEl?: string; glow?: THREE.Sprite }[] = [];
    private readonly arrows: {
        mesh: THREE.Object3D; wx: number; z: number; resolved: boolean; halfW: number; blast: boolean; dmg?: number;
        bossSpeed?: boolean; // true => BOSS_ARROW_SPEED で移動 (一定テレグラフ)
        sweep?: { from: number; to: number }; // 飛行中に wx を補間 (動く壁/笏)
        hp?: number; // 撃ち落とし可能: firepower で削り、0 で破裂
        feint?: boolean; // 囮レーン: 着弾しても collideCrowd しない
        bossHit?: boolean; // collideCrowd でボス専用 armor 軽減を適用
        homing?: boolean; // 追尾弾: 飛行中に wx をプレイヤーへ寄せる (振り切れる程度)
        slow?: boolean; // 低速弾 (追尾オーブ用・避ける時間を作る)
        trailCol?: number; trailEl?: string; glow?: THREE.Sprite; // 装飾 (当たり判定に影響なし)
    }[] = [];
    private readonly fx: { obj: THREE.Object3D; life: number; max: number; grow: number }[] = [];
    private boss:
        | {
              obj: THREE.Object3D; hp: number; max: number; z: number; bar: THREE.Sprite;
              kind: BossKind; windup: number; spin: THREE.Object3D[];
              phase: 0 | 1 | 2; heavyTimer: number; heavyCarry: number; patternIdx: number;
              tell: THREE.Object3D | null; jaw: THREE.Object3D | null;
              strikeT: number; idlePh: number; parts: BossParts;
              // 海獣専用: 回避↔突進のサブステート (毎フレーム updateLeviathan が駆動)。
              levMode?: "evade" | "charge"; levT?: number; levPhaseT?: number;
              levStrafe?: number; levChargeZ?: number; levTilt?: number;
              // mech 専用: 浮遊高度 hover・左右ダッシュ strafe・近接攻撃 (拳叩き/蹴り) の演出タイマー。
              mechX?: number; mechTargetX?: number; mechDashT?: number; mechHover?: number;
              mechMelee?: "none" | "fist" | "kick"; mechMeleeT?: number; mechMeleeX?: number;
              // smiler 専用: 点灯↔消灯と突進。lightT=消灯までの残り、lunge=突進の進行 (>0 で突進中)。
              smilerLight?: boolean; smilerLightT?: number; smilerLungeT?: number;
              smilerLungeX?: number; smilerFromX?: number;
          }
        | null = null;

    // 状態
    private phase: Phase = "running";
    private stage = 1;
    private crew = START_CREW;
    // クリーン走行回復 (耐性の床): noHitT=最後の被弾からの経過秒・crewPeak=このランの到達最大数 (回復上限)・regenCarry=端数繰り越し
    private noHitT = 0;
    private crewPeak = START_CREW;
    private regenCarry = 0;
    private weaponTier = 0; // 「選択中の武器 id」(0 拳銃 / 1 ライフル / 2 ミニガン / 3 ロケキャノン)
    private ownedWeapons = new Set<number>(); // 所持済み武器 id (アーセナル)。切替で weaponTier を選ぶ。
    private rocketCdT = 0; // ロケキャノンの次発射までの残り秒 (>0 で充填中)
    private homingCdT = 0; // 追尾ミサイルの次斉射までの残り秒
    // ブーメラン (id4): 飛んでいる間は次を投げられない。戻ってきて null になると再投擲可。
    private boomerang: {
        mesh: THREE.Object3D;
        phase: "out" | "back";
        x: number;
        z: number;
        vx: number;
        vz: number;
        life: number;
        hit: Set<EntBase>;
    } | null = null;
    private armor = 0;
    // --- 装備テーマアーマー: 数値アーマーLv に上乗せする装備レイヤー (見た目＋固有効果)。解放は localStorage で永続 ---
    private unlockedArmors = loadArmorUnlocks();
    private equippedArmorId = loadArmorEquip();
    private armorMesh: THREE.InstancedMesh | null = null; // 全クルーに重ねる装甲プレート (weaponMesh と同じ per-instance 装着)
    private readonly armorMat = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.35, metalness: 0.5, envMapIntensity: 0.9 });
    private armorDrainCarry = 0; // 闇アーマーのクルー消費の端数繰り越し
    private armorBurnT = 0; // 炎アーマーの焼き判定タイマー
    private partyCrewMesh: THREE.InstancedMesh | null = null; // パーティゴアアーマー時、クルーをパーティゴアモデルで描く (素体は隠す)
    private get isPartyArmor(): boolean { return this.equippedArmorId === "party"; }
    private get equippedArmorDef(): ArmorDef { return armorById(this.equippedArmorId); }
    private get effectiveArmor(): number { return this.armor + this.equippedArmorDef.extraArmor; } // ％軽減式に使う実効アーマー
    private shieldHits = 0; // 残りの「完全ブロック回数」。被弾を 1 回まるごと無効化して消費し、0 で割れる。
    private shieldBar: THREE.Object3D | null = null; // クルー前面の遮蔽バリア (shieldHits>0 で表示)
    private shieldBarMat: THREE.MeshBasicMaterial | null = null; // バリア面の色/不透明度を残量で変える
    private shieldHitFlash = 0; // 被弾でバリアがパッと光る残り時間
    // --- バックシールド: 前面シールドの鏡像。背後からの飛来攻撃 (flyer) を完全ブロックする ---
    private backShieldHits = 0; // 残りの「背後ブロック回数」。0 で割れる。
    private backShieldBar: THREE.Object3D | null = null; // クルー背面の遮蔽バリア (backShieldHits>0 で表示)
    private backShieldBarMat: THREE.MeshBasicMaterial | null = null; // 背面バリアの色/不透明度を残量で変える
    private backShieldHitFlash = 0; // 背後被弾でバリアがパッと光る残り時間
    // --- 天井シールド: クルー頭上に張る湾曲キャノピー。真上から降る攻撃 (overhead) を完全ブロックする ---
    private ceilShieldHits = 0; // 残りの「頭上ブロック回数」。0 で割れる。
    private ceilShieldBar: THREE.Object3D | null = null; // クルー頭上のキャノピー (ceilShieldHits>0 で表示)
    private ceilShieldBarMat: THREE.MeshBasicMaterial | null = null; // キャノピー面の色/不透明度を残量で変える
    private ceilShieldHitFlash = 0; // 頭上被弾でキャノピーがパッと光る残り時間
    private fireBuffT = 0; // G11 火力テンプレの一時バフ残り秒 (>0 で firepower ×FIRE_BUFF_MULT)
    // --- C 操作反転ゾーン: >0 の間だけ左右の操作が反転 (カメラに紫のティント) ---
    private invertT = 0; // 反転の残り秒
    private invertTint: THREE.Mesh | null = null; // カメラ子の全画面ティント (反転=紫 / 重力=水色を兼用)
    // --- D 重力反転ゾーン: >0 の間だけ世界が上下反転 (カメラ180°ロール + クルーが天井へ) ---
    private gravT = 0; // 反転の残り秒
    private gravRoll = 0; // イーズされた現在のロール量 (0→1)
    // Level 0 ターン壁の通過カメラ・スイング (曲がった体感)。turnSwingT>0 の間 lookAt 後にヨーを重ねる。
    private turnSwingT = 0;
    private turnSwingDir = 0; // -1=左へ / +1=右へ
    private turnWallT = 3.0; // Level 0 で次の行き止まり壁までの残り秒
    // --- 波動砲 (ストック制パニックボタン。取得で在庫 +1・召喚で前面の盾になり・再押下で一閃。横移動が鈍る) ---
    private cannonStock = 0; // 在庫 (0..CANNON_MAX_STOCK)。取得で +1・召喚で -1。
    private cannonShieldHp = 0; // 召喚中の盾耐久 (受け止めるたび減り・0 で砕けて発射不能)
    private cannonHeld = false; // 召喚中 (前面に砲台を出している)
    private cannonFiring = 0; // >0 = 持続ビーム発射中 (この間ずっとレーンを焼き続ける)
    private cannonBeam: THREE.Object3D | null = null; // 発射中のビーム本体 (持続表示)
    private cannonMesh: THREE.Object3D | null = null;
    private cannonFlashT = 0; // 前面で攻撃を受け止めた時の発光残り時間
    // --- 360°タレット (展開型コンパニオン): 群衆に追従し最寄りの敵を全方位射撃。耐久 0 で破壊。 ---
    private turret: {
        obj: THREE.Group; yaw: THREE.Object3D; pitch: THREE.Object3D; muzzle: THREE.Mesh;
        hp: number; max: number; bar: THREE.Sprite; fireT: number;
    } | null = null;
    // --- 周回ガンドローン (台数制コンパニオン): 群衆を旋回し外向きに自動射撃。 ---
    private drones = 0;
    private droneRig: { obj: THREE.Group; yaw: THREE.Object3D; muzzle: THREE.Mesh; fireT: number }[] = [];
    // --- 緊急イベント (画面が暗転して EMERGENCY → 特殊シーケンス。第1弾=Backrooms Level !) ---
    private eventActive = false;
    private eventTimer = 0; // イベント残り秒
    private eventIntro = 0; // noclip 落下演出の残り秒
    private eventSpawnT = 0; // 襲撃スポーンの間隔タイマー
    private paTimer = 0; // 次の館内放送までの残り秒
    private eventDoneStage = 0; // 直近でイベントを出したステージ番号 (連発防止)
    private lastEventStage = -99; // 最後にイベントが起きたステージ (一定ステージ間隔を空ける)
    private corridor: THREE.Object3D | null = null; // 病院の廊下メッシュ (イベント中のみ表示)
    private corridorPanels: THREE.Mesh[] = []; // 天井の赤い照明 (ちらつかせる)
    private corridorScroll: THREE.Object3D[] = []; // 手前へ流して通過させるランドマーク (ドア/器具/血痕/サイン)
    private eventEl: HTMLElement | null = null; // 赤いビネット + EMERGENCY の DOM オーバーレイ
    private fenceMeshes: THREE.Mesh[] = []; // 両脇の柵 (イベント中は隠す)
    // 柵の「隠し裂け目」(敢えてノークリップして Level ! に入るための秘密の入口)。
    // 同時に 1 つだけ・稀に出現。柵の縁へ群衆を押し込むと自発的に Level ! へ落ちる。
    private secretGap: { side: -1 | 1; z: number; obj: THREE.Object3D; hinted?: boolean } | null = null;
    private hospitalTex!: THREE.CanvasTexture; // 病院の床タイル (イベント中だけ走行レーンに差し替え)
    private robots = 0;
    // 援軍メカのホーミングミサイル (飛行中のみ保持・最大 this.robots 発)。target が死んだら直進して寿命切れ。
    private missiles: Array<{
        mesh: THREE.Object3D; x: number; y: number; z: number;
        vx: number; vy: number; vz: number; life: number; target: HpEnt | null;
        launcher?: boolean; // true = 追尾ミサイルランチャー発 (固定ダメージ + mech/シールドロボに特効)
    }> = [];
    private robotFireT = 0; // 次のミサイル発射までの残り秒 (機体ごとに少しずらして撃つ)
    private robotPdT = 0; // ロボのポイントディフェンス: 次の敵ミサイル迎撃までの残り秒 (robots に反比例)
    // smiler の消灯中だけ保存する照明スナップショット (復帰で戻す)。dark 中のみ非 null。
    private smilerDarkSaved: { hemi: number; dir: number; fogN: number; fogF: number; fogC: number } | null = null;
    // 敵側ミサイル (mech ボス / シールドロボが撃つ)。床に予告リングを出し、上/前/斜めから飛来して着弾で AoE。
    // 横に避ければ回避可能。overhead=true は頭上からなので前面シールド/砲台を貫通する。
    private enemyMissiles: Array<{
        mesh: THREE.Object3D; tell: THREE.Mesh; x: number; y: number; z: number;
        targetX: number; targetZ: number; t: number; R: number; overhead: boolean; col: number;
    }> = [];
    private coins = 0; // ラン内の所持コイン (敵撃破/箱破壊で増え、ステージ間ショップで使う)
    private combo = 0; // 連続撃破コンボ数 (途切れるまで)。コイン/火力に倍率
    private comboTimer = 0; // コンボが途切れるまでの残り秒 (撃破ごとにリセット)
    // --- 時間停止ボス (chronos) ---
    private timeStop = 0; // >0 = 時間停止中 (フリーズ秒)。モノクロ + 操作不能。
    private timeStopArm = 0; // 停止前の予告秒 (この間は移動して安全レーンへ逃げる)
    private timeStopFilterOn = false; // canvas のモノクロ filter の現在状態 (transition 時のみ書換)
    private chronoSafe = 0; // 時間停止で開ける安全レーンの中心 x
    private distance = 0;
    private speed = BASE_SPEED;
    private warpDashT = 0; // ワープ通過後の視覚ダッシュ残り秒 (背景スクロールだけ加速/逆流)
    private warpDashDir = 1; // ダッシュの向き (前進=+1 / 逆流=-1)
    private spawnTimer = 0;
    private clearTimer = 0;
    private nextLane = 0;
    private centroidX = 0;
    private targetX = 0;
    private crowdHalfW = 0.5; // 群衆の横幅 (位置ベース当たり判定に使う)
    private drainCarry = 0; // ボスの継続ダメージの貯め (まとめて見える弾で発射するため繰り越す)
    private bossFireTimer = 0; // 次のボス攻撃弾までの残り秒
    private battleTime = 0; // ボス戦の経過秒 (時間で火力ブースト → 必ず決着させる)
    // --- バランス検証用デバッグ計測 ---
    private stageTime = 0; // 現ステージの経過秒 (走行＋ボス戦)
    private spawnLog: Record<string, number> = {}; // 現ステージの出現ギミック数
    private debugOn = false; // F2 or ?crdebug で切替えるデバッグ表示
    private fireCooldown = 0;
    private minigunHeat = 0; // ミニガン過熱メーター (0..1)。撃つと溜まり満タンで過熱。
    private minigunOverheat = 0; // >0 = 過熱ロック中 (発射不可)。冷えるまでの残り秒。
    private heatGauge!: HTMLElement; // 丸い過熱ゲージ (DOM・ミニガン選択時のみ表示)
    private readonly sfx = new GameAudio(); // 合成 WebAudio (効果音 + ループ BGM)
    private critTimer = 0; // 次のクリティカル判定までの残り秒 (periodic な会心バースト)
    private weatherAcc = 0; // 天候パーティクルのレート蓄積
    private shake = 0;
    // シェイクは「一時オフセット」として扱う: 毎フレ先頭で前回分を引いて論理位置へ戻し、末尾で新たに足す。
    // (直接 camera.position に足しっぱなしだと y がランダムウォークで蓄積し、視点がじわじわ水平化する不具合になる)
    private shakeOffX = 0;
    private shakeOffY = 0;
    private bestAtStart = loadBest();
    // 個体として保持するクルー (rx/rz=隊列内の現在相対位置・lerp で詰め直す, ph=bob 位相)
    private members: Member[] = [];

    // パーティクル用テクスチャ (一度だけ生成・ゲーム中は破棄しない)
    private glowTex!: THREE.CanvasTexture;
    private ringTex!: THREE.CanvasTexture;
    private softTex!: THREE.CanvasTexture;
    private streakTex!: THREE.CanvasTexture;
    // ホロゲート用の共有テクスチャ (一度だけ生成・.map 経由参照なので disposeObj では消えない)
    private gatePanelTex!: THREE.CanvasTexture; // vertical energy gradient (scrolls via UV offset)
    private gateArrowTex!: THREE.CanvasTexture; // soft up-chevron glow
    // プールされたパーティクル
    private readonly POOL = 256;
    private parts: Particle[] = [];
    private freeParts: number[] = [];
    // スクラッチ (再利用・パーティクル毎にアロケートしない)
    private readonly _v = new THREE.Vector3();
    // フレームごとのカウンタ
    private trailTick = 0;

    // DOM HUD
    private hud!: HTMLElement;
    private hudCount!: HTMLElement;
    private hudWeapon!: HTMLElement;
    private hudStage!: HTMLElement;
    private weaponBar: HTMLElement | null = null; // 画面下中央の所持武器バー (タップで切替)
    private weaponSlots: HTMLElement[] = []; // 武器バーの各アイコン要素 (id 順)
    private cannonBtn: HTMLElement | null = null; // 画面右下の波動砲ボタン (召喚 / 発射を切替)
    private banner!: HTMLElement;
    private resultEl: HTMLElement | null = null;
    private shopEl: HTMLElement | null = null;
    private debugEl: HTMLElement | null = null;

    private raf = 0;
    private disposed = false;
    private pointerDown = false;
    private keyLeft = false;
    private keyRight = false;

    constructor(host: HTMLElement, onExit: () => void) {
        this.host = host;
        this.onExit = onExit;

        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(Math.min(globalThis.devicePixelRatio || 1, 2));
        this.renderer.setSize(host.clientWidth || 360, host.clientHeight || 640);
        host.appendChild(this.renderer.domElement);
        this.renderer.domElement.style.touchAction = "none";
        this.renderer.domElement.style.display = "block";

        // クルーの MeshStandard にソフトな映り込みを与える環境マップ (おもちゃプラスチック質感)。
        // RoomEnvironment を PMREM で焼いて scene.environment へ。MeshStandard 以外には無影響。
        const pmrem = new THREE.PMREMGenerator(this.renderer);
        this.scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
        pmrem.dispose();

        // 空/フォグはプレースホルダ。applyBiome(1) が S1 (forest) に上書きする (唯一の真実)。
        this.scene.background = new THREE.Color(0x9ad7ef);
        this.scene.fog = new THREE.Fog(0xcfe8c4, 60, 150);

        this.camera = new THREE.PerspectiveCamera(58, (host.clientWidth || 360) / (host.clientHeight || 640), 0.1, 400);
        this.camera.position.set(0, 6.4, -9);
        this.camera.lookAt(0, 1.0, 16);

        this.woodMat = new THREE.MeshLambertMaterial({ color: 0xffffff });
        this.buildWorld();
        this.buildCrowdMesh();
        this.buildShieldBarrier();
        this.buildBackShieldBarrier();
        this.buildCeilShieldBarrier();
        this.rebuildArmorMesh(); // 永続化された装備アーマーの装甲メッシュを用意
        this.buildCorridor();
        this.buildHud();
        this.buildParticlePool();
        this.applyBiome(1); // S1 = forest を一括適用 (毎ステージ遷移と同じ経路)
        void loadBacteriaModel(); // bacteria FBX を裏で先読み (await しない・間に合えば実モデルになる)
        void loadPartygoerModel(); // partygoer GLTF を裏で先読み (間に合えば群れが出る)
    }

    // ---------- パーティクルプール ----------
    private buildParticlePool(): void {
        this.glowTex = makeGlowTexture();
        this.ringTex = makeRingTexture();
        this.softTex = makeSoftTexture();
        this.streakTex = makeStreakTexture();
        this.gatePanelTex = makeFieldTexture();
        this.gatePanelTex.wrapT = THREE.RepeatWrapping; // REQUIRED so offset.y scroll loops
        this.gateArrowTex = makeChevronTexture();
        for (let i = 0; i < this.POOL; i++) {
            const mat = new THREE.SpriteMaterial({
                map: this.glowTex, transparent: true, depthWrite: false,
                blending: THREE.AdditiveBlending, opacity: 0,
            });
            const sp = new THREE.Sprite(mat);
            sp.visible = false;
            this.scene.add(sp);
            this.parts.push({
                sp, alive: false, life: 0, max: 1, vel: new THREE.Vector3(),
                grav: 0, drag: 0, size0: 0.3, size1: 0.3, spin: 0,
                col0: new THREE.Color(0xffffff), col1: new THREE.Color(0xffffff), add: true, hump: false,
            });
            this.freeParts.push(i);
        }
    }

    // ---------- パーティクル: 唯一のプリミティブ ----------
    private p_emit(pos: THREE.Vector3, o: {
        tex?: "glow" | "ring" | "soft" | "streak"; vx?: number; vy?: number; vz?: number;
        grav?: number; drag?: number; size0: number; size1: number; spin?: number;
        col0: number; col1?: number; life: number; add?: boolean; hump?: boolean;
    }): void {
        const idx = this.freeParts.pop();
        if (idx === undefined) return; // プール枯渇 → 何もしない (ハードキャップ)
        const p = this.parts[idx];
        const m = p.sp.material as THREE.SpriteMaterial;
        m.map = o.tex === "ring" ? this.ringTex : o.tex === "soft" ? this.softTex : o.tex === "streak" ? this.streakTex : this.glowTex;
        m.blending = (o.add ?? true) ? THREE.AdditiveBlending : THREE.NormalBlending;
        m.rotation = 0;
        p.sp.position.copy(pos);
        p.vel.set(o.vx ?? 0, o.vy ?? 0, o.vz ?? 0);
        p.grav = o.grav ?? 0;
        p.drag = o.drag ?? 0;
        p.size0 = o.size0;
        p.size1 = o.size1;
        p.spin = o.spin ?? 0;
        p.col0.set(o.col0);
        p.col1.set(o.col1 ?? o.col0);
        p.life = o.life;
        p.max = o.life;
        p.add = o.add ?? true;
        p.hump = o.hump ?? false;
        p.alive = true;
        p.sp.visible = true;
        p.sp.scale.set(o.size0, o.size0, 1);
        m.color.copy(p.col0);
        m.opacity = o.hump ? 0 : 1;
    }

    private updateParticles(dt: number): void {
        for (let i = 0; i < this.parts.length; i++) {
            const p = this.parts[i];
            if (!p.alive) continue;
            p.life -= dt;
            if (p.life <= 0) {
                p.alive = false;
                p.sp.visible = false;
                this.freeParts.push(i);
                continue;
            }
            const k = 1 - p.life / p.max; // 0 -> 1
            p.vel.y += p.grav * dt;
            if (p.drag > 0) p.vel.multiplyScalar(Math.max(0, 1 - p.drag * dt));
            p.sp.position.addScaledVector(p.vel, dt);
            const m = p.sp.material as THREE.SpriteMaterial;
            if (p.spin) m.rotation += p.spin * dt;
            const s = p.size0 + (p.size1 - p.size0) * Ease.quadOut(k);
            p.sp.scale.set(s, s, 1);
            m.opacity = p.hump ? Math.sin(k * Math.PI) : 1 - k;
            m.color.copy(p.col0).lerp(p.col1, k);
        }
    }

    // ---------- 複合 VFX ヘルパー (p_emit の上に組む) ----------
    private emitBurst(pos: THREE.Vector3, n: number, o: {
        speed: number; spread?: number; grav?: number; drag?: number;
        size0: number; size1: number; col0: number; col1?: number; life: number;
        tex?: "glow" | "soft" | "streak"; up?: boolean;
    }): void {
        for (let i = 0; i < n; i++) {
            const a = Math.random() * Math.PI * 2;
            const sp = o.speed * (0.6 + 0.4 * Math.random());
            const vx = Math.cos(a) * sp;
            const vy = (o.up ? Math.abs(Math.sin(a)) : Math.sin(a)) * sp + (o.up ? sp * 0.5 : 0);
            this._v.copy(pos);
            this.p_emit(this._v, {
                tex: o.tex ?? "glow", vx, vy, vz: (Math.random() - 0.5) * sp * 0.3,
                grav: o.grav ?? 0, drag: o.drag ?? 2.0, size0: o.size0 * (0.7 + 0.6 * Math.random()), size1: o.size1,
                spin: (Math.random() - 0.5) * 6, col0: o.col0, col1: o.col1, life: o.life * (0.7 + 0.5 * Math.random()),
            });
        }
    }

    private shockRing(pos: THREE.Vector3, color: number, maxScale = 6, life = 0.35): void {
        this.p_emit(pos, { tex: "ring", size0: 0.6, size1: maxScale, col0: color, life, add: true });
    }

    private impactBurst(pos: THREE.Vector3, el: "king" | "dragon" | "angel" | "leviathan" | "chronos" | "crew" | "mech" | "smiler", power = 1): void {
        // 1) 閃光
        this.p_emit(pos, { tex: "glow", size0: 0.6, size1: 2.4 * power, col0: 0xffffff, life: 0.1, add: true });
        // 2) 衝撃波 (大ヒットは 2 連)
        const ringCol = { king: 0xffd23b, dragon: 0xff7a2a, angel: 0xfff0c0, leviathan: 0xbfffff, chronos: 0xc0a0ff, crew: 0xeaf2ff, mech: 0x6fe0ff, smiler: 0x9bff6a }[el];
        this.shockRing(pos, ringCol, 4.5 * power, 0.34);
        if (power >= 1) this.shockRing(pos, ringCol, 2.6 * power, 0.22);
        // 3) 放射スパーク
        const sparkN = Math.round((el === "crew" ? 3 : 11) * power);
        this.emitBurst(pos, sparkN, {
            speed: 5 * power, grav: el === "leviathan" ? -2 : -1, drag: 2.4,
            size0: 0.5, size1: 0.05, col0: ringCol, life: 0.4,
        });
        // 4) 属性の尾
        if (el === "dragon") {
            this.emitBurst(pos, 6, { speed: 3, up: true, grav: -3, size0: 0.6, size1: 0.1, col0: 0xffae3b, col1: 0x3a1a0a, life: 0.7, tex: "soft" });
            for (let i = 0; i < 3; i++)
                this.p_emit(this._v.copy(pos).add(new THREE.Vector3((Math.random() - 0.5) * 1.2, 0, (Math.random() - 0.5) * 1.2)),
                    { tex: "soft", vy: 0.6, grav: -0.5, drag: 0.8, size0: 0.8, size1: 2.2, col0: 0x6a6a6a, col1: 0x2a2a2a, life: 0.9, add: false });
        } else if (el === "angel") {
            for (let i = 0; i < 5; i++)
                this.p_emit(pos, { tex: "soft", vx: (Math.random() - 0.5) * 2, vy: 0.4 + Math.random(), grav: -0.6, drag: 1.2, size0: 0.3, size1: 0.7, col0: 0xfff4cf, col1: 0xffd86b, life: 1.0, hump: true });
            for (let i = 0; i < 3; i++)
                this.p_emit(pos, { tex: "streak", vx: (Math.random() - 0.5) * 1.5, vy: 0.3, grav: 0.4, drag: 0.4, size0: 0.5, size1: 0.4, spin: (Math.random() - 0.5) * 2, col0: 0xffffff, life: 1.2 });
        } else if (el === "leviathan") {
            this.emitBurst(pos, 8, { speed: 5, up: true, grav: 9, size0: 0.4, size1: 0.1, col0: 0xbfffff, life: 0.6 }); // 水しぶき弧
            for (let i = 0; i < 5; i++)
                this.p_emit(pos, { tex: "soft", vy: 1 + Math.random(), grav: -1.5, drag: 1.5, size0: 0.15, size1: 0.3, col0: 0xeaffff, life: 0.8, hump: true }); // 泡が昇る
            this.p_emit(pos, { tex: "soft", size0: 0.8, size1: 2.4, col0: 0xffffff, life: 0.5, add: false }); // 泡しぶき
        } else if (el === "king") {
            this.emitBurst(pos, 8, { speed: 4, grav: 7, size0: 0.4, size1: 0.08, col0: 0xffd23b, col1: 0xff8a3b, life: 0.6 }); // 金貨が落ちる
        } else if (el === "mech") {
            // 機械の爆発: 火花 + 黒煙のかけら + シアンのエネルギー片
            this.emitBurst(pos, 10, { speed: 6, grav: 8, size0: 0.4, size1: 0.06, col0: 0xffd86b, col1: 0xff5a2a, life: 0.5 });
            this.emitBurst(pos, 4, { speed: 3, up: true, grav: -2, size0: 0.5, size1: 0.12, col0: 0x6fe0ff, col1: 0x1a3a5a, life: 0.6, tex: "soft" });
        } else if (el === "smiler") {
            // 不気味な緑の燐光がにじむ
            for (let i = 0; i < 5; i++)
                this.p_emit(pos, { tex: "soft", vx: (Math.random() - 0.5) * 1.6, vy: 0.3 + Math.random() * 0.6, grav: -0.8, drag: 1.2, size0: 0.4, size1: 0.9, col0: 0xc9ff8a, col1: 0x2a4a14, life: 0.9, hump: true });
        }
        // 5) シェイク
        const shk = { king: 0.3, dragon: 0.4, angel: 0.18, leviathan: 0.34, chronos: 0.36, crew: 0.07, mech: 0.45, smiler: 0.3 }[el] * power;
        this.shake = Math.max(this.shake, shk);
    }

    private chargeGather(center: THREE.Vector3, el: string, intensity: number): void {
        if (Math.random() > intensity * 0.6) return; // ウィンドアップの近さでレート制限
        const a = Math.random() * Math.PI * 2, r = 1.8 + Math.random() * 0.6;
        this._v.set(center.x + Math.cos(a) * r, center.y + (Math.random() - 0.5) * 1.2, center.z + Math.sin(a) * r);
        const col = ({ king: 0xffd23b, dragon: 0xff8a2a, angel: 0xfff0c0, leviathan: 0x7affe0 } as Record<string, number>)[el] ?? 0xffffff;
        // 速度は中心に向く
        this.p_emit(this._v, {
            tex: "soft", vx: (center.x - this._v.x) * 2.2, vy: (center.y - this._v.y) * 2.2, vz: (center.z - this._v.z) * 2.2,
            drag: 0.5, size0: 0.35, size1: 0.05, col0: col, life: 0.45, hump: true,
        });
    }

    private projGlow(mesh: THREE.Object3D, color: number, scale = 1.6): THREE.Sprite {
        const sp = new THREE.Sprite(new THREE.SpriteMaterial({
            map: this.glowTex, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, color,
        }));
        sp.scale.set(scale, scale, 1);
        mesh.add(sp); // 子 → コアに追従・親と一緒に disposeObj で破棄
        return sp;
    }

    private trailEmit(pos: THREE.Vector3, color: number, el?: string): void {
        const grav = el === "dragon" || el === "angel" ? -1.2 : el === "leviathan" ? 3 : 0;
        this.p_emit(pos, {
            tex: el === "leviathan" || el === "dragon" ? "soft" : "glow",
            vx: (Math.random() - 0.5) * 0.6, vy: (Math.random() - 0.5) * 0.4, grav, drag: 1.5,
            size0: 0.4, size1: 0.08, col0: color, life: 0.3,
        });
    }

    // ---------- 世界 ----------
    private buildWorld(): void {
        // ライト・地面・フォグはフィールドに保持して applyBiome で in-place に色だけ変える。
        this.hemi = new THREE.HemisphereLight(0xeaf7ff, 0x5a8a3a, 1.05);
        this.scene.add(this.hemi);
        this.dir = new THREE.DirectionalLight(0xfff2d8, 1.15);
        this.dir.position.set(-6, 12, -4);
        this.scene.add(this.dir);
        this.fog = this.scene.fog as THREE.Fog; // コンストラクタで生成済みのプレースホルダ

        // バイオームごとのスクロール テクスチャを一度だけ焼いて常駐させる。
        for (let i = 0; i < BIOMES.length; i++) {
            const lt = makeBiomeLaneTexture(i);
            lt.repeat.set(2, 60);
            this.laneTexes.push(lt);
            const ft = makeBiomeFenceTexture(i);
            ft.repeat.set(60, 1);
            this.fenceTexes.push(ft);
            this.backdropTexes.push(makeBiomeBackdropTexture(i));
        }

        // 草原 (一面の緑)。grassMat は applyBiome で色だけ変える。
        this.grassMat = new THREE.MeshLambertMaterial({ color: 0x7ec24a });
        const grass = new THREE.Mesh(new THREE.PlaneGeometry(400, 600), this.grassMat);
        grass.rotation.x = -Math.PI / 2;
        grass.position.set(0, -0.05, 120);
        this.scene.add(grass);

        // 土の小道 (走行レーン)。上面が y=0 に来るよう下にずらし、足元 y=0 のキャラが道に立つ。
        // 既定はバイオーム 0 (forest) → applyBiome(1) で上書きされる。
        this.pierTex = this.laneTexes[0];
        this.laneMat = new THREE.MeshLambertMaterial({ map: this.pierTex });
        const trail = new THREE.Mesh(new THREE.BoxGeometry(LANE * 2, PIER_TOP, 360), this.laneMat);
        trail.position.set(0, -PIER_TOP / 2, 150);
        this.scene.add(trail);

        // 木の横木フェンス (両脇)。透過テクスチャを道に沿ってスクロールさせ「流れる柵」に。
        // 左右とも同じ向き(rotateY +90°)＋両面描画にして、共有テクスチャを 1 方向に流す。
        this.fenceTex = this.fenceTexes[0];
        this.fenceMat = new THREE.MeshLambertMaterial({ map: this.fenceTex, transparent: true, alphaTest: 0.5, side: THREE.DoubleSide });
        for (const sx of [-LANE, LANE]) {
            const fence = new THREE.Mesh(new THREE.PlaneGeometry(360, 1.2), this.fenceMat);
            fence.rotation.y = Math.PI / 2;
            fence.position.set(sx, 0.55, 150);
            this.scene.add(fence);
            this.fenceMeshes.push(fence); // イベント中に隠すため保持
        }

        // 遠景シルエット帯 (1 quad)。fog が距離感の 9 割を担い、これは薄い補助。
        this.backdropMat = new THREE.MeshBasicMaterial({ map: this.backdropTexes[0], transparent: true, depthWrite: false });
        const backdrop = new THREE.Mesh(new THREE.PlaneGeometry(140, 30), this.backdropMat);
        backdrop.position.set(0, 12, 142);
        backdrop.rotation.y = Math.PI; // カメラ (奥向き) に正対
        this.scene.add(backdrop);

        // シーナリーのプロップ プールを作る (側方の木/岩など)。
        this.buildSceneryPools();
    }

    // ---------- シーナリーのプロップ プール ----------
    private buildSceneryPools(): void {
        this.propMat = new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true });
        const all = (fn: (b: number) => THREE.BufferGeometry) => BIOMES.map((_, b) => fn(b));
        const slots: { count: number; geoms: THREE.BufferGeometry[] }[] = [
            { count: POOL_A, geoms: all(biomePropA) },
            { count: POOL_B, geoms: all(biomePropB) },
            { count: POOL_C, geoms: all(biomePropC) },
        ];
        for (const s of slots) {
            const mesh = new THREE.InstancedMesh(s.geoms[0], this.propMat, s.count);
            mesh.frustumCulled = false; // crowdMesh と同様に手動リサイクル
            mesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage); // 毎フレーム更新
            this.scene.add(mesh);
            const data = new Float32Array(s.count * 5);
            for (let i = 0; i < s.count; i++) this.seedProp(data, i, true);
            const pool = { mesh, data, count: s.count, geoms: s.geoms };
            this.propPools.push(pool);
            this.writeAllProps(pool); // 初期行列を焼く
        }
    }

    /** 1 個のプロップの論理状態を乱択 (ゼロアロケーション)。fresh=帯全体に散布 / 再生=奥端のみ。 */
    private seedProp(data: Float32Array, i: number, fresh: boolean): void {
        const b = i * 5;
        const side = Math.random() < 0.5 ? -1 : 1;
        data[b] = side * (SIDE_NEAR + Math.random() * SIDE_DEPTH); // x (外側へ扇状)
        data[b + 1] = fresh
            ? RECYCLE_NEAR + Math.random() * RECYCLE_BAND // 初期化時は帯全体に散布
            : RECYCLE_FAR - Math.random() * 6; // 再生時は帯の奥端
        data[b + 2] = 0.7 + Math.random() * 0.8; // scale 0.7..1.5
        data[b + 3] = Math.random() * 6.283; // rotY
        data[b + 4] = side;
    }

    /** プール全体の行列を data から焼く (スクロールなし・初期化/再シード用)。 */
    private writeAllProps(pool: CrewRun3D["propPools"][number]): void {
        const d = this.dummy;
        const data = pool.data;
        for (let i = 0; i < pool.count; i++) {
            const b = i * 5;
            d.position.set(data[b], PROP_Y, data[b + 1]);
            d.rotation.set(0, data[b + 3], 0);
            d.scale.setScalar(data[b + 2]);
            d.updateMatrix();
            pool.mesh.setMatrixAt(i, d.matrix);
        }
        pool.mesh.instanceMatrix.needsUpdate = true;
    }

    /** 毎フレームのプロップ前進＋リサイクル (走行中のみ・ゼロアロケーション)。 */
    private updateScenery(dt: number, mul = 1): void {
        if (this.phase !== "running") return; // 戦闘/クリア中は止める (レーン停止に合わせる)
        const dz = this.speed * mul * dt; // エンティティと同じスカラ (ワープ視覚ダッシュ倍率込み)
        const d = this.dummy; // 共有スクラッチ
        for (const pool of this.propPools) {
            const data = pool.data;
            for (let i = 0; i < pool.count; i++) {
                const b = i * 5;
                data[b + 1] -= dz; // 手前へ
                if (data[b + 1] < RECYCLE_NEAR) { // カメラ通過 → 帯の奥へ折り返し
                    data[b + 1] += RECYCLE_BAND; // 帯長を足す (ドリフトなし)
                    this.seedProp(data, i, false); // x/scale/rot を引き直して再び扇状に
                }
                d.position.set(data[b], PROP_Y, data[b + 1]);
                d.rotation.set(0, data[b + 3], 0);
                d.scale.setScalar(data[b + 2]);
                d.updateMatrix();
                pool.mesh.setMatrixAt(i, d.matrix);
            }
            pool.mesh.instanceMatrix.needsUpdate = true; // プールごとに 1 回
        }
        // 遠景帯の緩いパララックス (現バイオームの map を fenceTex と同じ要領でゆっくり流す)。
        const bd = this.backdropTexes[this.biomeIdx];
        if (bd) bd.offset.x = (bd.offset.x - 0.1 * this.speed * mul * dt * 0.01) % 1;
    }

    // ---------- バイオーム切替 (安価・リークなし) ----------
    private applyBiome(stage: number): void {
        const N = BIOMES.length;
        const i = (((stage - 1) % N) + N) % N;
        const b = BIOMES[i];
        this.biomeIdx = i;
        // --- 共有のライト/フォグ/地面/空を in-place に色変え (ゼロアロケーション) ---
        (this.scene.background as THREE.Color).setHex(b.sky);
        this.fog.color.setHex(b.fogCol);
        this.fog.near = b.fogNear;
        this.fog.far = b.fogFar;
        this.grassMat.color.setHex(b.ground);
        this.hemi.color.setHex(b.hemiSky);
        this.hemi.groundColor.setHex(b.hemiGround);
        this.dir.color.setHex(b.dirCol);
        this.dir.intensity = b.dirInt;
        // --- レーン/フェンス テクスチャのポインタ差し替え (repeat+offset をコピーしてスクロール位相を保つ) ---
        // 【最重要】laneMat.map = lt の後に this.pierTex = lt を必ず行う。さもないと
        // 毎フレームのスクロール (this.pierTex.offset.y -= ...) が外れた旧テクスチャを回し続け地面が凍る。
        const lt = this.laneTexes[i];
        lt.repeat.copy(this.pierTex.repeat);
        lt.offset.copy(this.pierTex.offset);
        this.laneMat.map = lt;
        this.laneMat.needsUpdate = true;
        this.pierTex = lt; // スクロール対象を付け替える!
        const ft = this.fenceTexes[i];
        ft.repeat.copy(this.fenceTex.repeat);
        ft.offset.copy(this.fenceTex.offset);
        this.fenceMat.map = ft;
        this.fenceMat.needsUpdate = true;
        this.fenceTex = ft; // スクロール対象を付け替える!
        // --- プロップ プール: 焼いたジオメトリのポインタを差し替え + 即時再散布 ---
        for (const pool of this.propPools) {
            pool.mesh.geometry = pool.geoms[i]; // ポインタ差し替え
            for (let k = 0; k < pool.count; k++) this.seedProp(pool.data, k, true); // 全体を再散布
            this.writeAllProps(pool);
        }
        // 遠景帯の差し替え
        if (this.backdropMat) {
            this.backdropMat.map = this.backdropTexes[i];
            this.backdropMat.needsUpdate = true;
        }
    }

    /** 連続素体クルーのスケルトン (約 11 ボーン) を rest 姿勢で組む。root=hips。 */
    private buildCrewSkeleton(): { skeleton: THREE.Skeleton; bones: THREE.Bone[]; root: THREE.Bone; boneIndexOf: Record<CrewBoneName, number> } {
        const bones: THREE.Bone[] = [];
        const byName: Partial<Record<CrewBoneName, THREE.Bone>> = {};
        const boneIndexOf = {} as Record<CrewBoneName, number>;
        // 親が先に並ぶよう CREW_BONE_NAMES 順に生成 (CREW_BONE_PARENT は親が必ず先)
        CREW_BONE_NAMES.forEach((name, idx) => {
            const b = new THREE.Bone();
            b.name = name;
            bones.push(b);
            byName[name] = b;
            boneIndexOf[name] = idx;
        });
        // rest ワールド座標から親相対のローカル位置を求めて親へ add
        let root!: THREE.Bone;
        for (const name of CREW_BONE_NAMES) {
            const b = byName[name]!;
            const parentName = CREW_BONE_PARENT[name];
            const wp = CREW_BONE_REST[name];
            if (parentName) {
                const pp = CREW_BONE_REST[parentName];
                b.position.set(wp[0] - pp[0], wp[1] - pp[1], wp[2] - pp[2]);
                byName[parentName]!.add(b);
            } else {
                b.position.set(wp[0], wp[1], wp[2]);
                root = b;
            }
        }
        root.updateMatrixWorld(true);
        const skeleton = new THREE.Skeleton(bones);
        return { skeleton, bones, root, boneIndexOf };
    }

    private buildCrowdMesh(): void {
        // 1) スケルトンを組み、boneIndex を確定
        const { skeleton, bones, root, boneIndexOf } = this.buildCrewSkeleton();
        this.crewSkeleton = skeleton;
        this.crewBones = bones;
        // 2) 素体生成。優先順: SDF+smin (一枚皮・関節フィレット) > メタボール > プリミティブ。
        let geo: THREE.BufferGeometry;
        const implicit = USE_SDF_BODY ? buildCrewBodySDF() : (USE_METABALL_BODY ? buildCrewBodyMetaball() : null);
        if (implicit) {
            attachCrewSkinWeights(implicit, crewBoneSegments(), boneIndexOf);
            geo = implicit;
        } else {
            geo = buildCrewBodyPrimitive(boneIndexOf);
        }
        this.crewBodyGeo = geo;
        // 3) root bone の親 (matrixWorld=identity) を用意。AnimationMixer のターゲットにもする。
        //    bone は scene には足さない (描画は InstancedMesh2 が boneTexture で行う)。
        this.crewRigRoot = new THREE.Object3D();
        this.crewRigRoot.add(root);
        this.crewRigRoot.updateMatrixWorld(true);
        this.crewMixerRoot = this.crewRigRoot;
        // 4) 走りクリップを作り、mixer に載せる
        const { clip, duration } = buildCrewRunClip();
        this.crewClipDur = duration;
        this.crewMixer = new THREE.AnimationMixer(this.crewMixerRoot);
        const action = this.crewMixer.clipAction(clip);
        action.play();
        // 5) InstancedMesh2 を生成し skeleton を初期化。全 capacity 分のインスタンスを確保し、
        //    可視は毎フレーム n に合わせて差分トグルする。
        this.crowdMesh = new InstancedMesh2(geo, this.crewSkinMat, { capacity: MAX_INSTANCES, renderer: this.renderer });
        this.crowdMesh.frustumCulled = false;
        this.crowdMesh.perObjectFrustumCulled = false; // 近接個体が落ちないよう per-object 視錐台カリングは切る
        this.crowdMesh.addInstances(MAX_INSTANCES, (obj) => { obj.visible = false; });
        this.crewVisibleCount = 0;
        this.crowdMesh.initSkeleton(this.crewSkeleton);
        // バケットごとのボーン matrixWorld スナップショット領域を確保
        this.crewBucketBones = Array.from({ length: CrewRun3D.PHASE_BUCKETS }, () => bones.map(() => new THREE.Matrix4()));
        this.scene.add(this.crowdMesh);
        this.syncMembers();
    }

    /**
     * 各位相バケットについて mixer を一度だけ評価し、全ボーンの matrixWorld をスナップショットする。
     * 毎フレーム冒頭に 1 回呼ぶ。これで個体ループ内は復元 + setBonesAt(false) だけで済む。
     */
    private bakeCrewBuckets(): void {
        const K = CrewRun3D.PHASE_BUCKETS;
        const bones = this.crewBones;
        for (let b = 0; b < K; b++) {
            // バケットはサイクル全体 (0..1) を固定で刻む。個体ごとの位相は層側で
            // bIdx = floor((gaitPhase + phaseOffset) * K) で選ぶ (gaitPhase はここで足さない=二重計上回避)。
            const phase = (b + 0.5) / K; // バケット中央位相
            this.crewMixer.setTime(phase * this.crewClipDur); // クリップを評価しボーンの local quaternion を書く
            // 【重要】initSkeleton(disableMatrixAutoUpdate=true) で bone.matrixWorldAutoUpdate=false に
            // なるため updateMatrixWorld(force) でも matrixWorld に伝播しない。bones は親→子順なので
            // 親の matrixWorld から手動合成する (これを怠ると全バケットがレスト姿勢になりアニメが死ぬ)。
            for (let bi = 0; bi < bones.length; bi++) {
                const bone = bones[bi];
                // 武装中は腕ボーン (7=upperArmL 8=foreArmL 10=upperArmR 11=foreArmR) を構えポーズに上書き。
                // 走りの腕ポンプを潰し、両腕を前方に上げて銃を抱える形に。脚は走りのまま。
                if (this.crewArmed) {
                    if (bi === 7) bone.quaternion.copy(CrewRun3D.AIM_UPPER_L);
                    else if (bi === 8) bone.quaternion.copy(CrewRun3D.AIM_FORE_L);
                    else if (bi === 10) bone.quaternion.copy(CrewRun3D.AIM_UPPER_R);
                    else if (bi === 11) bone.quaternion.copy(CrewRun3D.AIM_FORE_R);
                }
                bone.updateMatrix(); // local quaternion → bone.matrix
                if (bone.parent) bone.matrixWorld.multiplyMatrices(bone.parent.matrixWorld, bone.matrix);
                else bone.matrixWorld.copy(bone.matrix);
            }
            const snap = this.crewBucketBones[b];
            for (let i = 0; i < bones.length; i++) snap[i].copy(bones[i].matrixWorld);
        }
    }

    /** 基底フットプリントの一辺。これ以上は横に広げず上へ積む (山型)。最大 13 で頭打ち。 */
    private pileSide(n: number): number {
        return Math.min(13, Math.max(1, Math.ceil(Math.sqrt(Math.min(n, MAX_INSTANCES)))));
    }
    /**
     * i 番目の相対座標 (x,y,z)。小さい群れは平たい一枚、増えるとステップピラミッドで
     * 上に積み上がる (各層は一辺 side、上ほど -2 して中央寄せ)。横幅は side で頭打ち。
     */
    private pile(i: number, n: number): { x: number; y: number; z: number } {
        let side = this.pileSide(n);
        let idx = i;
        let y = 0;
        while (side > 1 && idx >= side * side) {
            idx -= side * side;
            side -= 2;
            y += 0.5;
        }
        const col = idx % side;
        const row = Math.floor(idx / side);
        return {
            x: (col - (side - 1) / 2) * CREW_GAP,
            y,
            z: -(row - (side - 1) / 2) * CREW_GAP,
        };
    }

    /**
     * 個体数を crew (上限 MAX_INSTANCES) に合わせる。増えた分は隊列の外側に追加、
     * 減らす場合は末尾を落とす (ゲート/ボス等の非位置ダメージ用)。当たり判定での
     * 「特定個体の消滅」は collideCrowd 側で直接 members を削るのでここは通らない。
     */
    private syncMembers(): void {
        const target = Math.min(MAX_INSTANCES, Math.max(0, this.crew));
        while (this.members.length < target) {
            const p = this.pile(this.members.length, target);
            // 後方からスッと隊列に入る見た目 + 有機的な個体差を一度だけ焼く (lockstep を壊す)。
            // ry を少し下げて「スロットへ持ち上がる」+ land=0.18 で合流時に着地スクワッシュ。
            this.members.push({
                rx: p.x, ry: p.y - 0.25, rz: p.z - 1.5, ph: Math.random() * 6.28,
                spd: 0.85 + Math.random() * 0.3, amp: 0.85 + Math.random() * 0.35, swy: Math.random() * 6.28,
                lean: 0.16 + Math.random() * 0.12, land: 0.18, sc: 0.9 + Math.random() * 0.22,
                phaseOffset: Math.random(),
            });
        }
        if (this.members.length > target) this.members.length = target;
        const side = this.pileSide(target);
        this.crowdHalfW = Math.min(LANE - 0.3, ((side - 1) / 2) * CREW_GAP + 0.28);
    }

    // ---------- HUD (DOM) ----------
    private buildHud(): void {
        const mk = (css: string): HTMLElement => {
            const el = document.createElement("div");
            el.style.cssText = css;
            this.host.appendChild(el);
            return el;
        };
        this.hud = mk(
            "position:absolute;top:10px;left:10px;right:10px;display:flex;gap:8px;align-items:center;" +
                "font:700 16px system-ui,sans-serif;color:#fff;text-shadow:0 2px 4px #06101c;pointer-events:none;z-index:2;",
        );
        this.hudCount = document.createElement("div");
        this.hudCount.style.cssText = "font-size:22px;color:#bfe6ff;";
        this.hudWeapon = document.createElement("div");
        this.hudWeapon.style.cssText = "flex:1;text-align:center;color:#ffd75e;";
        this.hudStage = document.createElement("div");
        this.hudStage.style.cssText = "color:#dfeaf3;";
        this.hud.append(this.hudCount, this.hudWeapon, this.hudStage);

        const close = document.createElement("button");
        close.textContent = "✕";
        close.style.cssText =
            "position:absolute;top:8px;right:8px;width:34px;height:30px;border:none;border-radius:8px;" +
            "background:#d64541;color:#fff;font-weight:700;cursor:pointer;z-index:3;";
        close.onclick = () => this.exit();
        this.host.appendChild(close);

        // ミュート切替ボタン (右上・✕ の隣)。
        const mute = document.createElement("button");
        mute.textContent = "🔊";
        mute.style.cssText =
            "position:absolute;top:8px;right:50px;width:34px;height:30px;border:none;border-radius:8px;" +
            "background:rgba(8,18,30,0.7);color:#fff;font-size:15px;cursor:pointer;z-index:3;";
        mute.onclick = () => { mute.textContent = this.sfx.toggleMute() ? "🔇" : "🔊"; };
        this.host.appendChild(mute);

        // ミニガンの丸い過熱ゲージ (時計回りに溜まる・満タンで過熱)。ミニガン選択時のみ表示。
        this.heatGauge = mk(
            "position:absolute;bottom:80px;right:18px;width:48px;height:48px;border-radius:50%;" +
                "display:none;align-items:center;justify-content:center;font-size:20px;pointer-events:none;z-index:3;" +
                "-webkit-mask:radial-gradient(transparent 42%,#000 43%);mask:radial-gradient(transparent 42%,#000 43%);",
        );

        // 画面下中央の武器バー (所持武器を横並び・タップ/クリックで切替)。pointer-events:auto で
        // ゲーム操作の steer を誤発火しない独立 DOM。各スロットは id 順に固定生成し、所持状況で表示を切替。
        this.weaponBar = mk(
            "position:absolute;bottom:14px;left:0;right:0;display:flex;gap:8px;justify-content:center;" +
                "pointer-events:none;z-index:3;",
        );
        this.weaponSlots = [];
        for (let id = 0; id < WEAPON_DEFS.length; id++) {
            const slot = document.createElement("button");
            slot.dataset.wid = String(id);
            slot.textContent = WEAPON_DEFS[id].icon;
            slot.style.cssText =
                "pointer-events:auto;width:52px;height:52px;border-radius:12px;border:2px solid #33404f;" +
                "background:rgba(8,18,30,0.7);font-size:26px;cursor:pointer;display:none;align-items:center;" +
                "justify-content:center;color:#fff;position:relative;";
            slot.onclick = (ev) => {
                ev.stopPropagation();
                this.selectWeapon(id);
            };
            // pointerdown が下の steer ハンドラへ伝播しないように止める。
            slot.addEventListener("pointerdown", (ev) => ev.stopPropagation());
            this.weaponBar.appendChild(slot);
            this.weaponSlots.push(slot);
        }

        // 画面右下の波動砲ボタン (任意タイミングで召喚→再押下で発射)。武器バー同様 pointer-events:auto の
        // 独立 DOM で、steer を誤発火しないよう pointerdown を止める。状態は updateCannonBtn で毎フレ更新。
        const cb = document.createElement("button");
        cb.style.cssText =
            "position:absolute;right:14px;bottom:78px;min-width:96px;height:56px;padding:0 12px;" +
            "border-radius:14px;border:2px solid #2a6a8a;background:rgba(8,26,42,0.82);color:#bff0ff;" +
            "font:800 18px system-ui,sans-serif;cursor:pointer;pointer-events:auto;z-index:3;display:none;" +
            "align-items:center;justify-content:center;text-shadow:0 1px 3px #000;";
        cb.onclick = (ev) => { ev.stopPropagation(); this.cannonButton(); };
        cb.addEventListener("pointerdown", (ev) => ev.stopPropagation());
        this.host.appendChild(cb);
        this.cannonBtn = cb;

        this.banner = mk(
            "position:absolute;top:36%;left:0;right:0;text-align:center;pointer-events:none;z-index:2;" +
                "font:800 34px system-ui,sans-serif;color:#ffe27a;text-shadow:0 3px 8px #06101c;opacity:0;transition:opacity .2s;",
        );

        // バランス検証用デバッグ表示 (F2 で切替・URL に ?crdebug で初期 ON)
        this.debugEl = mk(
            "position:absolute;left:8px;bottom:8px;right:8px;z-index:3;pointer-events:none;white-space:pre-wrap;" +
                "font:600 11px ui-monospace,monospace;color:#bdf5c0;text-shadow:0 1px 2px #000;",
        );
        this.debugEl.style.display = "none";
        if (typeof location !== "undefined" && location.search.includes("crdebug")) this.debugOn = true;
    }

    private updateHud(): void {
        this.hudCount.innerHTML = `👥 ${this.crew}<span style="color:#ffd75e;margin-left:10px;">🪙 ${this.coins}</span>`;
        const wdef = WEAPON_DEFS[this.weaponTier] ?? WEAPON_DEFS[0];
        const ex = this.robots > 0 ? ` 🤖×${this.robots}` : "";
        const ar = this.armor > 0 ? ` 🛡×${this.armor}` : "";
        const ea = ` ${this.equippedArmorDef.icon}`; // 装備中アーマーのアイコン
        const sh = this.shieldHits > 0 ? ` 🔵×${this.shieldHits}` : "";
        const bsh = this.backShieldHits > 0 ? ` 🟠×${this.backShieldHits}` : "";
        const csh = this.ceilShieldHits > 0 ? ` 🟣×${this.ceilShieldHits}` : "";
        const cn = this.cannonHeld ? ` ⚡盾${this.cannonShieldHp}` : this.cannonFiring > 0 ? " ⚡発射!" : this.cannonStock > 0 ? ` ⚡×${this.cannonStock}` : "";
        const tu = this.turret ? ` 🛡${Math.ceil(this.turret.hp)}` : "";
        const dr = this.drones > 0 ? ` 🛸×${this.drones}` : "";
        this.hudWeapon.textContent = `${wdef.icon} ${wdef.name}${ex}${ar}${ea}${sh}${bsh}${csh}${cn}${tu}${dr}`;
        this.hudStage.textContent = `S${this.stage} · ${Math.floor(this.distance / 4)}m`;
        // ミニガンの丸い過熱ゲージ (時計回りに溜まる)。選択中のみ表示。
        if (this.weaponTier === MINIGUN_WEAPON) {
            const over = this.minigunOverheat > 0;
            const h = this.minigunHeat;
            const col = over ? "#ff3a3a" : h > 0.7 ? "#ff8a2a" : "#ffd24a";
            this.heatGauge.style.display = "flex";
            this.heatGauge.style.background = `conic-gradient(${col} ${h * 360}deg, rgba(150,170,200,0.5) 0deg)`;
            this.heatGauge.style.filter = over ? "drop-shadow(0 0 9px #ff3a3a)" : "drop-shadow(0 1px 3px rgba(0,0,0,0.6))";
        } else if (this.heatGauge.style.display !== "none") {
            this.heatGauge.style.display = "none";
        }
        this.updateWeaponBar();
        this.updateCannonBtn();

        if (this.debugEl) {
            this.debugEl.style.display = this.debugOn ? "block" : "none";
            if (this.debugOn) {
                const sp = Object.entries(this.spawnLog)
                    .map(([k, v]) => `${k}:${v}`)
                    .join(" ");
                this.debugEl.textContent =
                    `S${this.stage} ${this.phase} t=${this.stageTime.toFixed(1)}s crew=${this.crew} ` +
                    `dist=${Math.round(this.distance)}/${Math.round(this.stageGoal)} ents=${this.entities.length} ` +
                    `wpn=${this.weaponTier} rbt=${this.robots}\nspawns: ${sp || "-"}`;
            }
        }
    }

    /** 武器バー: 所持武器だけ表示し、選択中をハイライト。ロケキャノンは充填クールダウンを表示。 */
    private updateWeaponBar(): void {
        for (let id = 0; id < this.weaponSlots.length; id++) {
            const slot = this.weaponSlots[id];
            const owned = this.ownedWeapons.has(id);
            slot.style.display = owned ? "flex" : "none";
            if (!owned) continue;
            const sel = id === this.weaponTier;
            slot.style.borderColor = sel ? "#ffd75e" : "#33404f";
            slot.style.background = sel ? "rgba(40,60,90,0.85)" : "rgba(8,18,30,0.7)";
            slot.style.filter = sel ? "brightness(1.25)" : "brightness(0.8)";
            // ロケキャノン: 充填中はクールダウン残量を上に小さく出す。
            if (id === ROCKET_WEAPON && this.weaponTier === ROCKET_WEAPON && this.rocketCdT > 0) {
                slot.textContent = WEAPON_DEFS[id].icon + Math.ceil(this.rocketCdT);
                slot.style.fontSize = "18px";
            } else if (id === HOMING_WEAPON && this.weaponTier === HOMING_WEAPON && this.homingCdT > 0) {
                slot.textContent = WEAPON_DEFS[id].icon + Math.ceil(this.homingCdT);
                slot.style.fontSize = "18px";
            } else if (id === BOOMERANG_WEAPON && this.weaponTier === BOOMERANG_WEAPON && this.boomerang) {
                // 飛行中は「投げ直せない」サインに … を付けて薄暗く
                slot.textContent = WEAPON_DEFS[id].icon + "…";
                slot.style.fontSize = "18px";
            } else {
                slot.textContent = WEAPON_DEFS[id].icon;
                slot.style.fontSize = "26px";
            }
        }
    }

    private showBanner(text: string, sub = ""): void {
        this.banner.innerHTML = sub ? `${text}<div style="font-size:18px;color:#7bd6ff;margin-top:6px;">${sub}</div>` : text;
        this.banner.style.opacity = "1";
        setTimeout(() => {
            if (!this.disposed) this.banner.style.opacity = "0";
        }, 1100);
    }

    // ---------- 入力 ----------
    private readonly onDown = (e: PointerEvent) => {
        this.pointerDown = true;
        this.steer(e.clientX);
    };
    private readonly onMove = (e: PointerEvent) => {
        if (this.pointerDown) this.steer(e.clientX);
    };
    private readonly onUp = () => {
        this.pointerDown = false;
    };
    private readonly onKeyDown = (e: KeyboardEvent) => {
        const k = e.key.toLowerCase();
        if (k === "a" || k === "arrowleft") this.keyLeft = true;
        else if (k === "d" || k === "arrowright") this.keyRight = true;
        else if (k === "f2") this.debugOn = !this.debugOn; // バランス検証用デバッグ表示
        else if (k === " " || e.code === "Space") { e.preventDefault(); this.cannonButton(); } // 波動砲 召喚/発射
        else if (k === "1" || k === "2" || k === "3" || k === "4" || k === "5" || k === "6") this.selectWeapon(Number(k) - 1); // 武器切替 (1〜6 → id 0〜5)
    };
    private readonly onKeyUp = (e: KeyboardEvent) => {
        const k = e.key.toLowerCase();
        if (k === "a" || k === "arrowleft") this.keyLeft = false;
        else if (k === "d" || k === "arrowright") this.keyRight = false;
    };
    private readonly onResize = () => {
        const w = this.host.clientWidth || 360;
        const h = this.host.clientHeight || 640;
        this.renderer.setSize(w, h);
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
    };
    /**
     * 指定方向 (dir = -1 左 / +1 右) へ許される targetX の絶対値上限を返す。
     * 通常はレーン境界 (LANE-0.6)。ただし隠し裂け目が同じ側で手の届く z 窓に来ている間だけ、
     * 柵を突き抜けて押し込めるよう上限を拡張する (= 敢えてのノークリップ入口)。
     */
    private steerBoundFor(dir: number): number {
        const g = this.secretGap;
        if (g && g.side === Math.sign(dir) && g.z > CROWD_Z - 2 && g.z < CROWD_Z + 10) return LANE + 3.0;
        return LANE - 0.6;
    }

    private steer(clientX: number): void {
        if (this.timeStop > 0) return; // 時間停止中は操作不能
        const rect = this.renderer.domElement.getBoundingClientRect();
        let rel = (clientX - rect.left) / rect.width; // 0..1
        if (this.invertT > 0) rel = 1 - rel; // C 操作反転ゾーン中はポインタ操作も鏡像化
        const n = rel * 2 - 1; // -1..1
        // 裂け目が隣接する側だけ柵の外まで押し込めるよう、向いている側の拡張上限でスケールする。
        this.targetX = n * this.steerBoundFor(Math.sign(n) || 1);
    }

    start(): void {
        const dom = this.renderer.domElement;
        dom.addEventListener("pointerdown", this.onDown);
        dom.addEventListener("pointermove", this.onMove);
        globalThis.addEventListener("pointerup", this.onUp);
        globalThis.addEventListener("keydown", this.onKeyDown);
        globalThis.addEventListener("keyup", this.onKeyUp);
        globalThis.addEventListener("resize", this.onResize);
        // 起動クリック (ユーザー操作) の直後なので AudioContext を resume して BGM 開始。
        this.sfx.resume();
        this.sfx.setAmbientUrl(levelAmbientUrl); // Level! 用アンビエント
        this.sfx.setMood("run");
        this.sfx.startMusic();
        this.loop();
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        this.sfx.dispose();
        cancelAnimationFrame(this.raf);
        const dom = this.renderer.domElement;
        dom.removeEventListener("pointerdown", this.onDown);
        dom.removeEventListener("pointermove", this.onMove);
        globalThis.removeEventListener("pointerup", this.onUp);
        globalThis.removeEventListener("keydown", this.onKeyDown);
        globalThis.removeEventListener("keyup", this.onKeyUp);
        globalThis.removeEventListener("resize", this.onResize);
        this.closeShop();
        this.clearSecretGap(); // 隠し裂け目の THREE 資産を解放
        this.clearTimeStop(); // canvas の filter を戻す
        this.smilerExitDark(); // 照明スナップショットを戻す
        this.clearEnemyMissiles(); // 敵ミサイルの THREE 資産を解放
        this.clearZoneModifiers(); // C/D のビュー反転を戻す
        if (this.armorMesh) { this.scene.remove(this.armorMesh); this.armorMesh.geometry.dispose(); this.armorMesh = null; }
        if (this.partyCrewMesh) { this.scene.remove(this.partyCrewMesh); this.partyCrewMesh.geometry.dispose(); this.partyCrewMesh = null; } // マテリアルは template 共有なので dispose しない
        this.armorMat.dispose();
        this.renderer.domElement.style.filter = ""; // パーティゴア/時間停止のモノクロを念のため解除
        // C/D ティント板はカメラ子で scene.traverse が届かないので明示破棄
        if (this.invertTint) {
            this.invertTint.geometry.dispose();
            (this.invertTint.material as THREE.Material).dispose();
            this.camera.remove(this.invertTint);
            this.invertTint = null;
        }
        this.clearCannon();
        this.clearTurret();
        this.clearDrones();
        if (this.boomerang) { this.disposeObj(this.boomerang.mesh); this.boomerang = null; }
        for (const m of this.missiles) this.disposeObj(m.mesh);
        this.missiles.length = 0;
        this.hideEventOverlay();
        this.scene.traverse((o) => {
            const m = o as THREE.Mesh;
            if (m.geometry) m.geometry.dispose();
        });
        // パーティクル資産 (共有テクスチャ + プールの SpriteMaterial) を解放
        this.glowTex?.dispose();
        this.ringTex?.dispose();
        this.softTex?.dispose();
        this.streakTex?.dispose();
        this.gatePanelTex?.dispose();
        this.gateArrowTex?.dispose();
        for (const p of this.parts) (p.sp.material as THREE.SpriteMaterial).dispose();
        // バイオーム資産。pierTex/fenceTex は laneTexes/fenceTexes のエイリアスなので二重破棄しない。
        for (const t of this.laneTexes) t.dispose();
        for (const t of this.fenceTexes) t.dispose();
        this.hospitalTex?.dispose();
        for (const t of this.backdropTexes) t.dispose();
        this.grassMat?.dispose();
        this.laneMat?.dispose();
        this.fenceMat?.dispose();
        this.backdropMat?.dispose();
        this.propMat?.dispose();
        // scene.traverse は現在 bound のジオメトリだけを破棄する。未 bound のバイオーム変種
        // (各プールの非アクティブ 3 ジオメトリ) を明示的に破棄する。
        for (const pool of this.propPools) for (const g of pool.geoms) g.dispose();
        // 連続素体クルー: InstancedMesh2 の GPU 資産 (matrices/colors/bone テクスチャ等)・
        // 素体ジオメトリ・スキニング用マテリアル・スケルトンを破棄。mixer は uncache で解放。
        this.crowdMesh?.dispose();
        this.crewBodyGeo?.dispose();
        this.crewSkinMat?.dispose();
        this.giantMat?.dispose();
        this.crewSkeleton?.dispose();
        if (this.crewMixer) this.crewMixer.uncacheRoot(this.crewMixerRoot);
        (this.scene.environment as THREE.Texture | null)?.dispose(); // PMREM 環境マップ
        this.renderer.dispose();
    }

    private exit(): void {
        this.onExit();
    }

    // ---------- メインループ ----------
    private loop = (): void => {
        if (this.disposed) return;
        this.raf = requestAnimationFrame(this.loop);
        const dt = Math.min(this.clock.getDelta(), 0.05);
        this.update(dt);
        this.renderer.render(this.scene, this.camera);
    };

    private update(dt: number): void {
        // 前フレームのシェイクオフセットを取り除き、カメラを論理位置へ戻す (蓄積=視点が下がる不具合の防止)。
        this.camera.position.x -= this.shakeOffX; this.shakeOffX = 0;
        this.camera.position.y -= this.shakeOffY; this.shakeOffY = 0;
        if (this.phase === "running") this.updateRunning(dt);
        else if (this.phase === "battle") this.updateBattle(dt);
        else if (this.phase === "clear") {
            this.clearTimer -= dt;
            if (this.clearTimer <= 0) this.openShop();
        }

        // C/D ゾーンモディファイアの減衰 (反転=操作鏡像 / 重力=ビュー反転)
        if (this.invertT > 0) this.invertT = Math.max(0, this.invertT - dt);
        if (this.gravT > 0) this.gravT = Math.max(0, this.gravT - dt);

        // キーボード操作 (A/D・矢印) で目標位置を動かす。時間停止中は操作不能 (位置ロック)。
        const slow = this.cannonHeld || this.cannonFiring > 0 ? 0.45 : 1; // 波動砲を召喚/発射中は横移動が鈍る
        if ((this.keyLeft || this.keyRight) && this.timeStop <= 0) {
            let dir = (this.keyRight ? 1 : 0) - (this.keyLeft ? 1 : 0);
            if (this.invertT > 0) dir = -dir; // C 操作反転ゾーン中は左右を反転
            this.targetX += dir * (LANE * 1.7) * slow * dt;
            // 通常はレーン境界でクランプ。裂け目が隣接する側だけ柵の外へ押し込める (ノークリップ入口)。
            this.targetX = Math.max(-this.steerBoundFor(-1), Math.min(this.steerBoundFor(1), this.targetX));
        }
        // 群衆の横追従＋カメラ追従 (砲台保持中はモッサリ追従)
        this.centroidX += (this.targetX - this.centroidX) * Math.min(1, dt * 10 * slow);
        this.camera.position.x += (this.centroidX * 0.3 - this.camera.position.x) * Math.min(1, dt * 4);
        this.camera.lookAt(this.centroidX * 0.4, 1.0, 16);
        // D 重力反転: ロール量を目標へイーズし、lookAt の後にカメラを 180° ロールしてビューを上下反転。
        this.gravRoll += ((this.gravT > 0 ? 1 : 0) - this.gravRoll) * Math.min(1, dt * 3);
        // lookAt の姿勢を壊さないよう、ロールは「相対回転」で view 軸に重ねる (絶対 rotation.z 代入は
        // +z を向くカメラの正しい z 成分を 0 に潰して通常時まで反転させる罠なので使わない)。
        if (this.gravRoll > 0.001) this.camera.rotateZ(this.gravRoll * Math.PI);
        // Level 0 ターン壁の通過スイング: lookAt の後にヨーを重ねて「曲がった」視界に (gravRoll と同じ相対回転)。
        if (this.turnSwingT > 0) {
            this.turnSwingT = Math.max(0, this.turnSwingT - dt);
            const k = this.turnSwingT / TURN_SWING_TIME; // 1→0
            this.camera.rotateY(Math.sin(k * Math.PI) * TURN_SWING_MAX * this.turnSwingDir); // 0→ピーク→0
        }
        // C/D 全画面ティント: 反転=紫 / 重力=水色 を残り時間でフェード (どちらも無ければ非表示)。
        this.updateZoneTint();

        // ワープ通過の視覚ダッシュ: 背景スクロールだけ一時的に加速 (前進) / 逆流 (逆向きワープ)。
        // 距離・スポーンには一切影響させず、見た目の「ぐっと進んだ / 引き戻された」感だけを足す。
        if (this.warpDashT > 0) this.warpDashT = Math.max(0, this.warpDashT - dt);
        const dash = this.warpDashT > 0
            ? (this.warpDashDir > 0 ? 1 + 2.2 * (this.warpDashT / 0.9) : 1 - 1.6 * (this.warpDashT / 0.9))
            : 1;
        const scrollSpeed = this.speed * dash; // スクロール/側方プロップだけに使う (distance には使わない)
        this.pierTex.offset.y = (this.pierTex.offset.y - scrollSpeed * dt * 0.05) % 1;
        this.fenceTex.offset.x = (this.fenceTex.offset.x - scrollSpeed * dt * 0.05) % 1;
        this.updateScenery(dt, dash); // 側方プロップを前進＋リサイクル (走行中のみ・ダッシュ倍率込み)
        this.updateWeather(dt); // バイオーム別の降り物 (雪/落ち葉/砂塵/残り火/蛍火/きらめき)
        this.updateShieldBar(dt); // 前面バリアの表示・追従・残量演出
        this.updateBackShieldBar(dt); // 背面バリアの表示・追従・残量演出
        this.updateCeilShieldBar(dt); // 天井キャノピーの表示・追従・残量演出
        this.updateCannon(dt); // 波動砲の追従・脈動・発射ビーム
        this.layoutCrowd(dt);
        this.updateBullets(dt);
        this.updateBoomerang(dt); // ブーメランは走行/ボス両フェーズで毎フレーム飛ばす
        this.updateRobotMissiles(dt); // 援軍メカのホーミングミサイル (走行/ボス両フェーズ)
        this.updateEnemyMissiles(dt); // 敵ミサイル (mech ボス / シールドロボ) の飛来・着弾
        this.updateTurret(dt); // 360°タレットの追従・索敵・射撃
        this.updateDrones(dt); // 周回ガンドローンの旋回・索敵・射撃
        this.updateArrows(dt);
        this.updateFx(dt);
        this.updateParticles(dt); // NEW — updateFx の後

        // 画面シェイク (カメラ微振動)。一時オフセットとして足し、次フレ先頭で必ず引き戻す (蓄積させない)。
        this.shake *= 0.86;
        if (this.shake > 0.01) {
            this.shakeOffX = (Math.random() - 0.5) * this.shake;
            this.shakeOffY = (Math.random() - 0.5) * this.shake;
            this.camera.position.x += this.shakeOffX;
            this.camera.position.y += this.shakeOffY;
        }
        this.trailTick++; // 末尾 (updateHud の前)
        this.updateHud();
    }

    private updateRunning(dt: number): void {
        this.stageTime += dt;
        // コンボの減衰 (一定時間倒さないと途切れる)
        if (this.combo > 0) {
            this.comboTimer -= dt;
            if (this.comboTimer <= 0) this.combo = 0;
        }
        // G11 火力テンプレの一時バフ減衰 (切れたら通常火力へ戻る)
        if (this.fireBuffT > 0) this.fireBuffT = Math.max(0, this.fireBuffT - dt);

        // クリーン走行回復: 被弾せず走り続けると失ったクルーを crewPeak 上限まで少しずつ取り戻す。
        // hit() で noHitT=0 にリセットされるので、無傷を維持できている時だけ効く。
        this.noHitT += dt;
        if (this.noHitT > REGEN_DELAY && this.crew < this.crewPeak) {
            this.regenCarry += (REGEN_BASE + this.crew * REGEN_FRAC) * dt;
            const add = Math.floor(this.regenCarry);
            if (add > 0) {
                this.regenCarry -= add;
                this.changeCrew(Math.min(add, this.crewPeak - this.crew));
            }
        }

        // 装備アーマーの固有効果 (闇=クルー毎秒消費 / 炎=前方近接の敵を毎秒焼く)
        const adef = this.equippedArmorDef;
        if (adef.drainFrac > 0 && this.crew > 2) {
            this.armorDrainCarry += this.crew * adef.drainFrac * dt;
            const d = Math.floor(this.armorDrainCarry);
            if (d > 0) {
                this.armorDrainCarry -= d;
                this.changeCrew(-Math.min(d, this.crew - 2));
                // クルーが「溶けていく」演出: ランダムなクルーの足元から紫の雫が滴り落ちる
                const ev = Math.min(d, 4, this.members.length);
                for (let q = 0; q < ev; q++) {
                    const m = this.members[(Math.floor(this.clock.elapsedTime * 50) + q * 5) % this.members.length];
                    this.p_emit(new THREE.Vector3(this.centroidX + m.rx, 0.85, CROWD_Z + m.rz), { tex: "soft", col0: 0x8b3dff, col1: 0x16062e, size0: 0.32, size1: 0.58, life: 0.6, vy: -1.6, grav: -2.4, add: true });
                }
            }
        }
        if (adef.burn > 0) {
            this.armorBurnT -= dt;
            if (this.armorBurnT <= 0) {
                this.armorBurnT = 0.25;
                const dmg = this.firepower * FIRE_FACTOR * 0.25 * adef.burn;
                for (const e of this.entities) {
                    if (e.type !== "mob" && e.type !== "archer" && e.type !== "bruiser" && e.type !== "rocket") continue;
                    if (e.resolved || e.hp <= 0) continue;
                    if (e.z > CROWD_Z - 1 && e.z < CROWD_Z + 4 && Math.abs(e.wx - this.centroidX) < 2.6) {
                        this.damageEnemy(e, dmg);
                        this.p_emit(new THREE.Vector3(e.wx, 0.8, e.z), { tex: "glow", col0: 0xff7a1e, col1: 0xffd23b, size0: 0.2, size1: 0.5, life: 0.3, vy: 1.4, add: true });
                    }
                }
            }
        }

        let reachedGoal = false;
        if (this.eventActive) {
            // イベント中: 通常の前進/スポーン/ゴール判定を止め、専用の襲撃ループを回す。
            this.updateBackroomsEvent(dt);
        } else {
            this.distance += this.speed * dt;
            // 速度はステージ依存で、終盤 (ステージ9以降) だけ徐々に加速。
            this.speed = BASE_SPEED * (1 + Math.max(0, this.stage - 8) * 0.06);
            // ゴール到達後は新規スポーンを止める。残りの障害物が捌けたらボス戦へ。
            reachedGoal = this.distance > this.stageGoal;
            this.spawnTimer -= dt;
            if (!reachedGoal && this.spawnTimer <= 0) {
                // Level 0 は曲がる迷路が主役なので通常ハザードを薄める。
                this.spawnTimer = (this.biomeIdx === LEVEL0_BIOME ? 1.6 : 0.8) - this.diff * 0.4;
                this.spawnRandom();
            }
            // Level 0 (黄色ロビー): 迷路の行き止まり壁で定期的に横へ曲がらされる。
            if (this.biomeIdx === LEVEL0_BIOME && !reachedGoal) {
                this.turnWallT -= dt;
                if (this.turnWallT <= 0) {
                    this.turnWallT = 4.0 + Math.random() * 2.5;
                    this.spawnTurnWall(Math.random() < 0.5 ? -1 : 1);
                }
            }
            if (reachedGoal && !this.boss && this.entities.length === 0) {
                this.startBattle();
                return;
            }
            this.maybeTriggerEvent(); // 稀に緊急イベントが発生
            this.maybeSpawnSecretGap(); // 稀に「敢えてノークリップ」用の隠し裂け目を出す
            this.updateSecretGap(dt); // 裂け目を手前へ流し、突き抜けたら自発入場 (内部で startBackroomsEvent)
            if (this.eventActive) return; // 上で自発入場した場合はこのフレームの通常処理を抜ける
        }
        this.autoFire(dt);

        for (const e of this.entities) {
            let sp = e.type === "bruiser" || e.type === "rocket" ? this.speed * e.speedMul : this.speed;
            if (e.type === "hazard" && e.sky) sp = 0; // 頭上隕石は前進せずその場で落下
            // 突進ドラゴン: 溜め中は奥でホバリング (前進せず) して翼を羽ばたく → 0 で一気に突進。
            if (e.type === "bruiser" && e.variant === "dragon" && e.dragonPhase) {
                const t2 = this.clock.elapsedTime;
                if (e.wings) { // 翼は常時羽ばたく
                    const f = Math.sin(t2 * 14) * 0.7;
                    e.wings[0].rotation.z = -0.4 - f;
                    e.wings[1].rotation.z = 0.4 + f;
                }
                if (e.dragonPhase === "approach") {
                    // 接近: 高度を保ってそこそこの速さで近づき、射程 (z≈34) で溜めに入る。
                    sp = this.speed * 1.3;
                    if (e.lift) e.lift.position.y = 1.4 + Math.sin(t2 * 3) * 0.25;
                    if (e.glow) e.glow.material.opacity = 0.25 + 0.15 * Math.abs(Math.sin(t2 * 5));
                    if (e.z <= 34) { e.dragonPhase = "hold"; e.telegraph = 0.8; }
                } else if (e.dragonPhase === "hold") {
                    // 溜め: その場でホバリングして赤く明滅 (突進レーンの予告)。0 で突進開始。
                    sp = 0;
                    e.telegraph = (e.telegraph ?? 0) - dt;
                    if (e.lift) e.lift.position.y = 1.4 + Math.sin(t2 * 8) * 0.18;
                    if (e.glow) e.glow.material.opacity = 0.4 + 0.5 * Math.abs(Math.sin(t2 * 9));
                    e.obj.rotation.x = -0.2; // 反って溜める
                    if ((e.telegraph ?? 0) <= 0) {
                        e.dragonPhase = "charge";
                        this.impactBurst(e.obj.position.clone().setY(1.4), "dragon", 1.0);
                        this.sfx.play("bossatk"); // 咆哮
                        this.shake = Math.max(this.shake, 0.3);
                    }
                } else {
                    // 突進: 群衆へ一直線に高速ダッシュ (床すれすれを突っ込む)。当たれば直撃 (resolveEntity)。
                    sp = this.speed * 6.0;
                    if (e.lift) e.lift.position.y = 0.7;
                    if (e.glow) e.glow.material.opacity = 0.6;
                    e.obj.rotation.x = 0.25; // 前のめりに突っ込む
                    this.trailEmit(e.obj.position.clone().setY(0.6), 0xff6a3a);
                }
            }
            // 飛来する強襲兵: 高度を保って上空を弧で越え → 背後に降りて溜め → 背後から一閃して飛び去る。
            if (e.type === "bruiser" && e.variant === "flyer" && e.flyLift) {
                const t2 = this.clock.elapsedTime;
                // 翼を羽ばたかせる (深めに打って「飛行中」を強調)
                if (e.wings) {
                    const f = Math.sin(t2 * 16) * 0.95;
                    e.wings[0].rotation.z = -0.4 - f;
                    e.wings[1].rotation.z = 0.4 + f;
                }
                // 真下の影: 高度が高いほど大きく薄く (飛んでいる手掛かり)。strike で急降下すると影が締まる。
                if (e.flyShadow) {
                    const alt = Math.max(0, e.flyLift.position.y);
                    const k = clamp01((alt - 1.0) / 4.0); // 1→5 を 0→1 に
                    e.flyShadow.scale.setScalar(1.0 + k * 1.4); // 高いほど大きい影
                    (e.flyShadow.material as THREE.MeshBasicMaterial).opacity = 0.36 - k * 0.22; // 高いほど薄い
                }
                // レーンを群衆中心へ寄せながら飛ぶ (背後から狙う照準)。strike 中は固定。
                if (e.flyPhase !== "strike" && !e.flyDone) {
                    const dx = this.centroidX - e.wx;
                    if (Math.abs(dx) > 0.02) e.wx += Math.sign(dx) * Math.min(Math.abs(dx), 2.4 * dt);
                    // 旋回方向へ機体をバンク (羽ばたきと併せて飛行感を出す)
                    e.flyLift.rotation.z = Math.max(-0.5, Math.min(0.5, -dx * 0.5)) + Math.sin(t2 * 16) * 0.05;
                    e.flyLift.rotation.x = 0.12 + Math.sin(t2 * 4) * 0.05; // 軽い前傾ボビング
                }
                if (e.flyPhase === "approach") {
                    // 接近: 高度 3.5 でボビングしながら入ってくる (撃ち落とせる)。
                    e.flyLift.position.y = 3.5 + Math.sin(t2 * 4) * 0.3;
                    if (e.glow) e.glow.material.opacity = 0.3 + 0.2 * Math.abs(Math.sin(t2 * 5));
                    if (e.z <= CROWD_Z + 6) e.flyPhase = "over";
                } else if (e.flyPhase === "over") {
                    // 通過: 群衆の真上を高々度 (y 5) で越える。前面のクルーには当てにくい。
                    e.flyLift.position.y = 4.6 + Math.sin(t2 * 4) * 0.2;
                    if (e.z <= CROWD_Z - 2) {
                        e.flyPhase = "behind";
                        e.flyStrikeT = 0.42; // 背後で身構える溜め (短め=黒い壁として居座らせない)
                    }
                } else if (e.flyPhase === "behind") {
                    // 背後で身構えて溜め: 床に予兆グロー + 短い間。クルーに向き直る。
                    // 高めの位置 (y≈2.8) に留めてカメラ正面を覆わない (低く降りると黒い壁になって視界を塞ぐため)。
                    sp = 0; // 溜め中は背後で居座る
                    e.flyLift.position.y += (2.8 - e.flyLift.position.y) * Math.min(1, dt * 6);
                    e.obj.rotation.y = Math.PI; // クルーへ向き直る
                    if (e.glow) e.glow.material.opacity = 0.5 + 0.5 * Math.abs(Math.sin(t2 * 12)); // 警告の明滅
                    this.trailEmit(e.obj.position.clone().setY(0.2), 0xff5a5a); // 背後の床に予兆
                    e.flyStrikeT = (e.flyStrikeT ?? 0) - dt;
                    if (e.flyStrikeT <= 0) e.flyPhase = "strike";
                } else if (e.flyPhase === "strike" && !e.flyDone) {
                    // 一閃: 背後から群衆へ突撃 (fromBehind=true)。バックシールドが無ければ直撃。
                    sp = 0;
                    this.impactBurst(e.obj.position.clone().setY(1.2), "dragon", 1.0);
                    this.collideCrowd(this.centroidX, e.halfW, false, false, true); // 背後から強襲
                    this.shake = Math.max(this.shake, 0.3);
                    e.flyDone = true;
                    e.resolved = true; // 通常の前面接触判定は走らせない
                }
                // 突撃済み: 上昇しながら背後へ飛び去る (撤去待ち)。
                if (e.flyDone) {
                    e.flyLift.position.y += dt * 3;
                    sp = 3.5; // z をさらに後方へ進めて画面外で撤去
                }
            }
            if (e.type === "bruiser" && e.variant === "shieldbot" && e.lift) {
                const t2 = this.clock.elapsedTime;
                // 退避中でなければ横にプレイヤーへ寄せ、浮遊ボビング。
                const dx = this.centroidX - e.wx;
                if (!e.botEscaping) {
                    if (Math.abs(dx) > 0.02) e.wx += Math.sign(dx) * Math.min(Math.abs(dx), 1.6 * dt);
                    e.lift.position.y = 2.0 + Math.sin(t2 * 3) * 0.18;
                    e.obj.rotation.y = Math.max(-0.4, Math.min(0.4, dx * 0.3));
                }
                // ジェット炎 (下向き)
                if ((this.trailTick & 1) === 0) {
                    this.p_emit(new THREE.Vector3(e.wx + (Math.random() - 0.5) * 0.8, e.lift.position.y - 0.7, e.obj.position.z + 0.2),
                        { tex: "soft", vy: -2.5 - Math.random() * 1.5, grav: 5, drag: 2, size0: 0.34, size1: 0.05, col0: 0x9fe8ff, col1: 0xff8a3b, life: 0.25 });
                }
                // シールド表示: 残量で不透明度、割れたら消す。
                if (e.shieldMesh) {
                    const sm = e.shieldMesh.material as THREE.MeshBasicMaterial;
                    sm.opacity = e.shieldBroken ? 0 : 0.16 + 0.22 * ((e.shieldHp ?? 0) / (e.shieldMax ?? 1)) + 0.06 * Math.abs(Math.sin(t2 * 6));
                }
                // ミサイル発射 (退避中は撃たない)
                if (!e.botEscaping && e.z > CROWD_Z + 6 && e.z < SPAWN_Z * 0.85) {
                    e.botFireT = (e.botFireT ?? 1.2) - dt;
                    if ((e.botFireT ?? 0) <= 0) {
                        e.botFireT = 2.2 + Math.random();
                        const from = new THREE.Vector3(e.wx, e.lift.position.y, e.obj.position.z);
                        this.spawnEnemyMissile(from, this.centroidX + (Math.random() - 0.5) * 2.0, { R: 1.2, overhead: false, col: 0x9fe8ff, t: 1.0 });
                    }
                }
                // 接近しきったら貫通せずジェットで上空へ退避 (早すぎないよう CROWD_Z+4.5 まで引きつけてから)
                if (!e.botEscaping && e.z <= CROWD_Z + 4.5) {
                    e.botEscaping = true; e.resolved = true; // 以後は通常接触判定を走らせない
                    this.spark(new THREE.Vector3(e.wx, e.lift.position.y, e.obj.position.z), 0x9fe8ff);
                    this.popWorld("撤退！", e.wx, "#9fe8ff");
                }
                if (e.botEscaping) {
                    e.lift.position.y += dt * 5; // 急上昇しながら前方へ抜けて画面外へ
                    sp = Math.max(sp, 4);
                    if (e.shieldMesh) (e.shieldMesh.material as THREE.MeshBasicMaterial).opacity = 0;
                }
            }
            if (e.type === "bruiser" && e.variant === "orbiter" && e.lift) {
                const t2 = this.clock.elapsedTime;
                e.lift.position.y = 3.0 + Math.sin(t2 * 2) * 0.2; // ホバリング
                if (!e.orbiting) {
                    // 接近: 高度を保って前進。旋回半径まで来たら周回開始 (今の相対位置から角度を継ぐ)。
                    if (e.z <= CROWD_Z + (e.orbitR ?? 6.5)) {
                        e.orbiting = true;
                        e.orbitA = Math.atan2(e.z - CROWD_Z, e.wx - this.centroidX);
                    }
                } else {
                    sp = 0; // 位置は周回計算で直接制御
                    const R = e.orbitR ?? 6.5;
                    if ((e.orbitT ?? 0) > 0) {
                        e.orbitT = (e.orbitT ?? 9) - dt;
                        e.orbitA = (e.orbitA ?? 0) + dt * 1.15; // 旋回速度
                        e.wx = this.centroidX + Math.cos(e.orbitA) * R;
                        e.z = CROWD_Z + Math.sin(e.orbitA) * R;
                        e.obj.rotation.y = Math.atan2(this.centroidX - e.wx, CROWD_Z - e.z); // 群衆を睨む
                        // 射撃: 自分の位置からクルーへ (背後/横からも撃てる 360°の脅威)。床予告で回避可。
                        e.orbFireT = (e.orbFireT ?? 1.2) - dt;
                        if ((e.orbFireT ?? 0) <= 0) {
                            e.orbFireT = 1.5 + Math.random() * 0.6;
                            const from = new THREE.Vector3(e.wx, e.lift.position.y, e.z);
                            this.spawnEnemyMissile(from, this.centroidX + (Math.random() - 0.5) * 1.6, { R: 1.2, overhead: false, col: 0xc06aff, t: 0.9 });
                        }
                    } else {
                        // 寿命: 上昇して飛び去る (少し上がってから撤去)。
                        e.resolved = true;
                        e.orbitT = (e.orbitT ?? 0) - dt;
                        e.lift.position.y += dt * 5;
                        if ((e.orbitT ?? 0) <= -0.8) e.z = -100; // 撤去ループに回収させる
                    }
                }
            }
            if (e.type === "bruiser" && e.variant === "bacteria") {
                if (e.mixer) {
                    // FBX 内蔵アニメ (実モデル版) を進める
                    e.mixer.update(dt);
                } else if (e.writhe) {
                    // うごめく肉塊/触手 (フォールバック手続き版・累積しない bounded アニメ)
                    const t2 = this.clock.elapsedTime;
                    for (const w of e.writhe) {
                        const ph = (w.userData.ph as number) ?? 0;
                        w.scale.setScalar(1 + 0.18 * Math.sin(t2 * 4 + ph));
                        w.rotation.z = 0.3 * Math.sin(t2 * 3 + ph);
                    }
                }
                const small = e.halfW < 0.8;
                const t3 = this.clock.elapsedTime;
                const body = e.obj.children[0];
                const bodyOk = !!body && !(body as THREE.Sprite).isSprite;
                const pouncing = !!(e.pounceT && e.pounceT > 0);
                if (pouncing) sp = 0; // 跳躍中は通常前進/追尾を止め、wx/z を放物線で完全制御する
                // 跳躍していない間だけプレイヤーを追尾して横に詰めながら走り寄る (小型ほど機敏)
                const dx = this.centroidX - e.wx;
                if (!pouncing && !e.pounceExploded) {
                    const chase = (small ? 3.6 : 2.4) * dt;
                    if (Math.abs(dx) > 0.02) e.wx += Math.sign(dx) * Math.min(Math.abs(dx), chase);
                }
                e.obj.rotation.y = Math.max(-0.45, Math.min(0.45, dx * 0.4));
                // --- とびかかり (pounce): プレイヤーの足元へ狙いを定め → 放物線で跳んで「着地点で爆散」する。
                //     着地点は跳躍開始時にロックして床へ予告リングを出す。横に逃げれば回避でき、居残れば直撃。
                //     倒しそこねても素通りせず着地で力尽きる (脱・直進で「跳びかかってくる」怖さを出す)。 ---
                const POUNCE_DUR = 0.85; // 溜め(前半)+跳躍(後半)。読めるよう長め。
                const TELE = 0.32; // 溜め(テレグラフ)が占める割合
                // squash は基準スケール (実モデルは ~0.004 と極小) に相対で掛ける。絶対 1 を入れると巨大化して消える。
                const bs = e.pounceBaseScale ?? 1;
                const PJ = small ? 0.6 : 1; // 跳躍の大きさ係数 (小型は控えめ)
                const LAND_R = small ? 0.9 : 1.5; // 着地爆心の半径 (この内側に居たら直撃)
                if (pouncing) {
                    e.pounceT! -= dt;
                    const kp = clamp01(1 - e.pounceT! / POUNCE_DUR); // 0..1
                    if (kp < TELE) {
                        // 溜め: 大きくしゃがんで潰れ、後ろに反る。着地点リングが脈動して予告。
                        const c = kp / TELE;
                        if (bodyOk) { body!.position.y = -0.34 * c * PJ; body!.scale.set(bs * (1 + 0.30 * c), bs * (1 - 0.36 * c), bs * (1 + 0.30 * c)); }
                        e.obj.rotation.x = 0.25 * c;
                        if (e.pounceTell) {
                            (e.pounceTell.material as THREE.MeshBasicMaterial).opacity = 0.25 + 0.35 * Math.abs(Math.sin(t3 * 14));
                            e.pounceTell.scale.setScalar(1 + 0.12 * Math.sin(t3 * 14));
                        }
                    } else {
                        // 跳躍: 着地点 (toX/toZ) へ wx/z を補間しつつ body を弧で持ち上げ、深く前のめりに襲いかかる。
                        const j = (kp - TELE) / (1 - TELE); // 0..1
                        const arc = Math.sin(j * Math.PI);
                        const fx = e.pounceFromX ?? e.wx, fz = e.pounceFromZ ?? e.z;
                        e.wx = fx + ((e.pounceToX ?? e.wx) - fx) * j;
                        e.z = fz + ((e.pounceToZ ?? e.z) - fz) * j;
                        if (bodyOk) { body!.position.y = arc * 2.6 * PJ; body!.scale.set(bs * (1 - 0.18 * arc), bs * (1 + 0.4 * arc), bs * (1 - 0.18 * arc)); }
                        e.obj.rotation.x = -1.1 * arc;
                        if (e.pounceTell) (e.pounceTell.material as THREE.MeshBasicMaterial).opacity = 0.55;
                    }
                    if (e.pounceT! <= 0 && !e.pounceExploded) {
                        // 着地: 爆散。着地点で範囲ダメージ → 内側に居たクルーは死亡。素通りさせず力尽きて退場。
                        e.pounceExploded = true;
                        if (bodyOk) { body!.position.y = 0; body!.scale.setScalar(bs); }
                        e.obj.rotation.x = 0;
                        const lx = e.pounceToX ?? e.wx;
                        this.boom(new THREE.Vector3(lx, 1.0, CROWD_Z)); // 着地点で爆散
                        this.shake = Math.max(this.shake, small ? 0.3 : 0.5);
                        this.collideCrowd(lx, LAND_R); // 着地点に居たら直撃 (横に逃げていれば無傷)
                        if (e.pounceTell) { this.disposeObj(e.pounceTell); e.pounceTell = undefined; }
                        e.resolved = true; e.z = -100; // kill 報酬は出さず自然退場 (撤去ループが z<-8 で片付ける)
                    }
                } else if (!e.pounceExploded) {
                    // クールダウン → 射程に入ったら跳躍を開始 (着地点をロックして床に予告)。
                    // 近づきすぎたら CD 無視で強制的に跳ぶ → 通常接触の「素通り」へ落ちずに必ず着地爆散させる。
                    e.pounceCd = (e.pounceCd ?? 0.5) - dt;
                    if (e.z < 50 && e.z > CROWD_Z + 1.5 && (e.pounceCd <= 0 || e.z < CROWD_Z + 5)) {
                        e.pounceT = POUNCE_DUR;
                        e.pounceCd = (small ? 0.8 : 1.4) + Math.random();
                        e.pounceFromX = e.wx; e.pounceFromZ = e.z;
                        e.pounceToX = Math.max(-(LANE - 0.4), Math.min(LANE - 0.4, this.centroidX)); // 今のプレイヤー x にロック
                        e.pounceToZ = CROWD_Z; // プレイヤーの列へ着地
                        const tell = new THREE.Mesh(
                            new THREE.RingGeometry(LAND_R * 0.66, LAND_R, 24),
                            new THREE.MeshBasicMaterial({ color: 0xff4d4d, transparent: true, opacity: 0.3, depthWrite: false, side: THREE.DoubleSide }),
                        );
                        tell.rotation.x = -Math.PI / 2;
                        tell.position.set(e.pounceToX, 0.07, CROWD_Z);
                        this.scene.add(tell);
                        e.pounceTell = tell;
                    }
                    // 通常の上下ラーチ (placeEntity が y=0 に戻すので子に乗せる)
                    if (bodyOk) body!.position.y = Math.abs(Math.sin(t3 * 7 + e.z)) * 0.14;
                }
            }
            if (e.type === "bruiser" && e.variant === "partygoer") {
                // --- パーティゴアー (Level Fun): 追わずに自分のレーンで陽気に踊りながら前進し、
                //     前線に着いたらクルーを 1 人ずつ「勧誘」して背後のコンガの列に加える。撃てば取り返せる。 ---
                const t2 = this.clock.elapsedTime;
                const seed = e.danceSeed ?? 0;
                const pbs = e.partyBaseScale ?? 1; // スケール演出は必ずこの基準相対で (絶対1は巨大化バグ)
                const body = e.obj.children[0];
                const bodyOk = !!body && !(body as THREE.Sprite).isSprite;

                // 居座り判定: 前線 (PARTY_HOLD_Z) に着いたら前進を止めて勧誘する。
                // 上限まで連れ去ったら退場フェーズ (z を増やして奥へ去る)。
                const atFront = e.z <= PARTY_HOLD_Z;
                if (e.recruitDone) {
                    sp = 0;
                    e.z += this.speed * 0.6 * dt; // 列を連れてゆっくり奥へ退場
                } else if (atFront) {
                    sp = 0; // 前線に居座る (前進停止)
                }

                // --- ダンス(常時): その場で小さく横スウェイ + 陽気なスピン/ウォブル + base 相対バウンス ---
                // 追尾はしない。スウェイの中心は spawn 時のレーン位置 (partyLaneX) で固定。
                const lane = e.partyLaneX ?? e.wx;
                const sway = Math.sin(t2 * 3.2 + seed) * 0.45; // その場の小さな横揺れ (踊り)
                e.wx = Math.max(-(LANE - 0.5), Math.min(LANE - 0.5, lane + sway));
                e.obj.rotation.y = Math.sin(t2 * 4 + seed) * 0.5; // 陽気にスピン/ウォブル
                if (bodyOk) {
                    e.obj.rotation.z = Math.sin(t2 * 2.6 + seed) * 0.12; // 体を左右に傾ける (沈み対策で控えめ ±0.12)
                    e.obj.rotation.x = 0;
                    const bounce = Math.abs(Math.sin(t2 * 6 + seed)); // 上下バウンスは abs(sin) で必ず ≥0 (地面にめり込ませない)
                    body!.position.y = Math.max(0, bounce * 0.18); // 足元 y=0 を割り込ませない
                    body!.scale.setScalar(pbs * (1 + 0.06 * bounce)); // base 相対で軽く伸縮
                }

                // --- 勧誘(コア): 前線に居座っている間、クールダウンごとにクルーを 1 人連れ去る ---
                // パーティゴアアーマー装備中は逆転: 攻撃されず、向こうが踊りながら味方に加わる。
                if (atFront && !e.recruitDone) {
                    if (this.isPartyArmor) {
                        e.recruitCd = (e.recruitCd ?? PARTY_RECRUIT_CD) - dt;
                        if (e.recruitCd <= 0) {
                            e.recruitCd = 0.6;
                            this.returnRecruited(e); // 連れ去られていた分があれば取り返す
                            this.changeCrew(PARTY_JOIN_CREW); // 仲間が加わる
                            this.popWorld("仲間になった！", e.wx, "#ffe27a");
                            this.spark(new THREE.Vector3(e.wx, 1.2, e.z), 0xff5ec0);
                            e.resolved = true; e.z = -100; // 退場 (撤去ループが片付ける)
                        }
                    } else {
                        e.recruitCd = (e.recruitCd ?? PARTY_RECRUIT_CD) - dt;
                        if (e.recruitCd <= 0 && this.crew > 0) {
                            e.recruitCd = PARTY_RECRUIT_CD;
                            this.recruitOne(e);
                        }
                    }
                }
                // コンガの列を毎フレーム本体の背後に追従させる (少し遅延/うねり)。
                this.updateConga(e, dt);
            }
            if (e.type === "bruiser" && e.variant === "stalker" && e.stalk) {
                // hold まで来たら居座り (前進を止めて攻撃を回す)。それまでは低速で迫る。
                if (e.z <= e.stalk.hold) sp = 0;
                this.updateStalker(e, dt);
            }
            e.z -= sp * dt;
            if (e.type === "hazard") {
                const t = this.clock.elapsedTime;
                const freq = e.sweepFreq ?? 1.8; // legacy saw = 1.8
                if (e.sweepAmp > 0) {
                    e.wx = Math.sin(t * freq + e.sweepPhase) * e.sweepAmp;
                    if (e.swingHead) {
                        // G1 pendulum: rock the head in sync with wx (NOT free-spin)
                        e.swingHead.rotation.z = Math.sin(t * freq + e.sweepPhase) * (e.swingAmpZ ?? 0.9);
                    } else if (e.spin) {
                        e.spin.rotation.z += dt * 14; // legacy saw free-spin
                    }
                    if (e.gapWall) {
                        // G3: keep the hole centered on e.wx (segments are fixed length LANE*2)
                        const gh = e.gapWall.gapHalf, segLen = LANE * 2;
                        e.gapWall.segL.position.x = e.wx - gh - segLen / 2; // inner (right) edge at e.wx-gh
                        e.gapWall.segR.position.x = e.wx + gh + segLen / 2; // inner (left) edge at e.wx+gh
                    }
                }
                if (e.crusher) {
                    // G2: lerp slabs inward as it nears
                    const k = clamp01(1 - e.z / SPAWN_Z);
                    const gap = e.crusher.gap0 + (e.crusher.gap1 - e.crusher.gap0) * k;
                    e.crusher.left.position.x = -(LANE + gap) / 2; // inner edge at -gap
                    e.crusher.right.position.x = (LANE + gap) / 2; // inner edge at +gap
                }
                if (e.arc) {
                    // G9 電流フェンス: 周期で論理 halfW を伸縮 (mesh は scale.x で追従させるだけ・判定は halfW)。
                    const w = e.arc.minHalf + (e.arc.maxHalf - e.arc.minHalf)
                        * (0.5 + 0.5 * Math.sin(t * e.arc.freq + e.arc.phase));
                    e.halfW = w;
                    e.wx = e.arc.side * (LANE - w); // 帯の中心 = 伸びた側 (端から内向きに w 伸ばす)
                    e.arc.bars.scale.x = Math.max(0.05, w * 2); // 単位長 1 を全長 2w に伸ばす
                    const lethal = w > e.arc.minHalf + 0.3; // ほぼ縮みきった瞬間だけ安全
                    const fm = (e.arc.bars.children[0] as THREE.Mesh).material as THREE.MeshBasicMaterial;
                    fm.opacity = lethal ? 0.45 : 0.12; // 縮むと薄れる (通り抜けの合図)
                }
                if (e.pit) {
                    // G10 崩落床: openAt まで来たら蓋が下がって開口。撃ち込んで armorHp を削ると固まる。
                    if (e.z <= e.pit.openAt && !e.pit.open) {
                        // 接近中: 蓋を徐々に下げる (固める前なら開く)
                        const k = clamp01((e.pit.openAt - e.z) / 8);
                        e.pit.lid.position.y = e.pit.armorHp > 0 ? -2.4 * k : 0; // 固めたら蓋は閉じたまま
                    }
                    // 前方に居る間は firepower で足場を固められる (撃って耐久を削る)
                    if (e.pit.armorHp > 0 && e.z > CROWD_Z + 1 && e.z < SPAWN_Z * 0.8) {
                        e.pit.armorHp = Math.max(0, e.pit.armorHp - this.firepower * FIRE_FACTOR * dt);
                        const bar = (e as HazardEnt & { _pitBar?: THREE.Sprite })._pitBar;
                        if (bar) {
                            bar.scale.x = 1.6 * (e.pit.armorHp / e.pit.armorMax);
                            bar.visible = e.pit.armorHp > 0;
                        }
                        if (e.pit.armorHp <= 0) {
                            // 固まった瞬間: 蓋を閉じて安全な足場にする
                            e.pit.lid.position.y = 0;
                            this.spark(e.obj.position.clone().setY(0.5), this.skin.spike);
                            this.award(15, e.wx);
                        }
                    }
                }
                if (e.slam) {
                    // 落下プレス: 奥では高く、近づくほど落ちて着弾直前で地面へ
                    const k = clamp01(1 - e.z / SPAWN_Z);
                    e.slam.position.y = 0.6 + 8 * (1 - k);
                }
                if (e.boulder) {
                    // A 巨大球: 進んだ距離に比例して転がす (見た目) + 予告された緩い横揺れ (論理 wx を動かし当たりを追従)
                    e.boulder.rotation.x -= (sp * dt) / e.halfW; // 接地転がり
                    e.wx = Math.sin(t * 0.9 + (e.wobblePhase ?? 0)) * 1.0; // ゆっくり ±1.0 で蛇行
                    e.boulder.rotation.z = -e.wx * 0.15; // 蛇行方向へ僅かに傾ける (視覚のみ)
                    if (e.z < CROWD_Z + 16) this.shake = Math.max(this.shake, (1 - e.z / 16) * 0.18); // 接近で地響き
                }
                if (e.warp && e.gate) {
                    // B ワープゲート: リングを回し膜を脈動させる (キルなし・通過判定は resolveEntity)
                    e.gate.rotation.z += dt * 1.4;
                    const film = e.obj.children[1] as THREE.Mesh;
                    const fm = film?.material as THREE.MeshBasicMaterial | undefined;
                    if (fm) fm.opacity = 0.14 + 0.1 * Math.abs(Math.sin(t * 4));
                }
                if (e.sky) {
                    // 頭上隕石: その場で落下。fuse 0 で真上から着弾 (前面遮蔽を貫通する overhead)。
                    e.sky.fuse -= dt;
                    const k = clamp01(1 - e.sky.fuse / 1.6);
                    e.sky.block.position.y = 0.5 + 14 * (1 - k * k); // 終盤ほど加速して落ちる
                    if (e.sky.fuse <= 0 && !e.resolved) {
                        e.resolved = true;
                        this.boom(new THREE.Vector3(e.wx, 1.0, CROWD_Z));
                        this.shake = Math.max(this.shake, 0.5);
                        this.collideCrowd(e.wx, e.halfW, false, true); // overhead=true → 遮蔽貫通
                        e.z = -100; // 次の後始末で撤去
                    }
                }
                if (e.eye) {
                    // 竜巻: 輪を旋回させる (上ほど速い渦)
                    for (const c of e.obj.children) c.rotation.z += dt * ((c.userData.spin as number) ?? 1) * 3;
                }
                if (e.pull && e.z > CROWD_Z - 3 && e.z < CROWD_Z + 3) {
                    // G7 desert: steer-bias, no kill
                    const cx = e.wx;
                    if (Math.abs(this.centroidX - cx) < e.halfW) {
                        this.targetX += (cx - this.centroidX) * e.pull * dt;
                        this.targetX = Math.max(-(LANE - 0.6), Math.min(LANE - 0.6, this.targetX));
                    }
                }
                if (e.drift && e.z > CROWD_Z - 3 && e.z < CROWD_Z + 3) {
                    // G8 snow: lateral momentum overshoot
                    const slideDir = Math.sign(this.targetX - this.centroidX) || 0;
                    this.targetX += slideDir * e.drift * dt;
                    this.targetX = Math.max(-(LANE - 0.6), Math.min(LANE - 0.6, this.targetX));
                }
            }
            this.placeEntity(e);
            if (e.type === "item") e.spin.rotation.y += dt * 1.8;
            if (e.type === "gate") {
                const t = this.clock.elapsedTime;
                const gp = this.gatePanelTex;
                gp.offset.y = (gp.offset.y - dt * 0.6) % 1; // shared scroll
                for (const sub of e.obj.children) {
                    const panel = sub.children[0] as THREE.Mesh;
                    const pm = panel.material as THREE.MeshBasicMaterial;
                    const jitter = panel.userData.nerf ? (Math.random() - 0.5) * 0.1 : 0; // nerf feels unstable
                    pm.opacity = 0.5 + 0.12 * Math.sin(t * 3 + sub.position.x) + jitter;
                }
            }
            this.resolveEntity(e, dt);
        }
        for (let i = this.entities.length - 1; i >= 0; i--) {
            const e = this.entities[i];
            const dead =
                (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket") && e.hp <= 0;
            // ゴール後はまだ遠い障害物を片付けてボスへ素早く移行
            if (e.z < -8 || dead || (reachedGoal && e.z > 12)) {
                // パーティゴアー: 撤去前に連れ去ったクルーを取り返す (撃破でも自然退場でも crew を不当に失わせない)。
                if (e.type === "bruiser" && e.variant === "partygoer") this.returnRecruited(e);
                if (dead) {
                    this.spark(e.obj.position, 0xff8f5a);
                    this.award(this.killCoin(e), e.wx, e.elite ? 3 : 1); // 撃破でコイン + コンボ
                    // 撃ち落としたロケット / 倒したギガントロボ・ドラゴンは派手に爆発
                    if (e.type === "rocket" || (e.type === "bruiser" && (e.variant === "giant" || e.variant === "dragon" || e.variant === "stalker"))) {
                        this.boom(e.obj.position.clone().setY(1.0));
                        if (e.type === "bruiser" && e.variant === "stalker") {
                            // 中ボス撃破: 派手な連爆 + リング + 大シェイク
                            this.impactBurst(e.obj.position.clone().setY(1.6), "king", 1.4);
                            this.shockRing(e.obj.position.clone().setY(1.0), 0xff7a3a, 6, 0.5);
                            this.shake = Math.max(this.shake, 0.6);
                            this.popWorld("撃破！", e.wx, "#ffd23b");
                        }
                    }
                }
                // bacteria の着地予告リングは scene 直付けなので個別に破棄 (跳躍中に撃破された場合の取り残し防止)
                if (e.type === "bruiser" && e.pounceTell) { this.disposeObj(e.pounceTell); e.pounceTell = undefined; }
                this.disposeObj(e.obj);
                this.entities.splice(i, 1);
            }
        }
        if (this.crew <= 0) this.defeat();
    }

    private get diff(): number {
        return Math.min(1, this.distance / 200);
    }
    private get stageGoal(): number {
        return BOSS_DIST + (this.stage - 1) * 26;
    }
    private get stageMul(): number {
        return 1 + (this.stage - 1) * 0.4;
    }
    private get firepower(): number {
        // 拳銃=1 / ライフル=2.2 / ミニガン=4 / ロケキャノン=0 (継続火力なし・周期ブラスト)。
        const mult = WEAPON_DEFS[this.weaponTier]?.mult ?? 1;
        // コンボで火力も微増 (最大 ×1.4)。コインほど効かせず、戦闘テンポを少しだけ後押し。
        const comboFp = 1 + Math.min(this.combo, 20) * 0.02;
        const buff = this.fireBuffT > 0 ? FIRE_BUFF_MULT : 1; // G11 火力テンプレの一時バフ
        const robotFp = this.isPartyArmor ? 0 : this.robots * 45; // パーティゴアアーマー中は援軍ロボの火力寄与なし
        return (this.crew * mult + robotFp) * comboFp * buff * this.equippedArmorDef.firePower; // 装備アーマーの火力倍率
    }
    private get lossMult(): number {
        // 走行ハザードもアーマーで軽減 (当たった列の一部が生き残る)。実効アーマー(数値Lv＋装備)で計算。armor=0 なら 1。
        // toKill の下限 1 は collideCrowd 側で保証されるので、当たれば最低 1 人は失う (無敵化しない)。
        return 1 / (1 + this.effectiveArmor * ARMOR_RUN_K);
    }

    // ---------- 群衆描画 ----------
    /**
     * 1 体の毎フレーム行列の唯一の真実 (棒立ち修正の中核)。this.dummy だけを変える・ゼロアロケーション。
     * バウンド同期のスクワッシュ&ストレッチ・横揺れ・捻り・前傾を行列のみに焼く (rx/ry/rz には触れない)。
     */
    private driveDoll(m: Member, t: number, dt: number): void {
        const phase = this.phase;
        const run = phase === "running";
        const clear = phase === "clear";
        // 挙動ごとの基本パラメータ
        const sp = run ? 16 * m.spd : clear ? 6 : 3.4;
        // 上下バウンドは落ち着かせる (旧 0.3 は速すぎ・激しすぎて「沸騰」して見えた)。
        const bobK = run ? 0.12 : clear ? 0.26 : 0.05;
        const styK = run ? 0.12 : clear ? 0.28 : 0.06;
        const swyK = run ? 0.06 : clear ? 0.08 : 0.03;
        // 走行中は体の沈みを脚のケイデンスに同期 (gaitPhase) — 1 サイクルに 2 回の接地で沈む。
        // 位相ばらつき (m.ph) は控えめにして群れが「沸騰」して見えるのを防ぐ。
        const a = run ? this.gaitPhase * 4 * Math.PI + m.ph * 0.3 : t * sp + m.ph;
        const bo = Math.abs(Math.sin(a)); // 0..1 バウンド高
        const bob = bobK * m.amp * bo;
        // バウンドに同期したスクワッシュ&ストレッチ: 上昇時 (bo 高) に伸び、接地付近 (bo 低) で潰れる
        let sy = 1 + styK * m.amp * (bo - 0.45);
        // 着地スクワッシュの衝撃 (減衰)
        if (m.land > 0) {
            m.land = Math.max(0, m.land - dt * 3.2);
            const L = m.land / 0.18;
            sy -= 0.35 * L * L;
        }
        const sxz = 1 / Math.sqrt(Math.max(0.4, sy)); // 体積保存
        // 二次運動: 横揺れ + 胴の捻り (捻りは +1.0 位相で遅れる)
        const swF = run ? 5 : clear ? 4 : 1.5;
        const sway = swyK * Math.sin(t * swF + m.swy);
        const twist = (run ? 0.14 : clear ? 0.08 : 0.04) * Math.sin(t * swF + m.swy + 1.0);
        const lean = m.lean + (run ? 0.14 : 0) + (run ? 0.1 : 0) * bo; // 走りは前傾ベース + ストライドで上下
        // D 重力反転中はクルーを天井へ持ち上げる (視覚のみ・当たり判定 m.ry/m.rx は不変)。
        this.dummy.position.set(this.centroidX + m.rx + sway, m.ry + bob + this.gravRoll * CEIL_H, CROWD_Z + m.rz);
        this.dummy.rotation.set(lean, twist * 0.5, twist); // 足元中心に回る (geometry 原点が足元) → 接地は保つ
        this.dummy.scale.set(sxz * m.sc, sy * m.sc, sxz * m.sc); // 個体ごとの身長差でクローン感を消す
        this.dummy.updateMatrix();
    }

    private layoutCrowd(dt: number): void {
        const t = this.clock.elapsedTime;
        const n = this.members.length;
        // パーティゴアアーマー装備でモデルがまだ用意できていない (起動直後は GLTF 未ロード) なら、読めた時点で一度だけ変身させる。
        if (this.isPartyArmor && !this.partyCrewMesh && partygoerModel) this.rebuildArmorMesh();
        const k = Math.min(1, dt * 6); // 隊列スロットへ詰め直す速さ (生き残りが滑らかに寄る)
        // 走りクリップの連続位相を進める。ケイデンスはスピード連動 (速いほど足が速く回る)。
        // battle/clear は穏やかな足踏み。位相は個体ごとに phaseOffset でずらしロックステップを解消。
        const cad = this.timeStop > 0 ? 0 : this.phase === "running" ? 3.0 * (this.speed / BASE_SPEED) : this.phase === "clear" ? 2.4 : 1.0;
        this.gaitPhase = (this.gaitPhase + dt * cad) % 1;
        this.crewArmed = this.weaponMesh != null; // 武器所持中は腕を構えポーズに (bake が参照)
        // バケットごとに 1 回スケルトンを評価しスナップショット (300 体でも K 回の評価で済む)
        const K = CrewRun3D.PHASE_BUCKETS;
        this.bakeCrewBuckets();
        const bones = this.crewBones;
        for (let i = 0; i < n; i++) {
            const m = this.members[i];
            const p = this.pile(i, n);
            m.rx += (p.x - m.rx) * k; // ← 当たり判定の状態 (視覚は書き戻さない)
            m.ry += (p.y - m.ry) * k;
            m.rz += (p.z - m.rz) * k;
            this.driveDoll(m, t, dt); // this.dummy (pos/rot/scale) を書く
            this.crowdMesh.setMatrixAt(i, this.dummy.matrix);
            if (i >= this.crewVisibleCount || !this.crowdMesh.getVisibilityAt(i)) this.crowdMesh.setVisibilityAt(i, true);
            // この個体の位相バケットのボーン姿勢を復元し、スキニング行列を書き込む
            const bIdx = Math.floor((((this.gaitPhase + m.phaseOffset) % 1) + 1) % 1 * K) % K;
            const snap = this.crewBucketBones[bIdx];
            for (let bj = 0; bj < bones.length; bj++) bones[bj].matrixWorld.copy(snap[bj]);
            this.crowdMesh.setBonesAt(i, false); // matrix 再計算せず現在の bone.matrixWorld を採用
            if (this.armorMesh) this.armorMesh.setMatrixAt(i, this.dummy.matrix); // 素体と同じ姿勢で装甲を重ねる (weapon の前に・基準行列のまま)
            if (this.partyCrewMesh) this.partyCrewMesh.setMatrixAt(i, this.dummy.matrix); // パーティゴア変身: 素体の姿勢でモデルを置く
            if (this.weaponMesh) {
                // 構えポーズ (両腕前方) の手の中央前方に銃を置く。geometry は前方 z+・胸高 y0.6 基準。
                // 構えた両手 (肩高・前方) の位置に銃を上げて前へ出す (geo は y0.62/z0.45 基準)。
                this.dummy.position.y += 0.16 * m.sc;
                this.dummy.position.z += 0.18 * m.sc;
                this.dummy.updateMatrix();
                this.weaponMesh.setMatrixAt(i, this.dummy.matrix);
            }
        }
        // 余った可視インスタンスを隠す (差分のみトグル)。実描画数 (count) はライブラリが
        // active+visible から毎フレーム算出するので手動設定しない。
        for (let i = n; i < this.crewVisibleCount; i++) this.crowdMesh.setVisibilityAt(i, false);
        this.crewVisibleCount = n;
        if (this.weaponMesh) {
            this.weaponMesh.count = n;
            this.weaponMesh.instanceMatrix.needsUpdate = true;
        }
        if (this.armorMesh) {
            this.armorMesh.count = n;
            this.armorMesh.instanceMatrix.needsUpdate = true;
        }
        if (this.partyCrewMesh) {
            this.partyCrewMesh.count = n;
            this.partyCrewMesh.instanceMatrix.needsUpdate = true;
        }
        // アーマーのオーラ粒子 (数体からだけ・プール予算を食い潰さないよう少量・装備ごとに表情を変える)
        const adef = this.equippedArmorDef;
        if (adef.aura !== "none" && n > 0) {
            const cnt = Math.min(3, n);
            for (let q = 0; q < cnt; q++) {
                const m = this.members[(Math.floor(t * 30) + q * 7) % n];
                const px = this.centroidX + m.rx, pz = CROWD_Z + m.rz;
                if (adef.aura === "fire") this.p_emit(new THREE.Vector3(px, 0.6 + Math.random() * 0.9, pz), { tex: "glow", col0: 0xffd23b, col1: 0xff4a10, size0: 0.18, size1: 0.52, life: 0.4, vy: 1.8, add: true }); // 立ち昇る炎
                else if (adef.aura === "dark") this.p_emit(new THREE.Vector3(px, 1.0 + Math.random() * 0.5, pz), { tex: "soft", col0: 0xa050ff, col1: 0x1a0830, size0: 0.22, size1: 0.5, life: 0.5, vy: -0.7, add: true }); // 滴り落ちる闇
                else if (adef.aura === "gold") this.p_emit(new THREE.Vector3(px, 0.6 + Math.random() * 1.0, pz), { tex: "glow", col0: 0xfff4c0, col1: 0xffd75e, size0: 0.07, size1: 0.2, life: 0.5, vy: 0.9, add: true }); // 舞う金粉
            }
        }
        // ロボット: 両脇後方をジェットでホバリング。バウンド + 前傾/バンク + ゆっくりヨーで「飛んでる」感。
        // パーティゴアアーマー中は援軍が付かない設定 → 描画も止める。
        if (this.robotMesh) this.robotMesh.visible = !this.isPartyArmor;
        if (this.robotMesh && !this.isPartyArmor) {
            const rn = this.members.length > 0 ? Math.min(6, this.robots) : 0;
            let jetBudget = 4; // ジェット炎のフレーム予算 (パーティクルプール保護)
            for (let i = 0; i < rn; i++) {
                const side = (i % 2 === 0 ? -1 : 1) * (1.6 + Math.floor(i / 2) * 0.9);
                const bob = Math.sin(t * 2.2 + i * 1.3) * 0.22;          // 上下の浮遊
                const hover = 2.3 + Math.floor(i / 2) * 0.18 + bob;      // 群衆より高く浮く
                this.dummy.position.set(this.centroidX + side, hover, CROWD_Z - 1.6);
                // 前傾 (rx) + 進行方向への軽いバンク (rz) + 既存のゆっくりヨー (ry)
                this.dummy.rotation.set(
                    0.12 + Math.sin(t * 2.2 + i) * 0.05,
                    Math.sin(t * 1.0 + i) * 0.2,
                    Math.sin(t * 1.6 + i * 0.7) * 0.12 * (i % 2 === 0 ? 1 : -1),
                );
                this.dummy.scale.set(1, 1, 1);
                this.dummy.updateMatrix();
                this.robotMesh.setMatrixAt(i, this.dummy.matrix);
                // ジェット炎: 1 フレームおき + 機体数で間引いて (戦闘パーティクルを枯らさない)
                if ((this.trailTick & 1) === 0 && jetBudget > 0) {
                    jetBudget--;
                    for (const sx of [-0.18, 0.18]) {
                        this._v.set(this.centroidX + side + sx, hover - 0.5, CROWD_Z - 1.9);
                        this.p_emit(this._v, {
                            tex: "soft", vy: -2.4 - Math.random(), vx: (Math.random() - 0.5) * 0.5,
                            vz: -0.8, grav: 4, drag: 2.2, size0: 0.34, size1: 0.05,
                            col0: 0xff9a3b, col1: 0x7af2ff, life: 0.26, // 橙→シアン
                        });
                    }
                }
            }
            this.robotMesh.count = rn;
            this.robotMesh.instanceMatrix.needsUpdate = true;
        }
    }

    /** 着地スクワッシュの再アーム (イベントフレーム限定・毎フレームではない)。 */
    private pulseLanding(all: boolean): void {
        if (!all) return; // (index 指定版は任意。`all` がステージ遷移を網羅)
        for (const m of this.members) m.land = 0.18;
    }

    private changeCrew(delta: number): void {
        this.crew = Math.max(0, Math.min(MAX_CREW, Math.round(this.crew + delta)));
        if (this.crew > this.crewPeak) this.crewPeak = this.crew; // 回復の上限 = 到達した最大数
        this.syncMembers();
    }

    /**
     * 位置ベース当たり判定: 脅威 [cx±halfW] に「実際に重なっている個体」だけを消滅させる。
     * 当たったクルーの位置でパフを出すので、触れた場所のクルーが消える。完全に避ければ 0。
     * armor は「アーマー分は持ちこたえる」= 当たった個体の一部を生存させて軽減。
     */
    private collideCrowd(cx: number, halfW: number, bossHit = false, overhead = false, fromBehind = false): number {
        const lo = cx - halfW;
        const hi = cx + halfW;
        // 範囲内の個体 index を集める
        const hitIdx: number[] = [];
        for (let i = 0; i < this.members.length; i++) {
            const wx = this.centroidX + this.members[i].rx;
            if (wx >= lo && wx <= hi) hitIdx.push(i);
        }
        if (hitIdx.length === 0) return 0; // 避けきった (被害なし)

        // 背後/周回からの攻撃: 360°タレットが居れば耐久を削って受け止める (背後攻撃の答え)。
        if (fromBehind && this.turret && this.turret.hp > 0) {
            this.turret.hp -= TURRET_BACK_COST;
            this.shockRing(new THREE.Vector3(this.turret.obj.position.x, 0.9, CROWD_Z - 1.0), 0xffd24a, 3.2, 0.4);
            this.shake = Math.max(this.shake, 0.18);
            return 0; // 損失ゼロ (タレットが肩代わり)
        }
        // 背後からの攻撃 (flyer): 前面の波動砲/シールドは守れない。代わりにバックシールドが受け止める。
        if (fromBehind && this.backShieldHits > 0) {
            this.backShieldHits--;
            this.backShieldHitFlash = 0.3; // 背面バリアがパッと光る
            const flashes = Math.min(hitIdx.length, 16);
            for (let i = 0; i < flashes; i++) {
                const m = this.members[hitIdx[i]];
                this.shieldFlash(this.centroidX + m.rx, CROWD_Z - 1.6); // クルー背後でフラッシュ
            }
            this.shockRing(new THREE.Vector3(cx, 0.9, CROWD_Z - 2.2), 0xffb24a, 3.2, 0.4);
            this.shake = Math.max(this.shake, 0.18);
            if (this.backShieldHits === 0) {
                // 背面バリアが割れる: 破片バースト + リング + シェイク (背後で)
                const p = new THREE.Vector3(this.centroidX, 1.3, CROWD_Z - 3.0);
                this.impactBurst(p, "crew", 1.3);
                this.shockRing(p, 0xffb24a, 4.5, 0.5);
                this.popWorld("バックシールド破壊!", this.centroidX, "#ffb24a");
                this.shake = Math.max(this.shake, 0.4);
            }
            return 0; // 損失ゼロ
        }

        // 頭上からの攻撃: 360°タレットが居れば耐久を削って受け止める (背後と同じく overhead の答えにもなる)。
        // タレットは pitch で上を狙えるので演出的にも自然。天井シールドより先に肩代わりする。
        if (overhead && this.turret && this.turret.hp > 0) {
            this.turret.hp -= TURRET_BACK_COST;
            this.shockRing(new THREE.Vector3(this.turret.obj.position.x, 2.4, CROWD_Z), 0xffd24a, 3.2, 0.4);
            this.shake = Math.max(this.shake, 0.18);
            return 0; // 損失ゼロ (タレットが肩代わり)
        }

        // 天井シールド: 真上から降る攻撃 (overhead) を頭上キャノピーが完全ブロックして 1 個消費。
        // 前面/背面/波動砲では防げない overhead 専用の答え。
        if (overhead && this.ceilShieldHits > 0) {
            this.ceilShieldHits--;
            this.ceilShieldHitFlash = 0.3; // キャノピーがパッと光る
            const flashes = Math.min(hitIdx.length, 16);
            for (let i = 0; i < flashes; i++) {
                const m = this.members[hitIdx[i]];
                this.shieldFlash(this.centroidX + m.rx, CROWD_Z + m.rz);
            }
            this.shockRing(new THREE.Vector3(cx, 2.6, CROWD_Z), 0xb98bff, 3.2, 0.4);
            this.shake = Math.max(this.shake, 0.18);
            if (this.ceilShieldHits === 0) {
                // キャノピーが割れる: 破片バースト + リング + シェイク (頭上で)
                const p = new THREE.Vector3(this.centroidX, 2.6, CROWD_Z);
                this.impactBurst(p, "crew", 1.3);
                this.shockRing(p, 0xb98bff, 4.5, 0.5);
                this.popWorld("天井シールド破壊!", this.centroidX, "#b98bff");
                this.sfx.play("shieldbreak");
                this.shake = Math.max(this.shake, 0.4);
            }
            return 0; // 損失ゼロ
        }

        // 波動砲が前面で受け止める (召喚/発射中・頭上攻撃 overhead・背後攻撃 fromBehind は貫通)。
        // 発射中 (cannonFiring) はそのまま無敵で受け流すが、召喚中 (cannonHeld) は盾耐久を消費し 0 で砕ける。
        if ((this.cannonHeld || this.cannonFiring > 0) && !overhead && !fromBehind) {
            this.cannonFlashT = 0.2;
            this.shockRing(new THREE.Vector3(cx, 0.9, CROWD_Z + 2), 0x49e0ff, 3.0, 0.3);
            this.shake = Math.max(this.shake, 0.12);
            if (this.cannonHeld && this.cannonFiring <= 0) {
                this.cannonShieldHp -= 1;
                if (this.cannonShieldHp <= 0) this.breakCannon(cx);
            }
            return 0;
        }

        // シールド: 被弾を 1 回まるごと無効化して 1 個消費 (armor の％軽減とは別物・頭上は貫通)。
        // 当たったクルー全員にバリアのフラッシュを出して「弾いた」感を見せる。
        if (this.shieldHits > 0 && !overhead && !fromBehind) {
            this.shieldHits--;
            this.shieldHitFlash = 0.3; // バリアがパッと光る
            const flashes = Math.min(hitIdx.length, 16);
            for (let i = 0; i < flashes; i++) {
                const m = this.members[hitIdx[i]];
                this.shieldFlash(this.centroidX + m.rx, CROWD_Z + m.rz);
            }
            this.shockRing(new THREE.Vector3(cx, 0.9, CROWD_Z), 0x7bffea, 3.2, 0.4);
            this.shake = Math.max(this.shake, 0.18);
            if (this.shieldHits === 0) {
                // バリアが割れる: 破片バースト + リング + シェイク (次フレームから非表示)
                const p = new THREE.Vector3(this.centroidX, 1.3, CROWD_Z + 2.2);
                this.impactBurst(p, "crew", 1.3);
                this.shockRing(p, 0x7bffea, 4.5, 0.5);
                this.popWorld("シールド破壊!", this.centroidX, "#ff8f8f");
                this.sfx.play("shieldbreak");
                this.shake = Math.max(this.shake, 0.4);
            }
            return 0; // 損失ゼロ
        }

        // armor 軽減: 当たった個体の一部を救う。走行ハザードは従来どおり 1、
        // ボス命中のみ armor が効く (装備の防御的見返り)。
        const loss = bossHit ? 1 / (1 + this.effectiveArmor * BOSS_ARMOR_K) : this.lossMult;
        let toKill = Math.max(1, Math.round(hitIdx.length * loss));
        toKill = Math.min(toKill, hitIdx.length);

        // 表示上の損失と、論理クルー(上限超過分)の損失を比例で求める
        const visibleBefore = this.members.length;
        const ratioExtra = this.crew > visibleBefore ? this.crew / visibleBefore : 1;

        // アーマーで耐えた個体 (当たったが死ななかった分) にシールド演出
        const survived = hitIdx.length - toKill;
        if (this.armor > 0 && survived > 0) {
            const shields = Math.min(survived, 16);
            for (let i = 0; i < shields; i++) {
                const m = this.members[hitIdx[i]];
                this.shieldFlash(this.centroidX + m.rx, CROWD_Z + m.rz);
            }
        }

        // 当たった個体を後ろから削除 (index ずれ防止) + 消滅パフ
        for (let j = hitIdx.length - 1; j >= hitIdx.length - toKill; j--) {
            const idx = hitIdx[j];
            const m = this.members[idx];
            this.deathPuff(this.centroidX + m.rx, CROWD_Z + m.rz);
            this.members.splice(idx, 1);
        }

        // 生き残った数人に着地スクワッシュ (被弾に身をすくめる反応・純視覚・rx には触れない)
        const react = Math.min(this.members.length, 12);
        for (let i = 0; i < react; i++) this.members[i].land = 0.18;

        // 論理クルーを減らす (画面外分も比例で消す)
        const logicalLost = Math.round(toKill * ratioExtra);
        this.crew = Math.max(0, this.crew - logicalLost);
        // 上限超過時は画面に空きができたぶんだけ補充 (大軍のみ)
        this.syncMembers();
        this.hit();
        return logicalLost;
    }

    /** 消えたクルー1体の位置に小さな白いパフ (+モート) を出す。 */
    private deathPuff(x: number, z: number): void {
        this.impactBurst(new THREE.Vector3(x, 0.7, z), "crew", 0.3);
    }

    /** アーマーで耐えたクルーに青いシールドのバブルがはじける演出を出す。 */
    private shieldFlash(x: number, z: number): void {
        this.shockRing(new THREE.Vector3(x, 0.8, z), 0x6fc8ff, 1.4, 0.3);
    }

    /** クルー前面に張る湾曲エネルギーバリア (遮蔽物)。一度だけ生成し、表示/色は updateShieldBar で制御。 */
    private buildShieldBarrier(): void {
        const g = new THREE.Group();
        const mat = new THREE.MeshBasicMaterial({
            color: 0x7bffea, transparent: true, opacity: 0.28, side: THREE.DoubleSide,
            depthWrite: false, blending: THREE.AdditiveBlending,
        });
        // CylinderGeometry の周方向頂点は x=r·sinθ, z=r·cosθ。θ=0 が +z (前方) なので、
        // 円弧は θ=0 を中心に左右対称 (START=-LEN/2) にして「前面いっぱい」に展開する。
        const R = 2.5, LEN = 2.4, START = -LEN / 2; // 前方 (+z) に凸の広い円弧
        const arc = new THREE.Mesh(new THREE.CylinderGeometry(R, R, 2.8, 28, 1, true, START, LEN), mat);
        arc.position.y = 1.35;
        g.add(arc);
        // 明るい縦リブ (フォースフィールドの骨)。面と同じ式 (sinθ,cosθ) で並べて一致させる。
        const ribMat = new THREE.MeshBasicMaterial({ color: 0xeafffb, transparent: true, opacity: 0.75, depthWrite: false, blending: THREE.AdditiveBlending });
        const RIBS = 7;
        for (let i = 0; i <= RIBS; i++) {
            const a = START + LEN * (i / RIBS);
            const rib = new THREE.Mesh(new THREE.BoxGeometry(0.06, 2.8, 0.06), ribMat);
            rib.position.set(Math.sin(a) * R, 1.35, Math.cos(a) * R);
            g.add(rib);
        }
        g.visible = false;
        this.scene.add(g);
        this.shieldBar = g;
        this.shieldBarMat = mat;
    }

    /** バリアの表示・追従・残量演出 (満タン=シアン / 残り1=赤く点滅警告 / 被弾でパッと光る)。 */
    private updateShieldBar(dt: number): void {
        const bar = this.shieldBar;
        if (!bar || !this.shieldBarMat) return;
        if (this.shieldHitFlash > 0) this.shieldHitFlash = Math.max(0, this.shieldHitFlash - dt);
        const on = this.shieldHits > 0;
        bar.visible = on;
        if (!on) return;
        bar.position.set(this.centroidX, 0, CROWD_Z);
        const low = this.shieldHits <= 1;
        const t = this.clock.elapsedTime;
        const flick = low ? 0.5 + 0.5 * Math.abs(Math.sin(t * 12)) : 1;
        const base = 0.34 + (this.shieldHitFlash / 0.3) * 0.5; // 被弾で一瞬まばゆく
        this.shieldBarMat.opacity = Math.min(0.9, base) * flick;
        this.shieldBarMat.color.setHex(low ? 0xff6a6a : 0x7bffea);
    }

    /** クルー背面に張る湾曲エネルギーバリア (背後攻撃の遮蔽物)。前面バリアの鏡像で、琥珀色・後方に凸。 */
    private buildBackShieldBarrier(): void {
        const g = new THREE.Group();
        const mat = new THREE.MeshBasicMaterial({
            color: 0xffb24a, transparent: true, opacity: 0.28, side: THREE.DoubleSide,
            depthWrite: false, blending: THREE.AdditiveBlending,
        });
        // 前面バリアと同じ円弧だが、Y 軸 180° 回転で θ=0 を -z (後方) に向けて「背面いっぱい」に展開する。
        const R = 2.5, LEN = 2.4, START = -LEN / 2;
        const arc = new THREE.Mesh(new THREE.CylinderGeometry(R, R, 2.8, 28, 1, true, START, LEN), mat);
        arc.position.y = 1.35;
        g.add(arc);
        const ribMat = new THREE.MeshBasicMaterial({ color: 0xfff0d6, transparent: true, opacity: 0.75, depthWrite: false, blending: THREE.AdditiveBlending });
        const RIBS = 7;
        for (let i = 0; i <= RIBS; i++) {
            const a = START + LEN * (i / RIBS);
            const rib = new THREE.Mesh(new THREE.BoxGeometry(0.06, 2.8, 0.06), ribMat);
            rib.position.set(Math.sin(a) * R, 1.35, Math.cos(a) * R);
            g.add(rib);
        }
        g.rotation.y = Math.PI; // 後方 (-z) に凸へ反転
        g.visible = false;
        this.scene.add(g);
        this.backShieldBar = g;
        this.backShieldBarMat = mat;
    }

    /** 背面バリアの表示・追従・残量演出 (満タン=琥珀 / 残り1=赤く点滅警告 / 被弾でパッと光る)。 */
    private updateBackShieldBar(dt: number): void {
        const bar = this.backShieldBar;
        if (!bar || !this.backShieldBarMat) return;
        if (this.backShieldHitFlash > 0) this.backShieldHitFlash = Math.max(0, this.backShieldHitFlash - dt);
        const on = this.backShieldHits > 0;
        bar.visible = on;
        if (!on) return;
        bar.position.set(this.centroidX, 0, CROWD_Z - 2.2); // クルーの少し後ろに追従
        const low = this.backShieldHits <= 1;
        const t = this.clock.elapsedTime;
        const flick = low ? 0.5 + 0.5 * Math.abs(Math.sin(t * 12)) : 1;
        const base = 0.34 + (this.backShieldHitFlash / 0.3) * 0.5; // 被弾で一瞬まばゆく
        this.backShieldBarMat.opacity = Math.min(0.9, base) * flick;
        this.backShieldBarMat.color.setHex(low ? 0xff6a6a : 0xffb24a);
    }

    /** クルー頭上に張る湾曲キャノピー (頭上攻撃の遮蔽物)。前面/背面バリアの天井版で、紫色・上方に凸。 */
    private buildCeilShieldBarrier(): void {
        const g = new THREE.Group();
        const mat = new THREE.MeshBasicMaterial({
            color: 0xb98bff, transparent: true, opacity: 0.26, side: THREE.DoubleSide,
            depthWrite: false, blending: THREE.AdditiveBlending,
        });
        // 円筒の軸を Y→X へ倒し (rotation.z=90°)、円弧の中心を真上 (θ=π/2) に置く＝頭上を覆うヴォールト天井。
        // 倒した後の各頂点は world(x=-yh, y=R·sinθ, z=R·cosθ) になり、X 方向 (SPAN) に張り出す。
        const R = 3.0, SPAN = 5.0, LEN = 1.5, START = Math.PI / 2 - LEN / 2, Y0 = 1.0;
        const arc = new THREE.Mesh(new THREE.CylinderGeometry(R, R, SPAN, 28, 1, true, START, LEN), mat);
        arc.rotation.z = Math.PI / 2;
        arc.position.y = Y0;
        g.add(arc);
        // 横リブ (天井の骨)。アーチ頂点 (y=Y0+R, z=0) 沿いに X 方向へ等間隔に渡す。
        const ribMat = new THREE.MeshBasicMaterial({ color: 0xf0e2ff, transparent: true, opacity: 0.7, depthWrite: false, blending: THREE.AdditiveBlending });
        const RIBS = 6;
        for (let i = 0; i <= RIBS; i++) {
            const x = -SPAN / 2 + SPAN * (i / RIBS);
            const rib = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.06, R * LEN + 0.2), ribMat);
            rib.position.set(x, Y0 + R, 0);
            g.add(rib);
        }
        g.visible = false;
        this.scene.add(g);
        this.ceilShieldBar = g;
        this.ceilShieldBarMat = mat;
    }

    /** キャノピーの表示・追従・残量演出 (満タン=紫 / 残り1=赤く点滅警告 / 被弾でパッと光る)。 */
    private updateCeilShieldBar(dt: number): void {
        const bar = this.ceilShieldBar;
        if (!bar || !this.ceilShieldBarMat) return;
        if (this.ceilShieldHitFlash > 0) this.ceilShieldHitFlash = Math.max(0, this.ceilShieldHitFlash - dt);
        const on = this.ceilShieldHits > 0;
        bar.visible = on;
        if (!on) return;
        bar.position.set(this.centroidX, 0, CROWD_Z); // クルーの真上に追従
        const low = this.ceilShieldHits <= 1;
        const t = this.clock.elapsedTime;
        const flick = low ? 0.5 + 0.5 * Math.abs(Math.sin(t * 12)) : 1;
        const base = 0.3 + (this.ceilShieldHitFlash / 0.3) * 0.5; // 被弾で一瞬まばゆく
        this.ceilShieldBarMat.opacity = Math.min(0.85, base) * flick;
        this.ceilShieldBarMat.color.setHex(low ? 0xff6a6a : 0xb98bff);
    }

    /** 武器をアーセナルに追加し、その武器を自動選択する。初回取得時は拳銃(0)も同時に付与。 */
    private grantWeapon(id: number): void {
        const first = this.ownedWeapons.size === 0;
        this.ownedWeapons.add(id);
        if (first) this.ownedWeapons.add(0); // 基本のフォールバックを常に持たせる
        this.selectWeapon(id); // 最新が即装備される手触り
    }

    /** 所持武器を切替える (未所持なら無視)。geometry を再構築し、バナーで通知。 */
    private selectWeapon(id: number): void {
        if (!this.ownedWeapons.has(id)) return;
        if (this.weaponTier === id) return;
        this.weaponTier = id;
        if (id === ROCKET_WEAPON) this.rocketCdT = ROCKET_COOLDOWN; // 切替直後はすぐ撃てない
        if (id === HOMING_WEAPON) this.homingCdT = HOMING_COOLDOWN;
        this.rebuildWeapons();
        const def = WEAPON_DEFS[id];
        if (def) this.showBanner(`${def.icon} ${def.name}`);
    }

    private rebuildWeapons(): void {
        if (this.weaponMesh) {
            this.scene.remove(this.weaponMesh);
            this.weaponMesh.geometry.dispose();
            this.weaponMesh = null;
        }
        const geo = buildWeaponGeometry(this.weaponTier);
        this.weaponMesh = new THREE.InstancedMesh(geo, this.crewMat, MAX_INSTANCES);
        this.weaponMesh.frustumCulled = false;
        this.weaponMesh.count = 0;
        this.scene.add(this.weaponMesh);
    }

    private rebuildRobots(): void {
        if (!this.robotMesh) {
            this.robotMesh = new THREE.InstancedMesh(buildRobotGeometry(), this.crewMat, 6);
            this.robotMesh.frustumCulled = false;
            this.scene.add(this.robotMesh);
        }
    }

    /** 装備中アーマーの装甲プレートメッシュ＋素体マテリアルを作り直す (全クルー共通の1装備＝共有1色)。 */
    private rebuildArmorMesh(): void {
        // 旧メッシュを破棄
        if (this.armorMesh) { this.scene.remove(this.armorMesh); this.armorMesh.geometry.dispose(); this.armorMesh = null; }
        if (this.partyCrewMesh) { this.scene.remove(this.partyCrewMesh); this.partyCrewMesh.geometry.dispose(); this.partyCrewMesh = null; }
        const def = this.equippedArmorDef;
        if (def.id === "party") {
            // パーティゴアに変身: クルーをパーティゴアモデルで描き、素体メッシュを隠す
            this.partyCrewMesh = this.buildPartyCrewMesh(); // モデル未ロード時は null → 素体にフォールバック
            if (this.crowdMesh) this.crowdMesh.visible = !this.partyCrewMesh;
            this.applyArmorBodyLook(); // フォールバック(素体表示)時の見た目
            this.updateMonochrome();
            return;
        }
        if (this.crowdMesh) this.crowdMesh.visible = true;
        this.applyArmorBodyLook(); // 素体(全身)をアーマーの質感に塗り替える
        this.armorMat.emissive.setHex(def.emissive);
        this.armorMat.emissiveIntensity = def.aura === "none" ? 0.3 : 0.85;
        const mesh = new THREE.InstancedMesh(buildWornArmorGeometry(def), this.armorMat, MAX_INSTANCES);
        mesh.frustumCulled = false;
        mesh.count = 0;
        this.scene.add(mesh);
        this.armorMesh = mesh;
        this.updateMonochrome();
    }

    /** パーティゴア GLTF (単一メッシュ) からクルー用 InstancedMesh を作る (クルー身長に合わせて縮小)。未ロードなら null。 */
    private buildPartyCrewMesh(): THREE.InstancedMesh | null {
        if (!partygoerModel) return null;
        partygoerModel.updateWorldMatrix(true, true);
        let src: THREE.Mesh | null = null;
        partygoerModel.traverse((c) => { const m = c as THREE.Mesh; if (!src && m.isMesh && m.geometry) src = m; });
        if (!src) return null;
        const srcMesh = src as THREE.Mesh;
        const geo = srcMesh.geometry.clone();
        geo.applyMatrix4(srcMesh.matrixWorld); // 正規化(高さ2.2・足元y=0・正面-Z)を焼き込む
        const f = 1.5 / 2.2; // モデル高さ2.2 → クルー相当 ~1.5 へ縮小
        geo.scale(f, f, f);
        const mat = Array.isArray(srcMesh.material) ? srcMesh.material[0] : srcMesh.material; // テクスチャ付きマテリアル共有 (dispose しない)
        const mesh = new THREE.InstancedMesh(geo, mat, MAX_INSTANCES);
        mesh.frustumCulled = false;
        mesh.count = 0;
        this.scene.add(mesh);
        return mesh;
    }

    /** canvas のモノクロ filter を一元管理 (時間停止 or パーティゴアアーマーで白黒に)。 */
    private updateMonochrome(): void {
        const on = this.timeStop > 0 || this.isPartyArmor;
        if (on === this.timeStopFilterOn) return;
        this.timeStopFilterOn = on;
        this.renderer.domElement.style.filter = on ? "grayscale(1) contrast(1.06) brightness(0.97)" : "";
    }

    /** 素体メッシュのマテリアル (crewSkinMat) を装備アーマーの色・金属感・発光に塗り替える (全身が装甲化)。 */
    private applyArmorBodyLook(): void {
        const d = this.equippedArmorDef;
        this.crewSkinMat.color.setHex(d.body);
        this.crewSkinMat.metalness = d.metal;
        this.crewSkinMat.roughness = d.rough;
        this.crewSkinMat.emissive.setHex(d.emissive);
        this.crewSkinMat.emissiveIntensity = d.aura === "dark" ? 0.65 : d.aura === "fire" ? 0.55 : (d.emissive ? 0.35 : 0);
    }

    /** アーマーを装備に切り替える (解放済みのみ)。装甲メッシュを作り直し、選択を永続化。 */
    private equipArmor(id: string): void {
        if (id !== "none" && !this.unlockedArmors.has(id)) return;
        this.equippedArmorId = id;
        saveArmorEquip(id);
        this.rebuildArmorMesh();
        this.updateMonochrome(); // パーティゴアアーマーの白黒を反映/解除
        // 着替えの一閃: クルーを覆うリング + アクセント色のスパーク
        const def = this.equippedArmorDef;
        this.shockRing(new THREE.Vector3(this.centroidX, 1.0, CROWD_Z), def.accent || 0x9fd0ff, 3.6, 0.5);
        this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), def.accent || 0x9fd0ff);
        this.sfx.play("shield");
    }

    /** アーマーを新規解放する (既に解放済みなら何もしない)。解放を永続化しバナー表示。 */
    private unlockArmor(id: string): void {
        if (id === "none" || this.unlockedArmors.has(id)) return;
        this.unlockedArmors.add(id);
        saveArmorUnlocks(this.unlockedArmors);
        this.showBanner(`🛡 ${armorById(id).name} 解放！`, "ショップで装備できます");
        this.sfx.play("pickup");
    }

    // ---------- スポーン ----------
    private spawnRandom(): void {
        this.nextLane++;
        // 段階解禁の重み付き抽選: ステージが上がるほど登場物が増える (解禁を前倒しして
        // 序盤からギミックで賑やかに)。S1=ゲート/雑魚/台/スパイク / S2=ノコギリ・突進・砲台 /
        // S3=迫撃砲・炎の壁・ロケット / S5=ギガントロボ。
        const s = this.stage;
        const b = this.biomeIdx;
        const weights: [string, () => void, number][] = [
            ["gate", () => this.spawnGate(), 28],
            ["mobW", () => this.spawnMob(true), s === 1 ? 34 : 14], // 一撃の数押し雑魚 (序盤の主役)
            ["item", () => this.spawnItem(), 16],
            ["haz", () => this.spawnHazard(), 5 + s * 1.0], // スパイクは S1 から少し
        ];
        if (s >= 2) weights.push(["mobH", () => this.spawnMob(false), 12 + (s - 2) * 2]); // HP 持ち硬め雑魚
        if (s >= 2) weights.push(["saw", () => this.spawnSaw(), 6 + (s - 2) * 1.3]); // 回転ノコギリ
        if (s >= 2) weights.push(["chg", () => this.spawnCharger(), 6 + (s - 2) * 1.2]);
        if (s >= 2) weights.push(["arc", () => this.spawnArcher(false), 7 + (s - 2) * 1.4]); // 砲台を前倒し
        if (s >= 3) weights.push(["bom", () => this.spawnArcher(true), 5 + (s - 3) * 1.2]);
        if (s >= 3) weights.push(["sprd", () => this.spawnArcher(false, true), 5 + (s - 3) * 1.2]); // 扇状3連弾
        if (s >= 3) weights.push(["brth", () => this.spawnBreath(), 5 + (s - 3) * 1.0]); // 炎の壁を前倒し
        if (s >= 3) weights.push(["rkt", () => this.spawnRocket(), 5 + (s - 3) * 1.4]); // ロケットを前倒し
        if (s >= 2) weights.push(["wall", () => this.spawnWall(), 6 + (s - 2) * 1.2]); // 仕切り壁
        if (s >= 3) weights.push(["slal", () => this.spawnSlalom(), 4 + (s - 3) * 1.0]); // スラローム
        if (s >= 4) weights.push(["mvr", () => this.spawnMover(), 4 + (s - 4) * 1.0]); // 走査する光の壁
        if (s >= 5) weights.push(["gnt", () => this.spawnGiant(), 3 + (s - 5) * 0.8]); // ギガントを前倒し
        if (s >= 3) weights.push(["drgn", () => this.spawnDragon(), 5 + (s - 3) * 1.3]); // 突進ドラゴン
        if (s >= 4) weights.push(["lasr", () => this.spawnLaserSweep(), 4 + (s - 4) * 1.0]); // 薙ぎ払いレーザー
        if (s >= 3) weights.push(["slam", () => this.spawnSlam(), 5 + (s - 3) * 1.0]); // 落下プレス (AoE)
        if (s >= 4) weights.push(["homo", () => this.spawnHomingOrb(), 4 + (s - 4) * 1.0]); // 追尾オーブ
        if (s >= 3) weights.push(["trnd", () => this.spawnTornado(), 4 + (s - 3) * 0.9]); // 竜巻
        if (s >= 4) weights.push(["sky", () => this.spawnSkyMeteor(), 4 + (s - 4) * 1.0]); // 頭上隕石 (遮蔽貫通)
        if (s >= 5) weights.push(["skyb", () => this.spawnMeteorBarrage(), 4 + (s - 5) * 1.0]); // 頭上隕石の連弾
        if (s >= 4) weights.push(["bact", () => this.spawnBacteria(), 4 + (s - 4) * 1.1]); // Backrooms 風 bacteria
        if (s >= 4) weights.push(["party", () => this.spawnPartygoer(), 2.4 + (s - 4) * 0.4]); // Backrooms Level Fun のパーティゴアー群れ (希少)
        // 飛来兵 (背後攻撃) は終盤から。背後対策 (バックシールド=shop S3+ / 追尾ミサイル=S6 / 撃ち落とし) が揃う頃に解禁。
        if (s >= 7) weights.push(["fly", () => this.spawnFlyingRaider(), 3 + (s - 7) * 1.0]);
        if (s >= 4) weights.push(["sbot", () => this.spawnShieldBot(), 3 + (s - 4) * 0.8]); // 前面シールド + ミサイル + ジェット退避の敵ロボ
        if (s >= 8) weights.push(["orb", () => this.spawnOrbiter(), 2 + (s - 8) * 0.9]); // 群衆を旋回し全方位から撃つ浮遊敵 (タレットが対の答え)
        // --- NEW GIMMICKS ---
        if (s >= 2) weights.push(["pend", () => this.spawnPendulum(), 6 + (s - 2) * 1.0]); // G1 timing
        if (s >= 3) weights.push(["crsh", () => this.spawnCrusher(), 4 + (s - 3) * 1.0]); // G2 positional
        if (s >= 4) weights.push(["drft", () => this.spawnDriftGap(), 4 + (s - 4) * 1.0]); // G3 positional
        if (s >= 3) weights.push(["zig", () => this.spawnZigzag(), 4 + (s - 3) * 0.9]); // G4 positional
        if (s >= 2) weights.push(["bcrt", () => this.spawnBonusCrate(), 8]); // G5 reward shoot-down
        if (s >= 3) weights.push(["heal", () => this.spawnHealOrb(), 6 + (s - 3) * 0.8]); // G6 buff shoot-down
        if (s >= 2 && b === 1) weights.push(["qsnd", () => this.spawnQuicksand(), 5]); // G7 desert only
        if (s >= 2 && b === 2) weights.push(["ice", () => this.spawnIceSlick(), 5]); // G8 snow only
        if (s >= 3) weights.push(["arcf", () => this.spawnArcFence(), 5 + (s - 3) * 1.0]); // G9 timing (電流フェンス)
        if (s >= 4) weights.push(["pit", () => this.spawnCollapseFloor(), 4 + (s - 4) * 1.0]); // G10 positional (崩落床)
        if (s >= 3) weights.push(["wbst", () => this.spawnWeaponBoost(), 6 + (s - 3) * 0.6]); // G11 reward shoot-down (火力テンプレ)
        if (s >= 6) weights.push(["stlk", () => this.spawnStalker(), 2 + (s - 6) * 0.7]); // D mini-boss (多腕の徘徊体)
        // --- WILD GIMMICKS (A 転がり球 / B ワープゲート / C 操作反転 / D 重力反転) ---
        if (s >= 4) weights.push(["bldr", () => this.spawnBoulder(), 4 + (s - 4) * 1.0]); // A 巨大転がり球 (中央帯・脱二択)
        if (s >= 3) weights.push(["warp", () => this.spawnWarpGate(), 3 + (s - 3) * 0.7]); // B ワープゲート (進捗ナッジ・キルなし)
        if (s >= 4) weights.push(["invz", () => this.spawnInvertZone(), 3 + (s - 4) * 0.8]); // C 操作反転ゾーン
        if (s >= 5) weights.push(["grav", () => this.spawnGravityZone(), 3 + (s - 5) * 0.8]); // D 重力反転ゾーン

        let total = 0;
        for (const w of weights) total += w[2];
        let r = Math.random() * total;
        for (const [label, fn, w] of weights) {
            r -= w;
            if (r <= 0) {
                this.spawnLog[label] = (this.spawnLog[label] ?? 0) + 1;
                fn();
                return;
            }
        }
        this.spawnLog.gate = (this.spawnLog.gate ?? 0) + 1;
        this.spawnGate();
    }

    private laneX(): number {
        const side = (this.nextLane % 2 === 0) === Math.random() < 0.5;
        return (side ? -1 : 1) * (LANE * (0.25 + Math.random() * 0.5));
    }

    private spawnGate(): void {
        const grp = new THREE.Group();
        const { left, right } = makeGatePair(this.diff, this.stage);
        const w = LANE - 0.2;
        const biome = this.biomeIdx;
        const frameAccent = GIMMICK_SKIN[biome].frame; // biome flavor for the arch only
        const mk = (op: GateOp, sign: -1 | 1) => {
            const inc = applyGate(this.crew, op) >= this.crew; // UNCHANGED semantics
            const good = inc;
            const sub = new THREE.Group();
            const W = w, H = 2.6;
            const coolCol = 0x39c6ff, warmCol = 0xff5a4d; // buff cyan / nerf red
            const fieldCol = good ? coolCol : warmCol;

            // (1) ENERGY FIELD PANEL — additive translucent, faces camera (rotation.y = PI like the old number)
            const panelMat = new THREE.MeshBasicMaterial({
                map: this.gatePanelTex, color: fieldCol, transparent: true,
                opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide,
            });
            const panel = new THREE.Mesh(new THREE.PlaneGeometry(W, H), panelMat);
            panel.position.set(0, H / 2, 0);
            panel.rotation.y = Math.PI;
            panel.userData.scrollDir = good ? 1 : -1; // up=buff flow, down=nerf flow
            panel.userData.nerf = good ? 0 : 1; // jitter flag for the animation hook
            panel.renderOrder = 0;
            sub.add(panel); // MUST be children[0] (anim hook reads children[0])

            // (2) NEON ARCH FRAME — 4 thin bars; opaque (no blending → no overdraw). Biome-tinted toward frameAccent.
            const frameMat = new THREE.MeshBasicMaterial({ color: fieldCol, transparent: true, opacity: 0.9 });
            if (biome !== 2) frameMat.color.lerp(new THREE.Color(frameAccent), 0.25); // skip snow (keeps cyan readable)
            const barT = 0.14;
            const addBar = (bw: number, bh: number, px: number, py: number) => {
                const m = new THREE.Mesh(new THREE.BoxGeometry(bw, bh, 0.18), frameMat);
                m.position.set(px, py, 0);
                sub.add(m);
            };
            addBar(W + barT, barT, 0, H); // top
            addBar(W + barT, barT, 0, 0); // bottom sill
            addBar(barT, H, -W / 2, H / 2); // left post
            addBar(barT, H, W / 2, H / 2); // right post

            // (3) DIRECTION CHEVRON — additive sprite, high+up for buff, low+down for nerf
            const icon = new THREE.Sprite(new THREE.SpriteMaterial({
                map: this.gateArrowTex, color: fieldCol, transparent: true,
                opacity: 0.7, blending: THREE.AdditiveBlending, depthWrite: false,
            }));
            icon.scale.set(W * 0.5, W * 0.5, 1);
            icon.position.set(0, good ? 2.05 : 0.55, -0.05);
            if (!good) icon.material.rotation = Math.PI; // flip chevron → hazard/down
            icon.renderOrder = 1;
            sub.add(icon);

            // (4) GLOWING NUMBER — upgraded label (glow halo + outline + gradient), per-gate unique texture
            const numTex = makeLabelTexture(gateLabel(op), { // gateLabel UNCHANGED ("x2","-25","÷2","+9")
                fill: good ? "#eaffff" : "#fff0ec", glow: "#" + fieldCol.toString(16).padStart(6, "0"), outline: "#06101c",
            });
            const numMat = new THREE.MeshBasicMaterial({ map: numTex, transparent: true, depthWrite: false });
            const num = new THREE.Mesh(new THREE.PlaneGeometry(W * 0.82, W * 0.41), numMat);
            num.position.set(0, 1.3, -0.32); // in front of panel (toward camera at -z)
            num.rotation.y = Math.PI;
            num.renderOrder = 2;
            sub.add(num);

            sub.position.x = (sign * LANE) / 2; // UNCHANGED layout
            return sub;
        };
        grp.add(mk(left, -1), mk(right, 1));
        grp.userData.gateFx = true;
        this.scene.add(grp);
        this.entities.push({ type: "gate", obj: grp, z: SPAWN_Z, wx: 0, resolved: false, leftOp: left, rightOp: right });
        this.placeEntity(this.entities[this.entities.length - 1]);
    }

    /**
     * 雑魚スポーン。weak=true は「一撃で死ぬが数で押す」序盤雑魚 (S1 の主役)。
     * weak=false は HP を持つ硬めの群れ (S2 以降)。当たり幅は HP では変えず固定。
     */
    private spawnMob(weak: boolean): void {
        const x = this.laneX();
        let hp: number;
        let show: number;
        let cols: number;
        if (weak) {
            hp = Math.ceil(2 + this.diff * 3); // ほぼ一撃
            show = 3 + Math.floor(Math.random() * 3); // 3〜5 匹
            cols = show;
        } else {
            const early = Math.min(1, 0.5 + this.distance / 200);
            const count = Math.ceil((8 + this.crew * 0.7) * this.stageMul * (0.85 + this.diff * 0.4) * early);
            hp = count;
            show = Math.min(10, Math.max(4, Math.round(count / 3)));
            cols = Math.min(4, show);
        }
        const halfW = ((cols - 1) / 2) * 0.5 + 0.32;
        const grp = new THREE.Group();
        const im = new THREE.InstancedMesh(this.blobGeo, this.crewMat, show);
        for (let i = 0; i < show; i++) {
            const col = i % cols;
            const row = Math.floor(i / cols);
            this.dummy.position.set((col - (cols - 1) / 2) * 0.5, 0, row * 0.5);
            this.dummy.rotation.set(0, Math.PI, 0);
            this.dummy.scale.setScalar(weak ? 0.85 : 1);
            this.dummy.updateMatrix();
            im.setMatrixAt(i, this.dummy.matrix);
        }
        im.instanceMatrix.needsUpdate = true;
        grp.add(im);
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.6, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: MobEnt = { type: "mob", obj: grp, z: SPAWN_Z, wx: x, resolved: false, hp, max: hp, bar, halfW, weak };
        if (weak) e.bar.visible = false; // 一撃雑魚は HP バー不要
        else this.updateBar(e);
        if (!weak) this.maybeElite(e); // 硬め雑魚はたまにエリート
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** 砲台 (人型でない遠隔敵)。bomber=true で着弾広範囲の迫撃砲になる。 */
    private spawnArcher(bomber = false, spread = false): void {
        const x = (Math.random() < 0.5 ? -1 : 1) * (LANE * (0.6 + Math.random() * 0.3));
        const hp = Math.ceil((10 + this.diff * 18) * this.stageMul * (bomber ? 1.25 : spread ? 1.15 : 1));
        const grp = new THREE.Group();
        // 砲台ベース＋ターレット
        const base = new THREE.Mesh(
            new THREE.CylinderGeometry(0.42, 0.52, 0.5, 10),
            new THREE.MeshLambertMaterial({ color: 0x4a4f57 }),
        );
        base.position.y = 0.25;
        grp.add(base);
        const turret = new THREE.Mesh(
            new THREE.SphereGeometry(0.36, 10, 8),
            new THREE.MeshLambertMaterial({ color: bomber ? 0x7a3030 : spread ? 0x5a2f8f : 0x2f6a3d }),
        );
        turret.position.y = 0.6;
        grp.add(turret);
        if (spread) {
            // 扇状シューター: 3 連の砲身を前方に開く
            for (const ang of [-0.5, 0, 0.5]) {
                const b3 = new THREE.Mesh(new THREE.CylinderGeometry(0.07, 0.07, 0.7, 6), new THREE.MeshLambertMaterial({ color: 0x9a60d0 }));
                b3.rotation.x = Math.PI / 2;
                b3.rotation.y = ang;
                b3.position.set(Math.sin(ang) * 0.3, 0.62, 0.4);
                grp.add(b3);
            }
        } else {
            // 砲身: 迫撃砲は上向き / 通常は前向き
            const barrel = new THREE.Mesh(
                new THREE.CylinderGeometry(bomber ? 0.16 : 0.08, bomber ? 0.16 : 0.08, bomber ? 0.5 : 0.8, 8),
                new THREE.MeshLambertMaterial({ color: 0x23262c }),
            );
            if (bomber) {
                barrel.position.set(0, 0.95, 0);
            } else {
                barrel.rotation.x = Math.PI / 2;
                barrel.position.set(0, 0.62, 0.42);
            }
            grp.add(barrel);
        }
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.4, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: ArcherEnt = { type: "archer", obj: grp, z: SPAWN_Z, wx: x, resolved: false, hp, max: hp, bar, shootTimer: 0.8, bomber, spread };
        this.updateBar(e);
        this.maybeElite(e); // 砲台もたまにエリート
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** 突進する大型敵 (charger)。俊足・幅広・高 HP で「壊すか避けるか」を迫る。 */
    private spawnCharger(): void {
        const x = this.laneX();
        const hp = Math.ceil((26 + this.crew * 0.8) * this.stageMul * (0.8 + this.diff * 0.5));
        const grp = new THREE.Group();
        const body = new THREE.Mesh(new THREE.BoxGeometry(1.3, 1.3, 1.1), new THREE.MeshLambertMaterial({ color: 0xb53c39 }));
        body.position.y = 0.8;
        grp.add(body);
        for (const dx of [-0.4, 0.4]) {
            const horn = new THREE.Mesh(new THREE.ConeGeometry(0.14, 0.5, 6), new THREE.MeshLambertMaterial({ color: 0xf0e2c0 }));
            horn.rotation.x = Math.PI / 2.3;
            horn.position.set(dx, 1.3, 0.5);
            grp.add(horn);
        }
        const eye = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.18, 0.1), new THREE.MeshBasicMaterial({ color: 0xffe34d }));
        eye.position.set(0, 1.0, 0.56);
        grp.add(eye);
        const bar = this.makeBarSprite();
        bar.position.set(0, 2.0, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: BruiserEnt = { type: "bruiser", variant: "charger", obj: grp, z: SPAWN_Z, wx: x, resolved: false, hp, max: hp, bar, halfW: 0.85, speedMul: 1.7 };
        this.updateBar(e);
        this.maybeElite(e); // 突進もたまにエリート
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * 突進ドラゴン: 奥でホバリングして溜め (翼を羽ばたく + 赤く明滅) → 床に標的マーカーを出して
     * 一気に突進してくる。溜め中は遠くて撃てないので、突進が始まったら撃ち落とすか避ける。
     */
    private spawnDragon(): void {
        const x = this.laneX();
        const hp = Math.ceil((30 + this.crew * 0.9) * this.stageMul * (0.8 + this.diff * 0.5));
        const tint = this.skin.hazardCol; // バイオームの危険色 (火山=橙 / 墓地=緑 / 空=黄 …)
        const grp = new THREE.Group();
        const lift = new THREE.Object3D();
        lift.position.y = 0.6;
        grp.add(lift);
        const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.45, 0.7, 6, 10), new THREE.MeshLambertMaterial({ color: 0xb53c39 }));
        body.rotation.x = Math.PI / 2;
        lift.add(body);
        const neck = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.3, 0.7, 8), new THREE.MeshLambertMaterial({ color: 0xc24a44 }));
        neck.position.set(0, 0.25, 0.6);
        neck.rotation.x = 0.6;
        lift.add(neck);
        const head = new THREE.Mesh(new THREE.BoxGeometry(0.5, 0.42, 0.7), new THREE.MeshLambertMaterial({ color: 0xc24a44 }));
        head.position.set(0, 0.5, 0.95);
        lift.add(head);
        for (const dx of [-0.16, 0.16]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.08, 8, 6), new THREE.MeshBasicMaterial({ color: 0xffe34d }));
            eye.position.set(dx, 0.58, 1.25);
            lift.add(eye);
        }
        // 翼 2 枚 (lift の子・羽ばたく)
        const wings: THREE.Object3D[] = [];
        for (const side of [-1, 1]) {
            const wing = new THREE.Object3D();
            wing.position.set(side * 0.4, 0.2, 0);
            const membrane = new THREE.Mesh(new THREE.ConeGeometry(0.7, 1.6, 3), new THREE.MeshLambertMaterial({ color: 0x8a2f2c, side: THREE.DoubleSide }));
            membrane.scale.set(1, 1, 0.3);
            membrane.rotation.z = side * Math.PI / 2;
            membrane.position.x = side * 0.8;
            wing.add(membrane);
            lift.add(wing);
            wings.push(wing);
        }
        const tail = new THREE.Mesh(new THREE.ConeGeometry(0.2, 1.2, 6), new THREE.MeshLambertMaterial({ color: 0xb53c39 }));
        tail.rotation.x = -Math.PI / 2;
        tail.position.set(0, 0.1, -0.8);
        lift.add(tail);
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.9, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "dragon", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: 0.95, speedMul: 2.5, telegraph: 0, dragonPhase: "approach", wings, lift,
        };
        e.glow = this.projGlow(lift, tint, 2.6); // 溜めのオーラ
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * 飛来する強襲兵 (flyer): 高度を保ってクルー上空を弧で越え、背後に回り込んで一閃する。
     * 接近中は撃ち落とせる (HP 控えめ)。背後に回られる前に倒すか、バックシールドで受ける。
     */
    private spawnFlyingRaider(): void {
        const x = this.laneX();
        const hp = Math.ceil((16 + this.crew * 0.4) * this.stageMul * (0.8 + this.diff * 0.4)); // 控えめ HP (撃ち落とせる)
        const tint = this.skin.hazardCol;
        const grp = new THREE.Group();
        const flyLift = new THREE.Object3D(); // 高度の弧を乗せる子ノード (placeEntity の y リセットを回避)
        flyLift.position.y = 3.5;
        flyLift.scale.setScalar(0.78); // カメラに寄っても画面を覆い尽くさないよう一回り小さく
        grp.add(flyLift);
        // 本体 (細い菱形ボディ・暗所でも形が読めるよう明るめ + 自己発光)
        const body = new THREE.Mesh(new THREE.OctahedronGeometry(0.45), new THREE.MeshLambertMaterial({ color: 0x9a5fc4, emissive: 0x3a1f55 }));
        body.scale.set(1, 0.7, 1.5);
        flyLift.add(body);
        const head = new THREE.Mesh(new THREE.ConeGeometry(0.22, 0.6, 6), new THREE.MeshLambertMaterial({ color: 0xb070dc, emissive: 0x40225e }));
        head.rotation.x = -Math.PI / 2;
        head.position.set(0, 0.05, 0.7);
        flyLift.add(head);
        for (const dx of [-0.14, 0.14]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.09, 8, 6), new THREE.MeshBasicMaterial({ color: 0xff7a4a }));
            eye.position.set(dx, 0.12, 0.55);
            flyLift.add(eye);
            this.projGlow(eye, 0xff5a3a, 0.7); // 光る目 = 黒い塊でなく「蝙蝠」と読ませる
        }
        // 翼 2 枚 (羽ばたく・lift の子・明るめの膜で黒い壁にならないように)
        const wings: THREE.Object3D[] = [];
        for (const side of [-1, 1]) {
            const wing = new THREE.Object3D();
            wing.position.set(side * 0.3, 0.15, -0.1);
            const membrane = new THREE.Mesh(new THREE.ConeGeometry(0.6, 1.4, 3), new THREE.MeshLambertMaterial({ color: 0x7a4f9c, emissive: 0x2a1840, side: THREE.DoubleSide }));
            membrane.scale.set(1, 1, 0.25);
            membrane.rotation.z = side * Math.PI / 2;
            membrane.position.x = side * 0.7;
            wing.add(membrane);
            flyLift.add(wing);
            wings.push(wing);
        }
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.4, 0);
        flyLift.add(bar); // 高度に追従させる (頭上)
        // 真下の床に落とす影 (grp 直下=高度には乗せない)。毎フレ flyLift.y で拡縮/濃淡し、飛行高度を読ませる。
        const flyShadow = new THREE.Mesh(
            new THREE.CircleGeometry(0.5, 18),
            new THREE.MeshBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.32, depthWrite: false }),
        );
        flyShadow.rotation.x = -Math.PI / 2;
        flyShadow.position.y = 0.06;
        grp.add(flyShadow);
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "flyer", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: 1.1, speedMul: 2.2, wings, flyLift, flyShadow, flyPhase: "approach",
        };
        e.glow = this.projGlow(flyLift, tint, 1.8);
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * シールドロボ (道中の敵ロボ): ジェットで浮遊しつつ前面シールドを張り、ミサイルを撃ってくる。
     * 正面射撃はシールドに阻まれる (割ると本体を削れる)。プレイヤーが近づくと貫通せずジェットで上空へ退避。
     * 追尾ミサイルランチャーの特効対象 (シールドを無視して一撃)。
     */
    private spawnShieldBot(): void {
        const x = this.laneX();
        const hp = Math.ceil((22 + this.crew * 0.5) * this.stageMul * (0.8 + this.diff * 0.4));
        const shieldMax = Math.ceil((18 + this.crew * 0.4) * this.stageMul);
        const grp = new THREE.Group();
        const lift = new THREE.Object3D(); // 浮遊高度 (placeEntity の y リセット回避・退避で上昇)
        lift.position.y = 2.0;
        grp.add(lift);
        const steel = new THREE.MeshStandardMaterial({ color: 0x4a5a6e, roughness: 0.4, metalness: 0.7, envMapIntensity: 1 });
        const steelLt = new THREE.MeshStandardMaterial({ color: 0x8d9cae, roughness: 0.35, metalness: 0.7 });
        // 本体 (浮遊コア)
        const core = new THREE.Mesh(new THREE.BoxGeometry(1.1, 0.9, 1.0), steel);
        lift.add(core);
        const headLamp = new THREE.Mesh(new THREE.SphereGeometry(0.16, 10, 8), new THREE.MeshBasicMaterial({ color: 0xff5a5a }));
        headLamp.position.set(0, 0.2, -0.55); lift.add(headLamp);
        // ミサイルポッド (左右)
        for (const s of [-1, 1]) {
            const pod = new THREE.Mesh(new THREE.BoxGeometry(0.4, 0.36, 0.6), steelLt);
            pod.position.set(s * 0.75, 0.1, 0); lift.add(pod);
        }
        // 下向きスラスター (浮遊感) — 炎は update で粒子
        for (const s of [-1, 1]) {
            const noz = new THREE.Mesh(new THREE.CylinderGeometry(0.12, 0.2, 0.4, 8), steelLt);
            noz.position.set(s * 0.4, -0.55, 0.2); lift.add(noz);
        }
        // 前面シールド (半透明ドーム・クルー側 -z を覆う)
        const shieldMesh = new THREE.Mesh(
            new THREE.SphereGeometry(1.3, 16, 12, 0, Math.PI * 2, 0, Math.PI / 2),
            new THREE.MeshBasicMaterial({ color: 0x49e0ff, transparent: true, opacity: 0.3, depthWrite: false, side: THREE.DoubleSide }),
        );
        shieldMesh.rotation.x = -Math.PI / 2; // ドームを -z (クルー側) へ傾ける
        shieldMesh.position.set(0, 0.1, -0.5);
        lift.add(shieldMesh);
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.5, 0);
        lift.add(bar);
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "shieldbot", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: 0.9, speedMul: 1.5,
            lift, shieldMesh, shieldHp: shieldMax, shieldMax, shieldBroken: false, botFireT: 1.2,
        };
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * 周回する浮遊敵 (orbiter): 接近後、群衆の周りを旋回し任意方向(背後含む)からミサイルを撃つ終盤の敵。
     * 前にいる時しか群衆の弾は当たらず、背後/横は 360°タレットでしか落とせない。数周して飛び去る。
     */
    private spawnOrbiter(): void {
        const x = this.laneX();
        const hp = Math.ceil((20 + this.crew * 0.45) * this.stageMul * (0.8 + this.diff * 0.4));
        const tint = this.skin.hazardCol;
        const grp = new THREE.Group();
        const lift = new THREE.Object3D(); lift.position.y = 3.0; grp.add(lift);
        // 不気味な浮遊する単眼 + トゲのリング
        const orb = new THREE.Mesh(new THREE.SphereGeometry(0.5, 16, 12), new THREE.MeshLambertMaterial({ color: 0x5a2a6a, emissive: 0x3a1450 }));
        lift.add(orb);
        const iris = new THREE.Mesh(new THREE.SphereGeometry(0.26, 14, 12), new THREE.MeshBasicMaterial({ color: 0xff5a5a }));
        iris.position.set(0, 0, -0.4); lift.add(iris); // -z (正面=睨む向き) に瞳
        const pupil = new THREE.Mesh(new THREE.SphereGeometry(0.12, 10, 8), new THREE.MeshBasicMaterial({ color: 0x200008 }));
        pupil.position.set(0, 0, -0.6); lift.add(pupil);
        this.projGlow(iris, 0xff3a3a, 1.0);
        for (let i = 0; i < 7; i++) {
            const a = (i / 7) * Math.PI * 2;
            const spike = new THREE.Mesh(new THREE.ConeGeometry(0.1, 0.45, 5), new THREE.MeshLambertMaterial({ color: 0x3a1a4a, emissive: 0x200a30 }));
            spike.position.set(Math.cos(a) * 0.55, Math.sin(a) * 0.55, 0.1);
            spike.rotation.z = a - Math.PI / 2;
            lift.add(spike);
        }
        // 真下の影 (浮遊を読ませる)
        const flyShadow = new THREE.Mesh(new THREE.CircleGeometry(0.5, 18),
            new THREE.MeshBasicMaterial({ color: 0x000000, transparent: true, opacity: 0.28, depthWrite: false }));
        flyShadow.rotation.x = -Math.PI / 2; flyShadow.position.y = 0.06; grp.add(flyShadow);
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.1, 0); lift.add(bar);
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "orbiter", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: 0.9, speedMul: 1.9, lift, flyShadow,
            orbiting: false, orbitR: 6.5, orbitT: 9, orbFireT: 1.2,
        };
        e.glow = this.projGlow(lift, tint, 1.8);
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * Backrooms 風「bacteria」: うごめく青白い肉塊 + 黒い触手。低速・高 HP でじりじり迫る不気味な敵。
     * (実在モデルは読まず手続き的に近似。肉塊と触手を常時うねらせる。)
     */
    private spawnBacteria(small = false): void {
        const x = this.laneX();
        const hp = small
            ? Math.ceil((6 + this.diff * 6) * this.stageMul) // 小型=数押しの雑魚 (ほぼ一撃)
            : Math.ceil((42 + this.crew * 1.0) * this.stageMul * (0.8 + this.diff * 0.5));
        const grp = new THREE.Group();

        // --- 実モデル版: ロード済みなら FBX を clone して使う (内蔵アニメ再生) ---
        if (bacteriaModel) {
            const model = cloneSkeleton(bacteriaModel) as THREE.Object3D;
            // SkeletonUtils.clone は geometry/material/skeleton を元と共有する。
            // disposeObj が共有リソースを破壊しないよう shared フラグを立てる。
            model.traverse((c) => { c.userData.shared = true; });
            grp.add(model);
            let mixer: THREE.AnimationMixer | undefined;
            if (bacteriaModel.animations && bacteriaModel.animations.length > 0) {
                mixer = new THREE.AnimationMixer(model);
                mixer.clipAction(bacteriaModel.animations[0]).play();
            }
            const bar = this.makeBarSprite();
            bar.position.set(0, 2.4, 0);
            grp.add(bar);
            if (small) grp.scale.setScalar(0.55);
            this.scene.add(grp);
            const e: BruiserEnt = {
                type: "bruiser", variant: "bacteria", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
                hp, max: hp, bar, halfW: small ? 0.55 : 1.0, speedMul: small ? 1.45 : 0.9, mixer,
                pounceBaseScale: model.scale.x, // 実モデルは極小スケール → squash はこれを基準に掛ける
            };
            if (small) bar.visible = false;
            this.updateBar(e);
            if (!small) this.maybeElite(e);
            this.placeEntity(e);
            this.entities.push(e);
            return;
        }

        // --- フォールバック版: モデル未ロード時は手続き的スフィアで組む ---
        const fleshA = 0xc59a98, fleshB = 0x8a5a58, dark = 0x2a1820;
        const writhe: THREE.Object3D[] = [];
        // 肉塊 (重なる球)
        for (let i = 0; i < 8; i++) {
            const r = 0.45 + Math.random() * 0.5;
            const lump = new THREE.Mesh(new THREE.SphereGeometry(r, 10, 8), new THREE.MeshLambertMaterial({ color: i % 2 ? fleshA : fleshB }));
            const a = (i / 8) * Math.PI * 2;
            lump.position.set(Math.cos(a) * 0.55, 0.7 + Math.sin(a * 2) * 0.3, Math.sin(a) * 0.5);
            lump.userData.ph = Math.random() * 6.28;
            grp.add(lump); writhe.push(lump);
        }
        // 触手 (黒いコーン)
        for (let i = 0; i < 7; i++) {
            const a = (i / 7) * Math.PI * 2;
            const tend = new THREE.Mesh(new THREE.ConeGeometry(0.12, 1.3, 5), new THREE.MeshLambertMaterial({ color: dark }));
            tend.position.set(Math.cos(a) * 0.75, 0.8, Math.sin(a) * 0.6);
            tend.rotation.set(Math.random() * 0.6, a, Math.random() * 0.6 - 0.3);
            tend.userData.ph = Math.random() * 6.28;
            grp.add(tend); writhe.push(tend);
        }
        // 黒い眼窩
        for (const dx of [-0.22, 0.22]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.11, 8, 6), new THREE.MeshBasicMaterial({ color: 0x0a060c }));
            eye.position.set(dx, 1.05, 0.72);
            grp.add(eye);
        }
        const bar = this.makeBarSprite();
        bar.position.set(0, 2.0, 0);
        grp.add(bar);
        if (small) grp.scale.setScalar(0.55); // 小型のうごめく塊
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "bacteria", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: small ? 0.55 : 1.0, speedMul: small ? 1.45 : 0.9, writhe,
        };
        if (small) bar.visible = false; // 雑魚は HP バー不要
        this.updateBar(e);
        if (!small) this.maybeElite(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * パーティゴアー (Backrooms Level Fun): 2〜3 体を横にずらして同時生成する群れ。
     * 陽気に踊って揺れながら近づき、射程に入ると突然プレイヤーへ横ダート (掴みかかり)。
     * モデル未ロード時は出さない (簡易フォールバックは設けない)。
     */
    private spawnPartygoer(): void {
        if (!partygoerModel) return; // GLTF 未ロードなら出さない
        const baseX = this.laneX();
        const n = 1 + (Math.random() < 0.5 ? 1 : 0); // 群れ 1〜2 体 (数を絞って希少に)
        const stageMul = this.stageMul;
        for (let i = 0; i < n; i++) {
            const grp = new THREE.Group();
            const model = cloneSkeleton(partygoerModel) as THREE.Object3D;
            // 共有 geometry/material をキャッシュ元から複製している → disposeObj で破壊しないよう shared フラグ。
            model.traverse((c) => { c.userData.shared = true; });
            grp.add(model);
            const bar = this.makeBarSprite();
            bar.position.set(0, 2.4, 0);
            grp.add(bar);
            // コンガの列の親ノード。本体の子なので disposeObj(e.obj) で figure ごと自動破棄される。
            // 毎フレーム本体の踊り回転を打ち消して、figure をワールド軸で背後に並べる。
            const congaHolder = new THREE.Object3D();
            grp.add(congaHolder);
            this.scene.add(grp);
            // 群れを横にずらして並べる (lane 内に収める)
            const spread = (i - (n - 1) / 2) * 1.4;
            const laneX = Math.max(-(LANE - 0.7), Math.min(LANE - 0.7, baseX + spread));
            // 耐久を厚めに (倒すまで勧誘で増え続ける敵なので、撃ち切るのに手応えを持たせる)。
            const hp = Math.ceil((75 + this.crew * 1.7) * stageMul * (0.8 + this.diff * 0.4));
            const e: BruiserEnt = {
                type: "bruiser", variant: "partygoer", obj: grp, z: SPAWN_Z + i * 1.5, wx: laneX, resolved: false,
                hp, max: hp, bar, halfW: 0.7, speedMul: 1.0,
                partyBaseScale: model.scale.x, // 正規化後の基準スケール (演出はこの相対で掛ける)
                partyLaneX: laneX,
                danceSeed: Math.random() * Math.PI * 2,
                congaHolder,
                conga: [],
                congaTrail: [],
                recruited: 0,
                recruitCd: PARTY_RECRUIT_CD,
                recruitDone: false,
            };
            this.updateBar(e);
            this.placeEntity(e);
            this.entities.push(e);
        }
    }

    /**
     * 簡易クルー figure (コンガの列用)。青いクルー風カプセル + 小さな頭。
     * 共有モデルではなく自前プリミティブなので disposeObj で破棄されてよい (shared フラグは付けない)。
     */
    private makeCongaFigure(): THREE.Object3D {
        const g = new THREE.Group();
        const bodyMat = new THREE.MeshLambertMaterial({ color: 0x3a7bd5 }); // クルー風の青
        const body = new THREE.Mesh(new THREE.CapsuleGeometry(0.26, 0.5, 4, 8), bodyMat);
        body.position.y = 0.5;
        g.add(body);
        const headMat = new THREE.MeshLambertMaterial({ color: 0x9fc4ff }); // バイザー風の薄青
        const head = new THREE.Mesh(new THREE.SphereGeometry(0.18, 8, 6), headMat);
        head.position.set(0.16, 0.74, 0.12);
        g.add(head);
        g.scale.setScalar(0.85); // 本体より一回り小さく
        return g;
    }

    /**
     * 勧誘 (まとめて連れ去る): 1 回の呼び出しで n 人 (既定 2) を一気に連れ去り、コンガの列を伸ばす。
     * crew 減少分は syncMembers が末尾個体を落として追従する (既存のクルー削除作法)。
     * クルー 4 人を下回るまでは奪わない (放置ソフトロック防止の床)。
     */
    private recruitOne(e: BruiserEnt, n = 2): void {
        if (this.crew <= 0) return;
        if ((e.recruited ?? 0) >= PARTY_RECRUIT_MAX) { e.recruitDone = true; return; }
        let took = 0;
        for (let i = 0; i < n; i++) {
            if (this.crew <= 4) break; // 4 人未満は奪わない (アンチソフトロックの床)
            if ((e.recruited ?? 0) >= PARTY_RECRUIT_MAX) break;
            // クルーを確実に 1 人減らす (画面個体は syncMembers が末尾から落とす)。
            this.crew = Math.max(0, this.crew - 1);
            e.recruited = (e.recruited ?? 0) + 1;
            // コンガの列に 1 体追加 (congaHolder の子 = disposeObj(e.obj) で一緒に破棄される)。
            const fig = this.makeCongaFigure();
            e.congaHolder?.add(fig);
            e.conga?.push(fig);
            took++;
        }
        if (took === 0) return; // 何も奪えなかった (床に当たった) → 演出も出さない
        this.syncMembers();
        // 不気味な勧誘演出 (1 回の呼び出しで 1 度だけ = スパム回避)
        this.popWorld("JOIN US…", e.wx, "#ff66cc");
        this.spark(new THREE.Vector3(this.centroidX, 0.6, CROWD_Z), 0xff66cc); // 連れ去られたクルーの位置
        this.spark(new THREE.Vector3(e.wx, 0.6, e.obj.position.z), 0xff66cc);
        this.shake = Math.max(this.shake, 0.16);
        if ((e.recruited ?? 0) >= PARTY_RECRUIT_MAX) e.recruitDone = true; // 上限 → 退場フェーズへ
    }

    /**
     * コンガの列を本体の背後に追従させる。本体のワールド位置履歴 (congaTrail) を一定間隔で
     * サンプルして figure を遅延配置 → コンガっぽく少しうねる。figure は congaHolder の子なので、
     * 本体の踊り回転を打ち消した上でワールド軸の相対位置 (= ワールド差分) を local に与える。
     */
    private updateConga(e: BruiserEnt, _dt: number): void {
        const holder = e.congaHolder;
        const figs = e.conga;
        if (!holder) return;
        // 本体の踊り回転/傾きを打ち消して、figure をワールド軸で並べられるようにする。
        holder.rotation.set(-e.obj.rotation.x, -e.obj.rotation.y, -e.obj.rotation.z);
        holder.position.set(0, 0, 0);
        if (!figs || figs.length === 0) return;
        // ワールド位置履歴を記録 (先頭が最新)。長すぎないよう上限で切る。
        const trail = (e.congaTrail ??= []);
        trail.unshift({ x: e.wx, z: e.obj.position.z });
        const maxTrail = (figs.length + 2) * 8;
        if (trail.length > maxTrail) trail.length = maxTrail;
        const t = this.clock.elapsedTime;
        for (let i = 0; i < figs.length; i++) {
            // 背後 (奥=+z) へ間隔ぶん下げた距離に相当する履歴サンプルを拾う。
            const back = (i + 1) * PARTY_CONGA_GAP;
            const sampleIdx = Math.min(trail.length - 1, Math.round(back * 6));
            const s = trail[sampleIdx];
            // 目標ワールド位置 (本体より奥に並ぶ) + コンガのうねり (左右に sin)
            const wobble = Math.sin(t * 4 + i * 0.9) * 0.35;
            const targetWX = s.x + wobble;
            const targetZ = s.z + back; // 奥へ
            // congaHolder は本体 (e.obj) の子なので、local = ワールド差分。
            // holder の回転は上で打ち消し済みなので、ワールド軸の差分をそのまま local 座標に使える。
            const fig = figs[i];
            fig.position.set(targetWX - e.wx, 0, targetZ - e.obj.position.z);
            fig.rotation.y = Math.PI + Math.sin(t * 3 + i) * 0.3; // 本体側を向きつつ揺れる (踊り)
            const hop = Math.abs(Math.sin(t * 6 + i * 1.3)) * 0.14; // 上下にぴょこぴょこ (≥0)
            fig.position.y = hop;
        }
    }

    /**
     * パーティゴアー撃破時: 連れ去ったクルーを取り返す (crew += recruited)。
     * コンガ figure は本体ごと disposeObj で破棄されるが、その前に散らす演出を出す。
     */
    private returnRecruited(e: BruiserEnt): void {
        const back = e.recruited ?? 0;
        if (back <= 0) return;
        this.crew = Math.min(MAX_CREW, this.crew + back); // 取り返し
        this.syncMembers();
        // コンガ figure を散らす演出 (figure 自体は disposeObj(e.obj) で破棄される)
        for (const fig of e.conga ?? []) {
            const p = new THREE.Vector3();
            fig.getWorldPosition(p);
            this.spark(p.setY(0.6), 0x66ffcc);
        }
        this.popWorld(`+${back} 取り返した！`, e.wx, "#7bffea");
        this.shake = Math.max(this.shake, 0.3);
        e.recruited = 0; // 二重取り返し防止
    }

    /** エンティティ一掃の直前に、生きているパーティゴアーが連れ去ったクルーを全員取り返す。 */
    private returnAllRecruited(): void {
        for (const e of this.entities) {
            if (e.type === "bruiser" && e.variant === "partygoer") this.returnRecruited(e);
        }
    }

    /** ギガントロボ (中ボス級)。超高 HP・広範囲。倒すと派手に爆発。 */
    private spawnGiant(): void {
        const x = (Math.random() * 2 - 1) * (LANE * 0.3);
        const hp = Math.ceil((140 + this.crew * 2.5) * this.stageMul);
        const grp = new THREE.Group();
        const robo = new THREE.Mesh(buildGiantRobotGeometry(), this.giantMat);
        robo.scale.setScalar(2.4);
        grp.add(robo);
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 7.2, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: BruiserEnt = { type: "bruiser", variant: "giant", obj: grp, z: SPAWN_Z, wx: x, resolved: false, hp, max: hp, bar, halfW: 1.6, speedMul: 0.8 };
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** ロケット弾: 奥から高速直進。避けるか撃ち落とす。着弾で爆発。 */
    private spawnRocket(): void {
        const x = this.laneX();
        const hp = Math.ceil((9 + this.diff * 10) * this.stageMul);
        const grp = new THREE.Group();
        const bodyMat = new THREE.MeshLambertMaterial({ color: 0xcfd6df });
        const tube = new THREE.Mesh(new THREE.CylinderGeometry(0.22, 0.22, 1.1, 10), bodyMat);
        tube.rotation.x = Math.PI / 2;
        tube.position.y = 1.0;
        grp.add(tube);
        const nose = new THREE.Mesh(new THREE.ConeGeometry(0.22, 0.5, 10), new THREE.MeshLambertMaterial({ color: 0xd64541 }));
        nose.rotation.x = -Math.PI / 2;
        nose.position.set(0, 1.0, -0.75);
        grp.add(nose);
        const flame = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.5, 8), new THREE.MeshBasicMaterial({ color: 0xff8f2e }));
        flame.rotation.x = Math.PI / 2;
        flame.position.set(0, 1.0, 0.75);
        grp.add(flame);
        const bar = this.makeBarSprite();
        bar.position.set(0, 1.7, 0);
        grp.add(bar);
        this.scene.add(grp);
        const e: RocketEnt = { type: "rocket", obj: grp, z: SPAWN_Z, wx: x, resolved: false, hp, max: hp, bar, halfW: 1.0, speedMul: 2.2 };
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    private spawnHazard(): void {
        const x = this.laneX();
        const grp = new THREE.Group();
        const base = new THREE.Mesh(new THREE.BoxGeometry(1.6, 0.4, 1.0), new THREE.MeshLambertMaterial({ color: this.skin.spikeBase }));
        base.position.y = 0.2;
        grp.add(base);
        for (let i = -2; i <= 2; i++) {
            const spike = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.6, 6), new THREE.MeshLambertMaterial({ color: this.skin.spike }));
            spike.position.set(i * 0.32, 0.7, 0);
            grp.add(spike);
        }
        this.scene.add(grp);
        const e: HazardEnt = { type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false, halfW: 0.85, fire: false, sweepAmp: 0, sweepPhase: 0 };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** 回転ノコギリ: 横に往復しながら刃が回る障害物。反対側に寄って避ける。 */
    private spawnSaw(): void {
        const grp = new THREE.Group();
        // 支柱
        const post = new THREE.Mesh(new THREE.BoxGeometry(0.16, 1.5, 0.16), new THREE.MeshLambertMaterial({ color: 0x555a63 }));
        post.position.y = 0.75;
        grp.add(post);
        // 回転刃 (円盤・刃面を前後に向ける)
        const discGeo = new THREE.CylinderGeometry(0.7, 0.7, 0.08, 18);
        discGeo.rotateX(Math.PI / 2);
        const disc = new THREE.Mesh(discGeo, new THREE.MeshLambertMaterial({ color: this.skin.saw }));
        disc.position.y = 1.05;
        grp.add(disc);
        // ギザ歯 (disc の子。disc 中心からの相対座標で刃面の x-y 平面に並べる)
        for (let i = 0; i < 8; i++) {
            const a = (i / 8) * Math.PI * 2;
            const tooth = new THREE.Mesh(new THREE.ConeGeometry(0.1, 0.22, 4), new THREE.MeshLambertMaterial({ color: this.skin.saw }));
            tooth.position.set(Math.cos(a) * 0.78, Math.sin(a) * 0.78, 0);
            tooth.rotation.z = a - Math.PI / 2; // 円錐の先を外向きに
            disc.add(tooth);
        }
        // 中心ハブ
        const hub = new THREE.Mesh(new THREE.SphereGeometry(0.18, 10, 8), new THREE.MeshLambertMaterial({ color: 0xd64541 }));
        hub.position.y = 1.05;
        grp.add(hub);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false, halfW: 0.8, fire: false,
            sweepAmp: LANE - 1.0, sweepPhase: Math.random() * 6.28, spin: disc,
        };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** G1 振り子ハンマー: レーンを横切って往復する重りに当たる前にリズムよく避ける。 */
    private spawnPendulum(): void {
        const sk = this.skin;
        const grp = new THREE.Group();
        const post = new THREE.Mesh(new THREE.BoxGeometry(0.16, 4.2, 0.16),
            new THREE.MeshLambertMaterial({ color: 0x555a63 }));
        post.position.y = 4.0; grp.add(post); // top bar of lane
        const pivot = new THREE.Object3D(); pivot.position.y = 4.0; grp.add(pivot);
        const arm = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 3.2, 8),
            new THREE.MeshLambertMaterial({ color: 0x6b6f78 }));
        arm.position.y = -1.6; pivot.add(arm);
        const head = new THREE.Mesh(new THREE.BoxGeometry(1.4, 1.0, 1.0),
            new THREE.MeshLambertMaterial({ color: sk.wall })); // biome: wood/ice/stone/anchor tint
        head.position.y = -3.2; pivot.add(head);
        const warn = new THREE.Mesh(new THREE.BoxGeometry(1.46, 0.16, 1.06),
            new THREE.MeshBasicMaterial({ color: 0xffd23b }));
        warn.position.y = -3.2; pivot.add(warn);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0.75, fire: false, sweepAmp: LANE * 0.7, sweepPhase: Math.random() * 6.28,
            sweepFreq: 1.4, swingHead: pivot, swingAmpZ: 0.35, // cosmetic lean; lethal x = e.wx (grp translation)
        };
        this.floorTell(0, sk.accent, 0.75);
        this.placeEntity(e); this.entities.push(e);
    }

    /** G2 圧縮シュート: 左右の壁が迫る。中央に密集して隙間を通り抜ける。 */
    private spawnCrusher(): void {
        const sk = this.skin;
        const grp = new THREE.Group();
        const gap1 = this.crowdHalfW + 0.6; // always survivable centered
        const gap0 = LANE - 0.3; // starts wide open
        const mk = (mir: number) => {
            const slab = new THREE.Mesh(new THREE.BoxGeometry(LANE, 3.0, 0.7),
                new THREE.MeshLambertMaterial({ color: sk.wall }));
            slab.position.set((mir * (LANE + gap0)) / 2, 1.5, 0);
            // inner-face warning stripe (reuse addWall stripe colors; do NOT biome-tint)
            for (let i = 0; i < 5; i++) {
                const st = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.5, 0.74),
                    new THREE.MeshLambertMaterial({ color: i % 2 ? 0xffd23b : 0x23262c }));
                st.position.set((-mir * LANE) / 2 + mir * 0.16, 0.45 + i * 0.55, 0);
                slab.add(st);
            }
            grp.add(slab); return slab;
        };
        const left = mk(-1), right = mk(1);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0, fire: false, sweepAmp: 0, sweepPhase: 0,
            crusher: { left, right, gap0, gap1 },
        };
        this.floorTell(0, sk.accent, gap1); // tell the centered safe gap
        this.placeEntity(e); this.entities.push(e);
    }

    /** ドラゴンブレス: レーンの片側を覆う炎の壁。安全な隙間へ寄せて避ける。 */
    private spawnBreath(): void {
        // 安全な隙間を残して片側を覆う (隙間幅は群衆が入れる程度)
        const gap = Math.max(this.crowdHalfW + 0.5, 1.4);
        const boundary = (Math.random() * 2 - 1) * (LANE - gap); // 炎と安全地帯の境界
        const left = Math.random() < 0.5;
        const edge = left ? -LANE : LANE;
        const cx = (boundary + edge) / 2;
        const halfW = Math.abs(boundary - edge) / 2;

        const grp = new THREE.Group();
        const mat = new THREE.MeshBasicMaterial({ color: this.skin.hazardCol, transparent: true, opacity: 0.65 });
        const wall = new THREE.Mesh(new THREE.BoxGeometry(halfW * 2, 2.4, 0.8), mat);
        wall.position.set(0, 1.2, 0);
        grp.add(wall);
        // 炎の舌 (上に伸びるコーン)
        const cols = Math.max(2, Math.round(halfW * 1.6));
        for (let i = 0; i < cols; i++) {
            const flame = new THREE.Mesh(
                new THREE.ConeGeometry(0.4, 1.6, 6),
                new THREE.MeshBasicMaterial({ color: i % 2 ? 0xffd23b : 0xff7a1e, transparent: true, opacity: 0.85 }),
            );
            flame.position.set(-halfW + (i + 0.5) * ((halfW * 2) / cols), 2.4, 0);
            grp.add(flame);
        }
        const lbl = new THREE.Sprite(
            new THREE.SpriteMaterial({ map: makeLabelTexture("🐉", { size: 70 }), transparent: true }),
        );
        lbl.scale.set(1.6, 1.6, 1);
        lbl.position.set(0, 3.6, 0);
        grp.add(lbl);
        this.scene.add(grp);
        const e: HazardEnt = { type: "hazard", obj: grp, z: SPAWN_Z, wx: cx, resolved: false, halfW, fire: true, sweepAmp: 0, sweepPhase: 0 };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * 薙ぎ払いレーザー: 立った光の刃がレーンを左右にスイープしながら接近する。
     * 細いので刃の無い側へ slip して避ける。弾と違い「線」で来る新攻撃。
     */
    private spawnLaserSweep(): void {
        const col = this.skin.accent;
        const grp = new THREE.Group();
        const beamMat = new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.85, blending: THREE.AdditiveBlending, depthWrite: false });
        const beam = new THREE.Mesh(new THREE.BoxGeometry(0.32, 3.2, 6), beamMat);
        beam.position.y = 1.6;
        grp.add(beam);
        const core = new THREE.Mesh(new THREE.BoxGeometry(0.12, 3.2, 6),
            new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false }));
        core.position.y = 1.6;
        grp.add(core);
        const emitter = new THREE.Mesh(new THREE.SphereGeometry(0.4, 10, 8), new THREE.MeshBasicMaterial({ color: col }));
        emitter.position.y = 3.35;
        grp.add(emitter);
        this.projGlow(emitter, col, 2.2);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0.55, fire: false, sweepAmp: LANE - 0.7, sweepPhase: Math.random() * 6.28, sweepFreq: 2.4,
        };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /**
     * 仕切り壁を 1 枚生成する共通処理。coverLeft=true ならレーン左側を塞ぎ、
     * 隙間は右側に残る (false で逆)。z はスポーン位置 (スラロームで前後にずらす)。
     */
    private addWall(coverLeft: boolean, z: number): void {
        const gap = Math.max(this.crowdHalfW + 0.7, 1.7); // 群衆が通れる隙間
        const boundary = (Math.random() * 0.5 + 0.25) * (LANE - gap) * (coverLeft ? 1 : -1);
        const edge = coverLeft ? -LANE : LANE;
        const cx = (boundary + edge) / 2;
        const halfW = Math.abs(boundary - edge) / 2;
        const openEdgeLocal = coverLeft ? halfW : -halfW; // 隙間に面する壁の端 (local x)

        const grp = new THREE.Group();
        // 本体スラブ (木の板塀)
        const slab = new THREE.Mesh(
            new THREE.BoxGeometry(halfW * 2, 3.0, 0.7),
            new THREE.MeshLambertMaterial({ color: this.skin.wall }),
        );
        slab.position.y = 1.5;
        grp.add(slab);
        // 上下の横木 (濃い木目の帯)
        for (const yy of [0.2, 1.5, 2.8]) {
            const band = new THREE.Mesh(
                new THREE.BoxGeometry(halfW * 2 + 0.04, 0.32, 0.78),
                new THREE.MeshLambertMaterial({ color: this.skin.wallBand }),
            );
            band.position.y = yy;
            grp.add(band);
        }
        // 隙間側の端に黄黒の警告ストライプ柱 (通り道が一目で分かる)
        for (let i = 0; i < 5; i++) {
            const stripe = new THREE.Mesh(
                new THREE.BoxGeometry(0.34, 0.55, 0.82),
                new THREE.MeshLambertMaterial({ color: i % 2 ? 0xffd23b : 0x23262c }),
            );
            stripe.position.set(openEdgeLocal - (coverLeft ? 0.18 : -0.18), 0.45 + i * 0.55, 0);
            grp.add(stripe);
        }
        this.scene.add(grp);
        const e: HazardEnt = { type: "hazard", obj: grp, z, wx: cx, resolved: false, halfW, fire: false, sweepAmp: 0, sweepPhase: 0 };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** 仕切り壁: レーンの片側を固い壁で塞ぐ。隙間へ寄せて通り抜ける。 */
    private spawnWall(): void {
        this.addWall(Math.random() < 0.5, SPAWN_Z);
    }

    /**
     * Level 0 の「曲がる」行き止まり壁: マップが途切れ、片側 (side) だけ通路が開く。
     * 「曲がれ！」ガイドを出し、開口へ寄れば無傷。塞がれた幅に重なった列だけ削れる (ミスの度合いで可変)。
     * 通過の瞬間にカメラを大きくスイングして「曲がった」体感を出す (resolveEntity 側で起動)。
     */
    private spawnTurnWall(side: -1 | 1): void {
        const sk = this.skin;
        const gapHalf = Math.max(this.crowdHalfW + 0.6, 1.3); // 通路の半幅
        const cx = -side * gapHalf;     // 塞がれた壁の中心 (開口の反対へ寄る)
        const halfW = LANE - gapHalf;   // 壁はレーンのほぼ全幅を塞ぐ (行き止まり感)
        const grp = new THREE.Group();
        const slab = new THREE.Mesh(new THREE.BoxGeometry(halfW * 2, 3.6, 0.8), new THREE.MeshLambertMaterial({ color: sk.wall }));
        slab.position.set(cx, 1.8, 0); grp.add(slab);
        for (const yy of [0.3, 1.8, 3.3]) {
            const band = new THREE.Mesh(new THREE.BoxGeometry(halfW * 2 + 0.04, 0.34, 0.86), new THREE.MeshLambertMaterial({ color: sk.wallBand }));
            band.position.set(cx, yy, 0); grp.add(band);
        }
        // 開口に面した端に黄黒の警告ストライプ柱 (通り道が一目で分かる)
        const openEdge = side * (LANE - 2 * gapHalf);
        for (let i = 0; i < 6; i++) {
            const stripe = new THREE.Mesh(new THREE.BoxGeometry(0.36, 0.58, 0.9), new THREE.MeshLambertMaterial({ color: i % 2 ? 0xffd23b : 0x23262c }));
            stripe.position.set(openEdge + side * 0.2, 0.5 + i * 0.56, 0); grp.add(stripe);
        }
        this.scene.add(grp);
        const e: HazardEnt = { type: "hazard", obj: grp, z: SPAWN_Z, wx: cx, resolved: false, halfW, fire: false, sweepAmp: 0, sweepPhase: 0, turnWall: { side, swung: false } };
        this.placeEntity(e); this.entities.push(e);
        this.showBanner(side > 0 ? "つきあたり！ 右へ →" : "← 左へ つきあたり！", "曲がれ！");
        this.floorTell(side * (LANE - gapHalf), 0xffe24b, gapHalf); // 開口の床ハイライト
    }

    /** スラローム: 隙間が左右逆の壁を前後 2 枚並べ、ジグザグに抜けさせる。 */
    private spawnSlalom(): void {
        const firstLeft = Math.random() < 0.5;
        this.addWall(firstLeft, SPAWN_Z + 7); // 奥の壁
        this.addWall(!firstLeft, SPAWN_Z); // 手前の壁 (隙間が逆側)
    }

    /** G3 流動ゲートの壁: 安全な穴が左右に滑る。穴を追いかけて通り抜ける。 */
    private spawnDriftGap(): void {
        const sk = this.skin;
        const gapHalf = Math.max(this.crowdHalfW + 0.6, 1.6);
        const sweepAmp = LANE - gapHalf - 0.3; // range the hole can slide
        const segLen = LANE; // each segment long enough to cover its side
        const grp = new THREE.Group();
        const mkSeg = () => {
            const m = new THREE.Mesh(new THREE.BoxGeometry(segLen * 2, 3.0, 0.7),
                new THREE.MeshLambertMaterial({ color: sk.wall }));
            m.position.y = 1.5; grp.add(m); return m;
        };
        const segL = mkSeg(), segR = mkSeg();
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0, fire: false, sweepAmp, sweepPhase: Math.random() * 6.28,
            sweepFreq: 1.1, gapWall: { gapHalf, segL, segR },
        };
        this.floorTell(0, sk.accent, gapHalf);
        this.placeEntity(e); this.entities.push(e);
    }

    /** G4 千鳥柱: 左右にずれた柱を縫うように抜けるスラローム。 */
    private spawnZigzag(): void {
        const off = LANE * 0.45;
        const xs = [-off, off, -off];
        for (let i = 0; i < 3; i++) this.addPillar(xs[i], SPAWN_Z + i * 6);
    }
    private addPillar(x: number, z: number): void {
        const sk = this.skin;
        const grp = new THREE.Group();
        const pillar = new THREE.Mesh(new THREE.CylinderGeometry(0.45, 0.55, 2.8, 8),
            new THREE.MeshLambertMaterial({ color: sk.spikeBase }));
        pillar.position.y = 1.4; grp.add(pillar);
        const cap = new THREE.Mesh(new THREE.SphereGeometry(0.5, 8, 6),
            new THREE.MeshLambertMaterial({ color: sk.spike }));
        cap.position.y = 2.8; grp.add(cap);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z, wx: x, resolved: false,
            halfW: 0.5, fire: false, sweepAmp: 0, sweepPhase: 0,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /** 走査する光の壁: レーン中央を覆う光のバリアが左右に往復。開いた側へ寄せて避ける。 */
    private spawnMover(): void {
        const halfW = LANE * 0.55; // 約 2.4 (両端に隙間が残る幅)
        const grp = new THREE.Group();
        // 光るパネル (半透明) ＋ 横ビーム束で「エネルギー壁」感を出す
        const panel = new THREE.Mesh(
            new THREE.BoxGeometry(halfW * 2, 2.8, 0.18),
            new THREE.MeshBasicMaterial({ color: 0x8a4dff, transparent: true, opacity: 0.32 }),
        );
        panel.position.y = 1.4;
        grp.add(panel);
        for (let i = 0; i < 4; i++) {
            const beam = new THREE.Mesh(
                new THREE.BoxGeometry(halfW * 2, 0.12, 0.26),
                new THREE.MeshBasicMaterial({ color: i % 2 ? 0xc89bff : 0x6fd2ff, transparent: true, opacity: 0.9 }),
            );
            beam.position.y = 0.5 + i * 0.7;
            grp.add(beam);
        }
        // 上下のエミッタ柱
        for (const dx of [-halfW, halfW]) {
            const post = new THREE.Mesh(
                new THREE.CylinderGeometry(0.14, 0.14, 2.9, 8),
                new THREE.MeshBasicMaterial({ color: this.skin.accent }),
            );
            post.position.set(dx, 1.45, 0);
            grp.add(post);
        }
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false, halfW,
            fire: false, sweepAmp: LANE - halfW - 0.1, sweepPhase: Math.random() * 6.28,
        };
        this.placeEntity(e);
        this.entities.push(e);
    }

    private spawnItem(): void {
        // 武器は「まだ持っていない次の段階」だけ提案する (取得後に同じ銃が何度も流れる
        // 違和感を解消)。ピストル→ガン→ミニガンの一方通行。ロボは別軸の強化。
        // 強武器ほど後のステージで解禁: ガンは序盤から / ミニガンは S3+ / ロボは S5+。
        // 解禁前は壊れ性能が早く出ないようアーマーを補給に回す。
        const kinds: ItemKind[] = [];
        if (!this.ownedWeapons.has(1)) kinds.push("gun"); // ライフル未所持なら提案
        else if (!this.ownedWeapons.has(2) && this.stage >= 3) kinds.push("minigun"); // ミニガン未所持
        if (this.stage >= 5 && !this.ownedWeapons.has(ROCKET_WEAPON)) kinds.push("rocket"); // S5+ ロケキャノン
        if (this.stage >= 6 && !this.ownedWeapons.has(HOMING_WEAPON)) kinds.push("homing"); // S6+ 追尾ミサイル (飛行/シールド敵の答え)
        if (this.stage >= 6) kinds.push("turret"); // S6+ 360°タレット (背後/周回の答え・再取得で耐久補給)
        if (this.stage >= 7 && this.drones < DRONE_MAX) kinds.push("drones"); // S7+ 周回ガンドローン (+1 台)
        if (this.stage >= 5) kinds.push("robot");
        if (this.stage >= 2) kinds.push("shield"); // S2 から: 被弾を完全ブロックするバリア
        if (this.stage >= 7 && this.cannonStock < CANNON_MAX_STOCK) kinds.push("cannon"); // S7 から: ストック制波動砲
        if (this.stage >= 8 && !this.unlockedArmors.has("aegis") && Math.random() < 0.25) kinds.push("rarearmor"); // 終盤の激レア: 黄金のイージスを解放する特殊ドロップ
        if (this.stage >= 10 && !this.unlockedArmors.has("party") && Math.random() < 0.18) kinds.push("partyarmor"); // 最終盤の超激レア: パーティゴアアーマーを解放
        if (kinds.length === 0) kinds.push("armor"); // 次の強化が未解禁の序盤・武器最大化後はアーマー
        const item = kinds[Math.floor(Math.random() * kinds.length)];
        // 耐久 tier: 報酬が強いほど固い。たまに爆発する樽 (リスク&リワード)。
        let stand: StandTier = item === "robot" || item === "cannon" || item === "rocket" || item === "homing" || item === "turret" || item === "drones" || item === "rarearmor" || item === "partyarmor" ? "vault" : item === "minigun" ? "steel" : "crate";
        if (Math.random() < 0.18) stand = "barrel";
        const x = this.laneX();
        const meta = ITEM_META[item];
        const grp = new THREE.Group();
        const { box, halfW, topY } = this.buildStand(stand);
        grp.add(box);

        // アイテム本体 (本物の3Dモデル)＋光るリング＋ラベル＋HP バー
        const model = buildItemModel(item);
        model.position.y = topY + 0.5;
        grp.add(model);
        const ring = new THREE.Mesh(
            new THREE.TorusGeometry(0.7, 0.06, 8, 20),
            new THREE.MeshBasicMaterial({ color: meta.color }),
        );
        ring.rotation.x = Math.PI / 2;
        ring.position.y = topY - 0.2;
        grp.add(ring);
        const lbl = new THREE.Sprite(
            new THREE.SpriteMaterial({ map: makeLabelTexture(meta.label, { fill: "#ffffff", size: 46 }), transparent: true }),
        );
        lbl.scale.set(1.3, 0.65, 1);
        lbl.position.set(0, topY + 1.5, 0);
        grp.add(lbl);
        const bar = this.makeBarSprite();
        bar.position.set(0, topY + 0.95, 0);
        grp.add(bar);
        this.scene.add(grp);

        const hp = this.standHp(stand);
        const e: ItemEnt = {
            type: "item", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            item, stand, hp, max: hp, bar, halfW, spin: model, opened: false,
        };
        this.updateBar(e);
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** G5 増援クレート: 撃ち落とすとクルーが増える緑のクレート (報酬・触れると直撃ロスト)。 */
    private spawnBonusCrate(): void {
        const x = this.laneX();
        const bonus = Math.ceil(this.crew * 0.25) + 5;
        const hp = Math.ceil((6 + this.diff * 6) * this.stageMul); // shootable in time at normal tiers
        const grp = new THREE.Group();
        const box = new THREE.Mesh(new THREE.BoxGeometry(1.2, 1.2, 1.2),
            new THREE.MeshLambertMaterial({ color: 0x2fae5a }));
        box.position.y = 0.9; grp.add(box);
        for (const e2 of [-0.6, 0.6]) {
            // emissive edge bars
            const bar2 = new THREE.Mesh(new THREE.BoxGeometry(0.1, 1.3, 0.1),
                new THREE.MeshBasicMaterial({ color: 0x7bffb0 }));
            bar2.position.set(e2, 0.9, 0.6); grp.add(bar2);
        }
        const lbl = new THREE.Sprite(new THREE.SpriteMaterial({
            map: makeLabelTexture("+" + bonus, { fill: "#eaffea", size: 56 }), transparent: true }));
        lbl.scale.set(1.8, 0.9, 1); lbl.position.set(0, 2.1, 0); grp.add(lbl);
        const bar = this.makeBarSprite(); bar.position.set(0, 1.7, 0); grp.add(bar);
        this.scene.add(grp);
        const spin = box;
        const e: ItemEnt = {
            type: "item", item: "bonus", stand: "crate", obj: grp, z: SPAWN_Z, wx: x,
            resolved: false, hp, max: hp, bar, halfW: 0.7, spin, opened: false, bonus,
        };
        this.updateBar(e); this.placeEntity(e); this.entities.push(e);
    }

    /** G6 強化オーブ: 撃ち落とすと火力/クルーがブーストされる青いオーブ。 */
    private spawnHealOrb(): void {
        const x = this.laneX();
        const bonus = Math.ceil(this.crew * 0.18) + 4;
        const hp = Math.ceil((7 + this.diff * 7) * this.stageMul);
        const grp = new THREE.Group();
        const orb = new THREE.Mesh(new THREE.SphereGeometry(0.7, 14, 10),
            new THREE.MeshBasicMaterial({ color: 0x6fc8ff, transparent: true, opacity: 0.7 }));
        orb.position.y = 1.1; grp.add(orb);
        const core = new THREE.Mesh(new THREE.SphereGeometry(0.32, 10, 8),
            new THREE.MeshBasicMaterial({ color: 0xeaffff }));
        core.position.y = 1.1; grp.add(core);
        const bar = this.makeBarSprite(); bar.position.set(0, 1.9, 0); grp.add(bar);
        this.scene.add(grp);
        const e: ItemEnt = {
            type: "item", item: "heal", stand: "crate", obj: grp, z: SPAWN_Z, wx: x,
            resolved: false, hp, max: hp, bar, halfW: 0.7, spin: orb, opened: false, bonus,
        };
        this.projGlow(orb, 0x6fc8ff, 1.6); // pooled glow, reused
        this.updateBar(e); this.placeEntity(e); this.entities.push(e);
    }

    /** G7 砂地獄 (砂漠): 中央へ吸い寄せる砂地。逆へ舵を切れば必ず抜けられる (キルなし)。 */
    private spawnQuicksand(): void {
        const patch = new THREE.Mesh(new THREE.CircleGeometry(2.2, 20),
            new THREE.MeshLambertMaterial({ color: 0xc2a060 }));
        patch.rotation.x = -Math.PI / 2; patch.position.y = 0.04;
        this.scene.add(patch);
        const e: HazardEnt = {
            type: "hazard", obj: patch, z: SPAWN_Z, wx: this.laneX(), resolved: false,
            halfW: 2.2, fire: false, sweepAmp: 0, sweepPhase: 0, pull: 2.5,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /** G8 氷の滑走 (雪): 横移動に慣性が乗る氷面。曲がりは滑るので先読みする (キルなし)。 */
    private spawnIceSlick(): void {
        const slick = new THREE.Mesh(new THREE.PlaneGeometry(LANE * 1.6, 5),
            new THREE.MeshBasicMaterial({ color: 0xbfeaff, transparent: true, opacity: 0.4 }));
        slick.rotation.x = -Math.PI / 2; slick.position.y = 0.03;
        this.scene.add(slick);
        const e: HazardEnt = {
            type: "hazard", obj: slick, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: LANE * 0.8, fire: false, sweepAmp: 0, sweepPhase: 0, drift: 1.6,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * G9 電流フェンス: レーンの片端から電撃帯が周期的に伸び縮みする。伸びている間は帯に居ると被弾、
     * 縮んだ隙に通り抜ける (タイミング避け)。論理的な当たり幅 halfW を周期で変える (mesh は判定に書き戻さない)。
     */
    private spawnArcFence(): void {
        const sk = this.skin;
        const side = Math.random() < 0.5 ? -1 : 1; // 電撃が伸びてくる側
        const minHalf = 0.4; // 縮んだとき (この幅以下は安全に通れる)
        const maxHalf = LANE - (this.crowdHalfW + 0.5); // 伸びても反対端に必ず安全地帯が残る
        const grp = new THREE.Group();
        // 端の発電ポスト (電撃の根本)
        const post = new THREE.Mesh(new THREE.BoxGeometry(0.3, 3.0, 0.4),
            new THREE.MeshLambertMaterial({ color: 0x4a4f59 }));
        post.position.set(side * LANE, 1.5, 0); grp.add(post);
        const knob = new THREE.Mesh(new THREE.SphereGeometry(0.32, 10, 8),
            new THREE.MeshBasicMaterial({ color: sk.accent }));
        knob.position.set(side * LANE, 3.0, 0); grp.add(knob);
        this.projGlow(knob, sk.accent, 2.0);
        // 伸縮する電撃帯 (bars を scale.x で伸縮 / 中心を端へ寄せて片側から伸びるように pivot を組む)
        const bars = new THREE.Object3D();
        bars.position.set(side * LANE, 0, 0); // 根本=端。-side 方向へ伸びる
        grp.add(bars);
        const fieldMat = new THREE.MeshBasicMaterial({
            color: sk.accent, transparent: true, opacity: 0.4, blending: THREE.AdditiveBlending, depthWrite: false,
        });
        const field = new THREE.Mesh(new THREE.BoxGeometry(1, 2.4, 0.5), fieldMat);
        field.position.set(-side * 0.5, 1.4, 0); // 単位長 1 を内向きに置く (scale.x で全長を作る)
        bars.add(field);
        for (let i = 0; i < 3; i++) {
            const wire = new THREE.Mesh(new THREE.BoxGeometry(1, 0.08, 0.08),
                new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.8, blending: THREE.AdditiveBlending, depthWrite: false }));
            wire.position.set(-side * 0.5, 0.7 + i * 0.8, 0);
            bars.add(wire);
        }
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0, fire: false, sweepAmp: 0, sweepPhase: 0,
            arc: { side, minHalf, maxHalf, freq: 1.6 + Math.random() * 0.4, phase: Math.random() * 6.28, bars },
        };
        this.floorTell(-side * (LANE * 0.6), sk.accent, this.crowdHalfW + 0.4); // 反対端=安全地帯を予告
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * G10 崩落床 (落とし穴): 前方の床区画が抜ける。z 接近で蓋が開き、踏むとロスト。
     * 撃って耐久 (armorHp) を削り切ると足場が固まって安全に渡れる / もしくは横へずれて開口を避ける。
     */
    private spawnCollapseFloor(): void {
        const sk = this.skin;
        const x = this.laneX();
        const halfW = 1.5; // 開口の横半幅 (両脇に安全地帯が残る)
        const armorMax = Math.ceil((10 + this.diff * 10) * this.stageMul); // 撃って固められる耐久
        const grp = new THREE.Group();
        // 落とし穴の縁 (赤黒の警告枠)
        const ring = new THREE.Mesh(new THREE.RingGeometry(halfW * 0.8, halfW + 0.3, 4, 1),
            new THREE.MeshBasicMaterial({ color: 0xff5a3a, transparent: true, opacity: 0.7, side: THREE.DoubleSide }));
        ring.rotation.x = -Math.PI / 2; ring.position.y = 0.07; grp.add(ring);
        // 開閉する蓋 (lid を下げると穴が開く)
        const lid = new THREE.Object3D(); grp.add(lid);
        const plate = new THREE.Mesh(new THREE.BoxGeometry(halfW * 2, 0.2, 2.4),
            new THREE.MeshLambertMaterial({ color: sk.spikeBase }));
        plate.position.y = 0.1; lid.add(plate);
        // 補強リベット (固めたときの安全感)
        for (const dx of [-halfW * 0.6, halfW * 0.6]) {
            const rv = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 0.24, 6),
                new THREE.MeshLambertMaterial({ color: sk.spike }));
            rv.position.set(dx, 0.22, 0); lid.add(rv);
        }
        const bar = this.makeBarSprite(); bar.position.set(0, 1.4, 0); grp.add(bar);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            halfW, fire: false, sweepAmp: 0, sweepPhase: 0,
            pit: { open: false, lid, openAt: CROWD_Z + 8, armorHp: armorMax, armorMax },
        };
        // 撃って固められる足場として撃てる対象にするため bar を表示しておく
        bar.scale.x = 1.6; bar.visible = true;
        (e as HazardEnt & { _pitBar?: THREE.Sprite })._pitBar = bar;
        this.floorTell(x, 0xff5a3a, halfW); // 開口位置を予告
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * G11 火力テンプレ強化ピックアップ: 撃ち落とすと一定時間 firepower が倍化するパネル。
     * 触れると逆に損失。spawnHealOrb / spawnBonusCrate の「撃って取る台」作法に準拠 (item="weapon")。
     */
    private spawnWeaponBoost(): void {
        const x = this.laneX();
        const hp = Math.ceil((8 + this.diff * 8) * this.stageMul);
        const grp = new THREE.Group();
        // 浮かぶ弾薬テンプレ (橙の箱 + 弾マーク)
        const box = new THREE.Mesh(new THREE.BoxGeometry(1.1, 0.9, 1.1),
            new THREE.MeshLambertMaterial({ color: 0xd9772b }));
        box.position.y = 1.1; grp.add(box);
        for (const dx of [-0.28, 0, 0.28]) {
            const round = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 0.6, 8),
                new THREE.MeshBasicMaterial({ color: 0xffd23b }));
            round.position.set(dx, 1.55, 0); grp.add(round);
        }
        const lbl = new THREE.Sprite(new THREE.SpriteMaterial({
            map: makeLabelTexture("⚡×2", { fill: "#ffe066", size: 52 }), transparent: true }));
        lbl.scale.set(1.7, 0.85, 1); lbl.position.set(0, 2.1, 0); grp.add(lbl);
        const bar = this.makeBarSprite(); bar.position.set(0, 1.8, 0); grp.add(bar);
        this.scene.add(grp);
        this.projGlow(box, 0xffae3b, 1.6);
        const e: ItemEnt = {
            type: "item", item: "weapon", stand: "crate", obj: grp, z: SPAWN_Z, wx: x,
            resolved: false, hp, max: hp, bar, halfW: 0.65, spin: box, opened: false,
        };
        this.updateBar(e); this.placeEntity(e); this.entities.push(e);
    }

    /**
     * A 巨大転がり球 (インディ・ジョーンズ風): 中央帯を占める大球が転がって迫る。
     * 両サイドに逃げ場が残るので、クルーは左右どちらへ流れても避けられる (脱二択)。
     */
    private spawnBoulder(): void {
        const R = 1.6 + Math.random() * 0.4; // 半径 1.6〜2.0
        const x = (Math.random() * 2 - 1) * 1.0; // レーン中央付近 (±1.0)
        const grp = new THREE.Group();
        // 転がる本体 (岩肌は IcosahedronGeometry の面で表現)
        const ball = new THREE.Mesh(new THREE.IcosahedronGeometry(R, 1),
            new THREE.MeshLambertMaterial({ color: 0x6e5a47, flatShading: true }));
        ball.position.y = R; grp.add(ball);
        // まだら模様 (濃い岩塊を貼って質感を出す)
        for (let i = 0; i < 5; i++) {
            const spot = new THREE.Mesh(new THREE.IcosahedronGeometry(R * 0.32, 0),
                new THREE.MeshLambertMaterial({ color: 0x4f4136, flatShading: true }));
            const a = Math.random() * 6.28, b = Math.random() * 3.14;
            spot.position.set(Math.sin(b) * Math.cos(a) * R, Math.sin(b) * Math.sin(a) * R, Math.cos(b) * R);
            ball.add(spot);
        }
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            halfW: R, fire: false, sweepAmp: 0, sweepPhase: 0,
            boulder: ball, wobblePhase: Math.random() * 6.28,
        };
        this.floorTell(x, 0x8a6a4a, R); // 中央帯=危険を予告 (両端が安全)
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * B ワープゲート: レーン全幅の発光リング。通過するとラン進捗が前後にずれる (キルなし)。
     * 緑=前進(報酬) / 赤=逆流(罠) を色で予告するので盲目の二択にならない。進捗のナッジのみで死は無い。
     */
    private spawnWarpGate(): void {
        const forward = Math.random() < 0.6; // 緑(前進)が 6 割
        const dir: 1 | -1 = forward ? 1 : -1;
        const col = forward ? 0x4cff7a : 0xff5a5a;
        const grp = new THREE.Group();
        // レーン全幅をまたぐリング (縦に立てて通過させる)
        const ring = new THREE.Mesh(new THREE.TorusGeometry(LANE, 0.32, 12, 40),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.85 }));
        ring.position.y = LANE * 0.5; grp.add(ring);
        this.projGlow(ring, col, 3.0);
        // 内側の渦 (薄膜・通過の合図)
        const film = new THREE.Mesh(new THREE.CircleGeometry(LANE - 0.3, 32),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.18, side: THREE.DoubleSide, blending: THREE.AdditiveBlending, depthWrite: false }));
        film.position.y = LANE * 0.5; grp.add(film);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: LANE, fire: false, sweepAmp: 0, sweepPhase: 0,
            warp: { dir, amount: 24 + Math.random() * 16, done: false }, gate: ring,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * C 操作反転ゾーン: 前線に着くと一定時間だけ左右操作が反転する床帯。
     * 紫のフォグ帯で接近を予告 → 通過で invertT を起動。位置の自由は残る (鏡像化のみ) ので脱二択。
     */
    private spawnInvertZone(): void {
        const col = 0xc98bff;
        const grp = new THREE.Group();
        const band = new THREE.Mesh(new THREE.BoxGeometry(LANE * 2, 0.1, 3.0),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.32, depthWrite: false }));
        band.position.y = 0.06; grp.add(band);
        // 立ち上るフォグ柱 (帯の存在を高く見せる)
        const haze = new THREE.Mesh(new THREE.BoxGeometry(LANE * 2, 3.4, 0.6),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.16, blending: THREE.AdditiveBlending, depthWrite: false }));
        haze.position.y = 1.7; grp.add(haze);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0, fire: false, sweepAmp: 0, sweepPhase: 0, zone: "invert",
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * D 重力反転ゾーン: 前線に着くと一定時間だけ世界が上下反転する床帯。
     * 水色のフォグ帯で接近を予告 → 通過で gravT を起動。横移動の自由は残る撹乱モディファイア (脱二択)。
     */
    private spawnGravityZone(): void {
        const col = 0x7be0ff;
        const grp = new THREE.Group();
        const band = new THREE.Mesh(new THREE.BoxGeometry(LANE * 2, 0.1, 3.0),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.32, depthWrite: false }));
        band.position.y = 0.06; grp.add(band);
        const haze = new THREE.Mesh(new THREE.BoxGeometry(LANE * 2, 3.4, 0.6),
            new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.16, blending: THREE.AdditiveBlending, depthWrite: false }));
        haze.position.y = 1.7; grp.add(haze);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: 0, resolved: false,
            halfW: 0, fire: false, sweepAmp: 0, sweepPhase: 0, zone: "grav",
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * D 多腕の徘徊体 (脱二択中ボス): 低速・高 HP で迫り、hold まで来たら居座って攻撃を回す。
     * 「薙ぎ払い(横スイープ帯)」と「叩きつけ(縦 AoE 円)」を非対称タイミングで交互に重ね、
     * 合間に小型 bacteria を散発スポーン。HP 閾値でフェーズが上がり攻撃間隔が詰まる。HP バー必須。
     */
    private spawnStalker(): void {
        const sk = this.skin;
        const x = (Math.random() * 2 - 1) * (LANE * 0.3);
        const hp = Math.ceil((180 + this.crew * 2.0) * this.stageMul);
        const grp = new THREE.Group();
        // 本体 (黒い胴 + 赤い単眼)
        const body = new THREE.Mesh(new THREE.CylinderGeometry(1.0, 1.4, 3.4, 10),
            new THREE.MeshLambertMaterial({ color: 0x2a2230 }));
        body.position.y = 1.9; grp.add(body);
        const eye = new THREE.Mesh(new THREE.SphereGeometry(0.4, 12, 10),
            new THREE.MeshBasicMaterial({ color: 0xff3a3a }));
        eye.position.set(0, 2.6, 1.0); grp.add(eye);
        this.projGlow(eye, 0xff4a4a, 2.2);
        // 多腕 (左右非対称に 4 本・update で振り回す純視覚)
        const arms: THREE.Object3D[] = [];
        const armConf: [number, number][] = [[-1, 1.9], [1, 2.3], [-1, 1.3], [1, 1.6]];
        for (const [s, y] of armConf) {
            const pivot = new THREE.Object3D();
            pivot.position.set(s * 0.9, y, 0.2); grp.add(pivot);
            const arm = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.1, 2.4, 6),
                new THREE.MeshLambertMaterial({ color: 0x4a3a4a }));
            arm.position.set(s * 1.0, 0, 0); arm.rotation.z = s * 1.2; pivot.add(arm);
            const claw = new THREE.Mesh(new THREE.ConeGeometry(0.22, 0.6, 5),
                new THREE.MeshLambertMaterial({ color: sk.spike }));
            claw.position.set(s * 2.0, 0, 0); claw.rotation.z = -s * Math.PI / 2; pivot.add(claw);
            arms.push(pivot);
        }
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 4.6, 0); grp.add(bar);
        // 攻撃予告メッシュ (床)。普段は隠し、予告中だけ見せる。
        const sweepTell = new THREE.Mesh(new THREE.PlaneGeometry(LANE * 2, 1.0),
            new THREE.MeshBasicMaterial({ color: 0xff7a3a, transparent: true, opacity: 0, side: THREE.DoubleSide }));
        sweepTell.rotation.x = -Math.PI / 2; sweepTell.position.y = 0.06; grp.add(sweepTell);
        const slamTell = new THREE.Mesh(new THREE.RingGeometry(1.2, 1.7, 24),
            new THREE.MeshBasicMaterial({ color: 0xffd23b, transparent: true, opacity: 0, side: THREE.DoubleSide }));
        slamTell.rotation.x = -Math.PI / 2; slamTell.position.y = 0.07; grp.add(slamTell);
        this.scene.add(grp);
        const e: BruiserEnt = {
            type: "bruiser", variant: "stalker", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            hp, max: hp, bar, halfW: 1.5, speedMul: 0.4,
            stalk: {
                hold: CROWD_Z + 11, phase: 0, atkT: 1.4, seq: 0,
                sweepT: 0, sweepDir: 1, sweepTell,
                slamT: 0, slamX: 0, slamTell,
                arms,
            },
        };
        this.updateBar(e);
        this.placeEntity(e); this.entities.push(e);
        this.showBanner("⚔ 多腕の徘徊体", "薙ぎ払いと叩きつけが交互に来る・撃ち込んで押し切れ");
    }

    /**
     * D 多腕の徘徊体の攻撃ステートマシン (脱二択)。腕を振り回しつつ、薙ぎ払い(横帯)/叩きつけ(縦 AoE)/
     * 雑魚散布を非対称に時間差で重ねる。HP 閾値でフェーズが上がり攻撃間隔が詰まる。当たり判定は論理座標のみ。
     */
    private updateStalker(e: BruiserEnt, dt: number): void {
        const st = e.stalk;
        if (!st) return;
        const t = this.clock.elapsedTime;
        // 腕を振り回す (純視覚)。左右で逆位相にして非対称な動きに見せる。
        for (let i = 0; i < st.arms.length; i++) {
            const dir = i % 2 === 0 ? -1 : 1;
            st.arms[i].rotation.z = dir * (0.6 + 0.5 * Math.sin(t * (3 + i) + i)) ;
            st.arms[i].rotation.x = 0.3 * Math.sin(t * 2.4 + i);
        }
        // フェーズ遷移 (HP 66% / 33% で攻撃頻度が上がる)。一方向のみ。
        const ratio = e.hp / e.max;
        const want: 0 | 1 | 2 = ratio < 0.34 ? 2 : ratio < 0.67 ? 1 : 0;
        if (want > st.phase) {
            st.phase = want;
            this.impactBurst(e.obj.position.clone().setY(2.0), "dragon", 0.8);
            this.shake = Math.max(this.shake, 0.3);
            this.popWorld(st.phase === 2 ? "激昂！" : "怒り！", e.wx, "#ff6a6a");
        }
        // hold まで来るまでは攻撃しない (まだ迫っている最中)。
        if (e.z > st.hold + 0.5) return;

        // --- 進行中の予告 → 発動の処理 ---
        if (st.sweepT > 0) {
            // 薙ぎ払い予告: 床の帯を点滅させ、時間切れで全幅をスイープ kill。逆端へ逃げる猶予がある。
            st.sweepT -= dt;
            const mat = (st.sweepTell as THREE.Mesh).material as THREE.MeshBasicMaterial;
            mat.opacity = 0.3 + 0.4 * Math.abs(Math.sin(t * 12));
            // 帯は徘徊体の正面・レーン中央付近 (z はクルー前線寄り)
            st.sweepTell.position.set(0, 0.06, CROWD_Z - e.obj.position.z); // grp ローカルで CROWD_Z に置く
            if (st.sweepT <= 0) {
                mat.opacity = 0;
                // 薙ぎ払い発動: sweepDir 側から薙ぎ、反対端に狭い安全地帯を残す (そこへ逃げれば助かる)。
                const safeHalf = this.crowdHalfW + 0.4; // 反対端に残す安全幅
                const killBoundary = -st.sweepDir * LANE + st.sweepDir * (safeHalf * 2); // 安全端の内側境界
                const cx = (st.sweepDir * LANE + killBoundary) / 2; // 薙ぐ帯の中心
                const half = Math.abs(st.sweepDir * LANE - killBoundary) / 2;
                this.shockRing(new THREE.Vector3(cx, 0.9, CROWD_Z), 0xff7a3a, 5, 0.35);
                this.shake = Math.max(this.shake, 0.35);
                this.collideCrowd(cx, half);
            }
            return; // 予告/発動フレームは新規攻撃を選ばない
        }
        if (st.slamT > 0) {
            // 叩きつけ予告: 着弾点にリングを出し、時間切れで縦 AoE。円から外れていれば無傷。
            st.slamT -= dt;
            const mat = (st.slamTell as THREE.Mesh).material as THREE.MeshBasicMaterial;
            mat.opacity = 0.4 + 0.4 * Math.abs(Math.sin(t * 14));
            st.slamTell.position.set(st.slamX - e.obj.position.x, 0.07, CROWD_Z - e.obj.position.z);
            if (st.slamT <= 0) {
                mat.opacity = 0;
                this.boom(new THREE.Vector3(st.slamX, 1.0, CROWD_Z));
                this.shake = Math.max(this.shake, 0.45);
                this.collideCrowd(st.slamX, 1.7); // AoE 半径
            }
            return;
        }

        // --- 次の攻撃を選ぶ (クールダウン) ---
        st.atkT -= dt;
        if (st.atkT > 0) return;
        // フェーズが上がるほど間隔が詰まる
        const cd = (st.phase === 2 ? 1.1 : st.phase === 1 ? 1.6 : 2.2);
        st.atkT = cd;
        // 非対称シーケンス: 左薙ぎ → 叩き(現在地) → 右薙ぎ → 雑魚散布 → 叩き(ランダム) … と回す。
        const step = st.seq % 5;
        st.seq++;
        if (step === 0 || step === 2) {
            // 薙ぎ払い予告開始 (step0=右から / step2=左から)
            st.sweepDir = step === 0 ? 1 : -1;
            st.sweepT = st.phase === 2 ? 0.7 : 1.0;
            const mat = (st.sweepTell as THREE.Mesh).material as THREE.MeshBasicMaterial;
            mat.color.setHex(0xff7a3a); mat.opacity = 0.4;
        } else if (step === 1 || step === 4) {
            // 叩きつけ予告開始 (step1=現在地ロック / step4=ランダム)
            st.slamX = step === 1
                ? THREE.MathUtils.clamp(this.centroidX, -(LANE - 1), LANE - 1)
                : (Math.random() * 2 - 1) * (LANE - 1);
            st.slamT = st.phase === 2 ? 0.7 : 1.0;
            const mat = (st.slamTell as THREE.Mesh).material as THREE.MeshBasicMaterial;
            mat.color.setHex(0xffd23b); mat.opacity = 0.4;
        } else {
            // 雑魚散布: 小型 bacteria を 1〜2 体散らす (薙ぎ/叩きと重なって読みづらくする)
            this.spawnBacteria(true);
            if (st.phase >= 1) this.spawnBacteria(true);
            this.popWorld("召喚", e.wx, "#c59a98");
        }
    }

    /** 落下プレス/隕石: 床に標的シャドウを出し、塊が空から落ちてきて広範囲を叩き潰す (AoE)。円から逃げる。 */
    private spawnSlam(): void {
        const x = this.laneX();
        const R = 1.6;
        const grp = new THREE.Group();
        // 床の標的シャドウ (危険円)
        const shadow = new THREE.Mesh(new THREE.CircleGeometry(R, 22),
            new THREE.MeshBasicMaterial({ color: this.skin.hazardCol, transparent: true, opacity: 0.4 }));
        shadow.rotation.x = -Math.PI / 2; shadow.position.y = 0.05;
        grp.add(shadow);
        const ring = new THREE.Mesh(new THREE.TorusGeometry(R, 0.08, 8, 24),
            new THREE.MeshBasicMaterial({ color: this.skin.hazardCol }));
        ring.rotation.x = -Math.PI / 2; ring.position.y = 0.06;
        grp.add(ring);
        // 落ちてくる塊 (lift の子で y を下げる・placeEntity の y リセットを回避)
        const lift = new THREE.Object3D(); lift.position.y = 8; grp.add(lift);
        const block = new THREE.Mesh(new THREE.IcosahedronGeometry(R * 0.7, 0),
            new THREE.MeshLambertMaterial({ color: this.skin.spikeBase }));
        lift.add(block);
        const cap = new THREE.Mesh(new THREE.IcosahedronGeometry(R * 0.4, 0),
            new THREE.MeshBasicMaterial({ color: this.skin.hazardCol }));
        cap.position.y = R * 0.5; lift.add(cap);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            halfW: R, fire: false, sweepAmp: 0, sweepPhase: 0, slam: lift,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /**
     * 頭上隕石: 前方からでなく真上から降ってくる。床に赤い標的レティクルが出て、塊が落下して着弾 AoE。
     * 前面の遮蔽 (波動砲/シールド) を貫通して後方クルーを直撃するので、横へ走って標的から逃げる。
     */
    private spawnSkyMeteor(targetX?: number): void {
        const x = targetX ?? this.centroidX; // 現在地を狙う → 横へ逃げる
        const R = 1.4;
        const grp = new THREE.Group();
        const reticle = new THREE.Mesh(new THREE.RingGeometry(R * 0.85, R, 24),
            new THREE.MeshBasicMaterial({ color: 0xff5a3a, transparent: true, opacity: 0.85, side: THREE.DoubleSide }));
        reticle.rotation.x = -Math.PI / 2; reticle.position.y = 0.07;
        grp.add(reticle);
        const cross = new THREE.Mesh(new THREE.RingGeometry(0.05, R * 0.5, 4),
            new THREE.MeshBasicMaterial({ color: 0xff7a3a, transparent: true, opacity: 0.6, side: THREE.DoubleSide }));
        cross.rotation.x = -Math.PI / 2; cross.position.y = 0.07;
        grp.add(cross);
        const lift = new THREE.Object3D(); lift.position.y = 14; grp.add(lift);
        const rock = new THREE.Mesh(new THREE.IcosahedronGeometry(R * 0.6, 0), new THREE.MeshLambertMaterial({ color: 0x3a2a26 }));
        lift.add(rock);
        const fire = new THREE.Mesh(new THREE.ConeGeometry(R * 0.5, 1.6, 8), new THREE.MeshBasicMaterial({ color: 0xff7a2e, transparent: true, opacity: 0.85 }));
        fire.position.y = 1.0; lift.add(fire);
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: CROWD_Z, wx: x, resolved: false,
            halfW: R, fire: false, sweepAmp: 0, sweepPhase: 0, sky: { fuse: 1.6, block: lift },
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /** 頭上隕石の連弾: 現在地を中心に 3 発を撒く。レーン端へ逃げ切らないと後方クルーが一気に溶ける。 */
    private spawnMeteorBarrage(): void {
        const base = this.centroidX;
        for (const off of [-2.6, 0, 2.6]) {
            this.spawnSkyMeteor(THREE.MathUtils.clamp(base + off, -(LANE - 1), LANE - 1));
        }
    }

    /** 追尾オーブ: ゆっくり接近しつつプレイヤーの横位置へ寄ってくる弾。撃ち落とすか、決め打ちで横へ振り切る。 */
    private spawnHomingOrb(): void {
        const x = this.laneX();
        const orb = new THREE.Mesh(new THREE.SphereGeometry(0.42, 14, 10),
            new THREE.MeshBasicMaterial({ color: 0xa05cff, transparent: true, opacity: 0.85 }));
        orb.position.set(x, 1.1, SPAWN_Z);
        this.scene.add(orb);
        const glow = this.projGlow(orb, 0xb060ff, 1.8);
        const hp = Math.ceil((6 + this.diff * 8) * this.stageMul); // 撃ち落とし可能
        this.arrows.push({
            mesh: orb, wx: x, z: SPAWN_Z, resolved: false, halfW: 0.55, blast: false,
            hp, homing: true, slow: true, trailCol: 0xb060ff, glow,
        });
    }

    /** 竜巻: 回る漏斗。近づくと中心へ強く引き寄せ、中心 (eye) に巻き込まれた列だけ消える。逆へ舵を切って抜ける。 */
    private spawnTornado(): void {
        const x = this.laneX();
        const grp = new THREE.Group();
        const col = this.biomeIdx === 2 ? 0xdfeaf4 : this.biomeIdx === 1 ? 0xd9b878 : 0x9aa6b8;
        for (let i = 0; i < 5; i++) {
            const r = 0.4 + i * 0.5;
            const ringMesh = new THREE.Mesh(new THREE.TorusGeometry(r, 0.12, 6, 16),
                new THREE.MeshBasicMaterial({ color: col, transparent: true, opacity: 0.5 }));
            ringMesh.rotation.x = Math.PI / 2;
            ringMesh.position.y = 0.3 + i * 0.9;
            ringMesh.userData.spin = 1 + i * 0.4;
            grp.add(ringMesh);
        }
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            halfW: 3.0, fire: false, sweepAmp: 0, sweepPhase: 0, pull: 3.4, eye: 0.7, // halfW=引き寄せ範囲 / eye=致死中心
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /** 台 (耐久 tier) の見た目と当たり幅・天面 y を作る。 */
    private buildStand(tier: StandTier): { box: THREE.Object3D; halfW: number; topY: number } {
        const g = new THREE.Group();
        if (tier === "barrel") {
            const barrel = new THREE.Mesh(
                new THREE.CylinderGeometry(0.62, 0.62, 1.4, 12),
                new THREE.MeshLambertMaterial({ color: 0xd9772b }),
            );
            barrel.position.y = 0.85;
            g.add(barrel);
            for (const yy of [0.55, 1.15]) {
                const ring = new THREE.Mesh(
                    new THREE.TorusGeometry(0.64, 0.06, 6, 16),
                    new THREE.MeshLambertMaterial({ color: 0x8a4a18 }),
                );
                ring.position.y = yy;
                ring.rotation.x = Math.PI / 2;
                g.add(ring);
            }
            const warn = new THREE.Sprite(
                new THREE.SpriteMaterial({ map: makeLabelTexture("⚠", { fill: "#ffd23b", size: 64 }), transparent: true }),
            );
            warn.scale.set(0.8, 0.8, 1);
            warn.position.set(0, 0.85, 0.66);
            g.add(warn);
            return { box: g, halfW: 0.66, topY: 1.55 };
        }
        const spec =
            tier === "crate"
                ? { w: 1.2, h: 1.2, face: 0x9a6b3a, frame: 0x6e4a24 }
                : tier === "steel"
                  ? { w: 1.25, h: 1.25, face: 0x8893a3, frame: 0x4f5763 }
                  : { w: 1.35, h: 1.45, face: 0x3a3f48, frame: 0xffd75e }; // vault
        const body = new THREE.Mesh(
            new THREE.BoxGeometry(spec.w, spec.h, spec.w),
            new THREE.MeshLambertMaterial({ color: spec.face }),
        );
        body.position.y = spec.h / 2;
        g.add(body);
        for (const yy of [0.06, spec.h - 0.06]) {
            const band = new THREE.Mesh(
                new THREE.BoxGeometry(spec.w + 0.04, 0.12, spec.w + 0.04),
                new THREE.MeshLambertMaterial({ color: spec.frame }),
            );
            band.position.y = yy;
            g.add(band);
        }
        return { box: g, halfW: spec.w / 2 + 0.08, topY: spec.h };
    }

    /** 台の耐久値。強い報酬ほど固い。序盤は楽に壊せる。 */
    private standHp(tier: StandTier): number {
        const base = (4 + this.crew * 0.5) * this.stageMul * (0.7 + this.diff * 0.4);
        const mult = tier === "crate" ? 0.7 : tier === "steel" ? 1.2 : tier === "vault" ? 1.9 : 0.55;
        return Math.ceil(base * mult);
    }

    private placeEntity(e: Entity): void {
        e.obj.position.set(e.type === "gate" ? 0 : e.wx, 0, e.z);
    }

    // ---------- 解決 ----------
    private resolveEntity(e: Entity, dt: number): void {
        if (e.type === "mob") {
            // 当たり幅は固定 (HP では変えない)。重なった列だけ消滅・避ければ無傷。
            if (!e.resolved && e.z <= CROWD_Z) {
                e.resolved = true;
                if (e.hp > 0) this.collideCrowd(e.wx, e.halfW);
            }
            if (!e.weak) this.updateBar(e);
            return;
        }
        if (e.type === "archer") {
            if (e.hp > 0 && e.z < SPAWN_Z * 0.8 && e.z > CROWD_Z + 4) {
                e.shootTimer -= dt;
                if (e.shootTimer <= 0) {
                    e.shootTimer = e.bomber ? 2.0 : e.spread ? 1.9 : 1.4;
                    if (e.bomber) this.fireBlast(e);
                    else if (e.spread) this.fireSpread(e);
                    else this.fireArrow(e);
                }
            }
            if (!e.resolved && e.z <= CROWD_Z) {
                e.resolved = true;
                if (e.hp > 0) this.collideCrowd(e.wx, 0.6); // 接触したら近くの列が消滅
            }
            this.updateBar(e);
            return;
        }
        if (e.type === "bruiser") {
            // flyer/orbiter は前面接触ではなく上空/周回からミサイルで攻撃するので、ここの直撃はスキップ。
            if (e.variant !== "flyer" && e.variant !== "orbiter" && !e.resolved && e.z <= CROWD_Z) {
                e.resolved = true;
                if (e.hp > 0) {
                    this.collideCrowd(e.wx, e.halfW); // 壊せず接触 → 幅ぶん直撃
                    if (e.variant === "giant" || e.variant === "dragon") this.boom(e.obj.position.clone().setY(1.5));
                }
            }
            this.updateBar(e);
            return;
        }
        if (e.type === "rocket") {
            if (!e.resolved && e.z <= CROWD_Z) {
                e.resolved = true;
                if (e.hp > 0) {
                    this.boom(e.obj.position.clone().setY(1.0));
                    this.collideCrowd(e.wx, e.halfW); // 着弾爆発
                }
            }
            this.updateBar(e);
            return;
        }
        if (e.type === "item") {
            // 破壊された → 中身入手して退場
            if (e.hp <= 0 && !e.opened) {
                e.opened = true;
                this.openStand(e);
                return;
            }
            if (!e.resolved && e.z <= CROWD_Z) {
                e.resolved = true;
                if (e.hp > 0) {
                    // 壊せずに突っ込んだ → 直撃で消滅。樽は爆発して広範囲。
                    if (e.stand === "barrel") {
                        this.boom(e.obj.position.clone().setY(1.0));
                        this.collideCrowd(e.wx, e.halfW + 0.6);
                    } else {
                        this.collideCrowd(e.wx, e.halfW);
                    }
                }
            }
            this.updateBar(e);
            return;
        }
        if (e.type === "hazard" && e.sky) return; // 頭上隕石は移動ブロックで完結 (resolve しない)
        if (e.resolved || e.z > CROWD_Z) return;
        e.resolved = true;

        if (e.type === "gate") {
            const op = this.centroidX < 0 ? e.leftOp : e.rightOp;
            const before = this.crew;
            // ゲートは最低 1 人を残す (両方が即死になる理不尽を防止)。死は敵/ボスのみ。
            this.crew = Math.max(1, applyGate(this.crew, op));
            this.syncMembers();
            this.popWorld(gateLabel(op), this.centroidX, this.crew >= before ? "#7bd6ff" : "#ff8f8f");
            return;
        }
        if (e.type === "hazard") {
            if (e.pull || e.drift) {
                if (e.z <= CROWD_Z) {
                    e.resolved = true;
                    // 竜巻: 中心 (eye) に巻き込まれた列だけ消える。砂/雪 (pull/drift) はキルなし。
                    if (e.eye && Math.abs(this.centroidX - e.wx) < e.eye) {
                        this.boom(new THREE.Vector3(e.wx, 1.0, CROWD_Z));
                        this.collideCrowd(e.wx, e.eye);
                    }
                }
                return; // G7/G8 はステア誘導のみ
            }
            if (e.crusher) {
                // G2: kill everything OUTSIDE [-gap, +gap]
                const k = clamp01(1 - e.z / SPAWN_Z);
                const gap = e.crusher.gap0 + (e.crusher.gap1 - e.crusher.gap0) * k;
                const fillHalf = (LANE - gap) / 2;
                this.collideCrowd(-(LANE + gap) / 2, fillHalf);
                this.collideCrowd((LANE + gap) / 2, fillHalf);
                return;
            }
            if (e.gapWall) {
                // G3: kill OUTSIDE [e.wx ± gapHalf]
                const gh = e.gapWall.gapHalf;
                const leftFillHi = e.wx - gh, rightFillLo = e.wx + gh;
                // left segment fills [-LANE, leftFillHi]; right fills [rightFillLo, LANE]
                const lCx = (-LANE + leftFillHi) / 2, lHalf = (leftFillHi + LANE) / 2;
                const rCx = (rightFillLo + LANE) / 2, rHalf = (LANE - rightFillLo) / 2;
                if (lHalf > 0.05) this.collideCrowd(lCx, lHalf);
                if (rHalf > 0.05) this.collideCrowd(rCx, rHalf);
                return;
            }
            if (e.slam) {
                // 落下プレス: 着弾点で爆発 (AoE)。円の外に逃げていれば無傷。
                this.boom(new THREE.Vector3(e.wx, 1.0, CROWD_Z));
                this.shake = Math.max(this.shake, 0.4);
                this.collideCrowd(e.wx, e.halfW);
                return;
            }
            if (e.pit) {
                // G10 崩落床: 固められた (armorHp<=0) なら踏んでも安全。開いたままなら開口に居た列がロスト。
                if (e.pit.armorHp > 0) this.collideCrowd(e.wx, e.halfW);
                return;
            }
            if (e.arc) {
                // G9 電流フェンス: 着弾時点の伸び (halfW) で帯に重なった列だけ被弾。縮みきっていれば無傷。
                if (e.halfW > e.arc.minHalf + 0.3) this.collideCrowd(e.wx, e.halfW);
                return;
            }
            if (e.boulder) {
                // A 巨大球: 中央帯 [e.wx ± halfW] に残っていた列だけ轢かれる。両サイドへ逃げていれば無傷。
                this.boom(new THREE.Vector3(e.wx, 1.0, CROWD_Z));
                this.shake = Math.max(this.shake, 0.5);
                this.collideCrowd(e.wx, e.halfW);
                return;
            }
            if (e.warp) {
                // B ワープゲート: 通過でラン進捗を前後にずらす (キルなし)。逆流はステージ開始より手前へは下げない。
                if (!e.warp.done) {
                    e.warp.done = true;
                    const floor = (this.stage - 1) * 26; // 現ステージ開始の安全床 (これ以下には戻さない)
                    this.distance = Math.max(floor, this.distance + e.warp.dir * e.warp.amount);
                    const col = e.warp.dir > 0 ? "#4cff7a" : "#ff8f8f";
                    this.popWorld(e.warp.dir > 0 ? "ワープ！" : "逆流…", this.centroidX, col);
                    this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), e.warp.dir > 0 ? 0x4cff7a : 0xff5a5a);
                    // 視覚ダッシュを起動 (背景スクロールだけが一気に流れる / 引き戻される)。距離ロジックには触れない。
                    this.warpDashT = 0.9;
                    this.warpDashDir = e.warp.dir;
                    this.shake = Math.max(this.shake, e.warp.dir > 0 ? 0.6 : 0.4); // 前進はより強く揺らす
                    // ズーム/引き戻しの閃光 (前進=緑 / 逆流=赤)
                    const flashCol = e.warp.dir > 0 ? 0x4cff7a : 0xff5a5a;
                    this.shockRing(new THREE.Vector3(this.centroidX, 1.0, CROWD_Z), flashCol, e.warp.dir > 0 ? 6 : 4, 0.4);
                    for (let s = 0; s < 6; s++) this.trailEmit(new THREE.Vector3((s - 2.5) * 1.2, 0.5 + Math.random() * 1.2, CROWD_Z + s * 2), flashCol);
                }
                e.z = -100; // 後始末で撤去
                return; // キルなし
            }
            if (e.zone) {
                // C/D ゾーン帯: 前線通過で一時モディファイアを起動 (キルなし・位置の自由は残る)。
                if (e.zone === "invert") {
                    this.invertT = 4.0;
                    this.popWorld("操作反転!", this.centroidX, "#c98bff");
                    this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), 0xc98bff);
                } else {
                    this.gravT = 5.0;
                    this.popWorld("重力反転!!", this.centroidX, "#7be0ff");
                    this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), 0x7be0ff);
                }
                this.shake = Math.max(this.shake, 0.4);
                e.z = -100; // 後始末で撤去
                return; // キルなし
            }
            if (e.turnWall) {
                // Level 0 ターン壁: 接近して接触した分だけ削れる (遠い間は予告のみ＝曲がる猶予)。通過でカメラスイング。
                if (e.z < CROWD_Z + 3) this.collideCrowd(e.wx, e.halfW);
                if (!e.turnWall.swung && e.z <= CROWD_Z) {
                    e.turnWall.swung = true;
                    this.turnSwingT = TURN_SWING_TIME;
                    this.turnSwingDir = e.turnWall.side;
                    this.shake = Math.max(this.shake, 0.35);
                }
                return;
            }
            // 触れた幅のクルーが消滅。炎の壁(fire)は太いので安全な隙間へ寄せて避ける。
            this.collideCrowd(e.wx, e.halfW); // G1 + legacy hazards
            return;
        }
    }

    /** 台を破壊して中身を入手。樽は破壊時にも爆発する。 */
    private openStand(e: ItemEnt): void {
        // 箱/樽の破壊そのものでコイン (中身は別途付与)
        this.award(e.stand === "vault" ? 80 : e.stand === "steel" ? 40 : e.stand === "barrel" ? 30 : 20, e.wx);
        if (e.stand === "barrel") {
            this.boom(e.obj.position.clone().setY(1.0));
            // 前線近くで壊すと爆風が味方を巻き込む (遠距離で撃ち抜くぶんには安全)
            if (e.z < CROWD_Z + 6 && Math.abs(this.centroidX - e.wx) <= e.halfW + 1.0) {
                this.collideCrowd(e.wx, e.halfW + 0.4);
            }
        }
        if (e.item === "weapon") {
            // G11 火力テンプレ: 一定時間 firepower を倍化 (タイマーで自動復帰)。
            this.fireBuffT = FIRE_BUFF_TIME;
            this.popWorld("⚡ 火力×2!", e.wx, "#ffe066");
            this.impactBurst(e.obj.position.clone().setY(1.0), "king", 0.7);
            this.spark(e.obj.position.clone().setY(1.2), ITEM_META.weapon.color);
            this.shake = Math.max(this.shake, 0.2);
            e.z = -100;
            return;
        }
        if (e.item === "bonus" || e.item === "heal") {
            const n = e.bonus ?? 5;
            if (e.item === "heal" && !this.ownedWeapons.has(1)) {
                this.grantWeapon(1); // boost = jumpstart firepower (未所持ならライフルを付与)
            }
            this.changeCrew(n);
            this.popWorld("+" + n, e.wx, e.item === "bonus" ? "#7bffb0" : "#bfe9ff");
            this.impactBurst(e.obj.position.clone().setY(1.0), "crew", 0.5);
            this.spark(e.obj.position.clone().setY(1.2), ITEM_META[e.item].color);
            e.z = -100;
            return; // exit; skip applyItem (no weapon swap path)
        }
        this.applyItem(e.item);
        this.popWorld(ITEM_META[e.item].label, e.wx, "#fff3b0");
        this.spark(e.obj.position.clone().setY(1.2), ITEM_META[e.item].color);
        this.sfx.play("pickup");
        e.z = -100; // 退場 (このフレームの後始末で撤去)
    }

    private applyItem(item: ItemKind): void {
        if (item === "gun") {
            this.grantWeapon(1); // gun → ライフル(1)
        } else if (item === "minigun") {
            this.grantWeapon(2); // ミニガン(2)
        } else if (item === "rocket") {
            this.grantWeapon(ROCKET_WEAPON); // ロケキャノン(3)
        } else if (item === "homing") {
            this.grantWeapon(HOMING_WEAPON); // 追尾ミサイル(5)
        } else if (item === "turret") {
            this.acquireTurret(); // 360°タレットを展開 / 耐久補給
        } else if (item === "drones") {
            this.grantDrone(); // 周回ガンドローン +1
        } else if (item === "armor") {
            // 上限まではアーマー、満タン後は増援 (クルー微増) に変換して無駄撃ちを防ぐ。
            if (this.armor < ARMOR_MAX) this.armor++;
            else this.changeCrew(Math.ceil(this.crew * 0.05) + 2);
        } else if (item === "rarearmor") {
            this.unlockArmor("aegis"); // 特殊レアドロップ → 黄金のイージス解放
        } else if (item === "partyarmor") {
            this.unlockArmor("party"); // 最終盤の超激レア → パーティゴアアーマー解放
        } else if (item === "shield") {
            this.shieldHits += SHIELD_GRANT; // 被弾完全ブロックを 3 回分チャージ
            this.sfx.play("shield");
        } else if (item === "cannon") {
            this.acquireCannon();
        } else {
            this.robots++;
            this.rebuildRobots();
        }
        this.shake = Math.max(this.shake, 0.2);
    }

    // ---------- 波動砲 (ストック制パニックボタン) ----------
    /** 波動砲を取得: 在庫を +1 する (上限 CANNON_MAX_STOCK)。即発動はしない。満タンならコインに変換。 */
    private acquireCannon(): void {
        if (this.cannonStock >= CANNON_MAX_STOCK) { this.coins += 80; return; }
        this.cannonStock++;
        this.showBanner(`⚡ 波動砲 ストック+1 (×${this.cannonStock})`, "ボタン (Space) で巨大キャノンを召喚！");
    }

    /** 波動砲ボタン: 発射中は無視・未召喚なら在庫消費で召喚・召喚中なら発射。 */
    private cannonButton(): void {
        if (this.cannonFiring > 0) return; // 発射中は無視
        if (!this.cannonHeld) {
            if (this.cannonStock <= 0) return; // 在庫なし
            this.cannonStock--;
            this.summonCannon();
        } else {
            this.fireCannon();
        }
    }

    /** 巨大 SF キャノンを召喚: 前面に盾＆主砲を出す。在庫は cannonButton で消費済み (戻せない)。 */
    private summonCannon(): void {
        this.cannonHeld = true;
        this.cannonShieldHp = CANNON_SHIELD_HP;
        const rig = buildGiantCannonModel();
        rig.scale.setScalar(2.6);
        rig.position.set(this.centroidX, 0, CROWD_Z + 2.7);
        this.scene.add(rig);
        this.cannonMesh = rig;
        this.shake = Math.max(this.shake, 0.45);
        this.showBanner("⚡ 波動砲 召喚！", "盾になる・もう一度押すと発射");
    }

    /** 召喚した波動砲の盾が砕けた: エフェクトを出して発射せず消滅 (召喚分は無駄になる)。 */
    private breakCannon(cx: number): void {
        const p = new THREE.Vector3(cx, 1.3, CROWD_Z + 2.4);
        this.impactBurst(p, "leviathan", 1.5);
        this.shockRing(p, 0xff6a6a, 4.6, 0.5);
        this.shake = Math.max(this.shake, 0.5);
        this.cannonHeld = false;
        this.cannonShieldHp = 0;
        if (this.cannonMesh) { this.disposeObj(this.cannonMesh); this.cannonMesh = null; }
        this.showBanner("盾が砕けた！", "波動砲を撃てなかった…");
    }

    /** 画面右下の波動砲ボタン表示更新: 状態でラベル/有効を切替。 */
    private updateCannonBtn(): void {
        const cb = this.cannonBtn;
        if (!cb) return;
        if (this.cannonFiring > 0) {
            cb.style.display = "none";
            return;
        }
        if (this.cannonHeld) {
            cb.style.display = "flex";
            cb.textContent = `⚡発射 (${this.cannonShieldHp})`;
            cb.style.opacity = "1";
            cb.style.borderColor = "#ffd75e";
            cb.style.background = "rgba(60,40,12,0.85)";
        } else if (this.cannonStock > 0) {
            cb.style.display = "flex";
            cb.textContent = `⚡召喚 ×${this.cannonStock}`;
            cb.style.opacity = "1";
            cb.style.borderColor = "#2a6a8a";
            cb.style.background = "rgba(8,26,42,0.82)";
        } else {
            cb.style.display = "none";
        }
    }

    /** 波動砲を完全に手放す (リスタート/敗北/破棄で安全側に倒す)。 */
    private clearCannon(): void {
        this.cannonHeld = false;
        this.cannonStock = 0;
        this.cannonShieldHp = 0;
        this.cannonFiring = 0;
        if (this.cannonBeam) { this.disposeObj(this.cannonBeam); this.cannonBeam = null; }
        if (this.cannonMesh) { this.disposeObj(this.cannonMesh); this.cannonMesh = null; }
    }

    /** 毎フレーム: 発射中はビーム処理・召喚中は前面追従＋砲口の威圧脈動 (盾が減るほど赤く明滅)。 */
    private updateCannon(dt: number): void {
        // === 発射中: 持続ビームでレーンを焼き続ける (1.4s) ===
        if (this.cannonFiring > 0) {
            this.cannonFiring -= dt;
            const cx = this.cannonMesh ? this.cannonMesh.position.x : this.centroidX;
            if (this.cannonMesh) this.cannonMesh.position.set(cx, 0, CROWD_Z + 2.7);
            // ビーム内に入ってきた敵を毎フレーム蒸発させ続ける + ボスに継続ダメージ
            for (const e of this.entities) {
                if ((e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item")
                    && e.hp > 0 && Math.abs(e.wx - cx) < 2.4 && e.z > CROWD_Z) {
                    e.hp -= 9999 * dt;
                }
            }
            for (let i = this.arrows.length - 1; i >= 0; i--) {
                const a = this.arrows[i];
                if (a.dmg == null && Math.abs(a.wx - cx) < 2.6 && a.z > CROWD_Z) { this.disposeObj(a.mesh); this.arrows.splice(i, 1); }
            }
            if (this.boss) this.boss.hp -= this.boss.max * 0.4 * dt * this.bossDmgMul(this.boss); // 1.4s で ~0.56 HP ぶん (海獣回避中は減衰)
            // ビームのちらつき + 追従
            if (this.cannonBeam) {
                this.cannonBeam.position.x = cx;
                const flick = 0.7 + 0.3 * Math.sin(this.clock.elapsedTime * 40);
                for (const c of this.cannonBeam.children) ((c as THREE.Mesh).material as THREE.MeshBasicMaterial).opacity = flick;
            }
            if (this.trailTick % 2 === 0) this.spark(new THREE.Vector3(cx, 1.4, CROWD_Z + 6), 0x9af0ff);
            this.shake = Math.max(this.shake, 0.22);
            if (this.cannonFiring <= 0) {
                if (this.cannonBeam) { this.disposeObj(this.cannonBeam); this.cannonBeam = null; }
                if (this.cannonMesh) { this.disposeObj(this.cannonMesh); this.cannonMesh = null; }
            }
            return;
        }
        const rig = this.cannonMesh;
        if (!this.cannonHeld || !rig) return;
        rig.position.set(this.centroidX, 0, CROWD_Z + 2.7); // クルーの少し前 (召喚した本体)
        if (this.cannonFlashT > 0) this.cannonFlashT = Math.max(0, this.cannonFlashT - dt);
        // 砲口を威圧的に脈動発光させる (自動充填は廃止)。盾耐久が減るほど赤寄りに明滅し「壊れそう」を見せる。
        const muzzle = rig.children[rig.children.length - 1] as THREE.Mesh;
        const t = this.clock.elapsedTime;
        const hurt = 1 - Math.max(0, Math.min(1, this.cannonShieldHp / CANNON_SHIELD_HP)); // 0=満タン 1=瀕死
        const beat = 0.85 + 0.25 * Math.sin(t * (4 + hurt * 12)); // 瀕死ほど速く明滅
        const pulse = beat + (this.cannonFlashT > 0 ? 0.5 : 0);
        muzzle.scale.setScalar(pulse);
        const mat = muzzle.material as THREE.MeshBasicMaterial | undefined;
        if (mat && (mat as THREE.MeshBasicMaterial).color) {
            // 青白 → 赤 へ補間 (盾が削れるほど赤い)
            mat.color.setRGB(0.68 + 0.32 * hurt, 0.96 - 0.6 * hurt, 1.0 - 0.7 * hurt);
            mat.opacity = 0.75 + 0.25 * Math.sin(t * 8);
        }
    }

    /** 波動砲 発射: 前方へ巨大ビームを 1.4s 持続。初撃で一掃 + 持続でレーンを焼き続け、撃つと消費。 */
    private fireCannon(): void {
        if (!this.cannonHeld) return;
        const cx = this.cannonMesh ? this.cannonMesh.position.x : this.centroidX;
        this.cannonHeld = false;
        this.cannonShieldHp = 0;
        this.cannonFiring = 1.4; // 持続発射
        this.sfx.play("cannon");
        this.spawnCannonBeam(cx);
        for (const e of this.entities) {
            if ((e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item")
                && e.hp > 0 && Math.abs(e.wx - cx) < 2.4) {
                e.hp = 0; // 初撃で今いる敵を消滅 (death loop が報酬/掃除)
            }
        }
        for (let i = this.arrows.length - 1; i >= 0; i--) {
            const a = this.arrows[i];
            if (a.dmg == null && Math.abs(a.wx - cx) < 2.6) { this.disposeObj(a.mesh); this.arrows.splice(i, 1); }
        }
        if (this.boss) this.boss.hp -= this.boss.max * 0.2 * this.bossDmgMul(this.boss); // 初撃 + 持続 DoT で合計大ダメージ (海獣回避中は減衰)
        this.shake = Math.max(this.shake, 0.7);
        this.showBanner("⚡ 波動砲 発射！");
    }

    /** 波動砲の持続ビーム本体 (this.cannonBeam に保持・updateCannon で追従/ちらつき)。 */
    private spawnCannonBeam(cx: number): void {
        const beam = new THREE.Group();
        const outer = new THREE.Mesh(
            new THREE.CylinderGeometry(1.0, 1.3, SPAWN_Z, 16, 1, true),
            new THREE.MeshBasicMaterial({ color: 0x49e0ff, transparent: true, opacity: 0.7, blending: THREE.AdditiveBlending, depthWrite: false, side: THREE.DoubleSide }),
        );
        outer.rotation.x = Math.PI / 2;
        beam.add(outer);
        const core = new THREE.Mesh(
            new THREE.CylinderGeometry(0.4, 0.55, SPAWN_Z, 12, 1, true),
            new THREE.MeshBasicMaterial({ color: 0xeafffe, transparent: true, opacity: 0.9, blending: THREE.AdditiveBlending, depthWrite: false }),
        );
        core.rotation.x = Math.PI / 2;
        beam.add(core);
        beam.position.set(cx, 1.4, CROWD_Z + SPAWN_Z / 2 + 2);
        this.scene.add(beam);
        this.cannonBeam = beam;
        this.impactBurst(new THREE.Vector3(cx, 1.4, CROWD_Z + 6), "leviathan", 1.8);
    }

    // ========== 緊急イベント: Backrooms Level ! (病院の廊下・ホラー) ==========
    private readonly CORRIDOR_BAND = 168; // ランドマークの周期 (ドア間隔14の倍数 → 折返しで間隔が崩れない)
    private readonly CORRIDOR_NEAR = -14; // この z より手前に来たら奥へ折り返す
    /** 病院の廊下を一度だけ組む。壁/天井/腰壁は固定、ドア・器具・血痕などは手前へ流すランドマーク。 */
    private buildCorridor(): void {
        this.hospitalTex = makeHospitalFloorTexture();
        this.hospitalTex.repeat.set(2, 60); // 走行レーンと同じ繰り返し
        const g = new THREE.Group();
        const wallMat = new THREE.MeshLambertMaterial({ color: 0xa8a59a }); // 病院の汚れた白壁
        const wainMat = new THREE.MeshLambertMaterial({ color: 0x5f7d72 }); // 腰壁 (淡い緑)
        const baseMat = new THREE.MeshLambertMaterial({ color: 0x3a3832 }); // 巾木
        const doorMat = new THREE.MeshLambertMaterial({ color: 0x4a4640 }); // 病室のドア
        const winMat = new THREE.MeshBasicMaterial({ color: 0x1a0808, transparent: true, opacity: 0.7 }); // ドアの小窓 (暗い)
        const ceilMat = new THREE.MeshLambertMaterial({ color: 0x8e8c82 }); // 天井タイル
        const LEN = 320, WX = LANE + 0.5, H = 4.4, MIDZ = 70;
        // --- 固定の構造 (連続して一様なのでスクロール不要) ---
        const ceil = new THREE.Mesh(new THREE.BoxGeometry(WX * 2 + 0.8, 0.4, LEN), ceilMat);
        ceil.position.set(0, H - 0.5, MIDZ); g.add(ceil);
        for (const sx of [-WX, WX]) {
            const inner = sx < 0 ? 0.22 : -0.22;
            const wall = new THREE.Mesh(new THREE.BoxGeometry(0.4, H, LEN), wallMat);
            wall.position.set(sx, H / 2 - 0.5, MIDZ); g.add(wall);
            const wain = new THREE.Mesh(new THREE.BoxGeometry(0.1, 0.7, LEN), wainMat);
            wain.position.set(sx + inner, 1.35, MIDZ); g.add(wain);
            const base = new THREE.Mesh(new THREE.BoxGeometry(0.12, 0.32, LEN), baseMat);
            base.position.set(sx + inner, 0.15, MIDZ); g.add(base);
        }
        // --- 流すランドマーク (手前へ移動 → 折返し。これで「前進」が伝わる) ---
        const addScroll = (node: THREE.Object3D, z: number) => { node.position.z = z; g.add(node); this.corridorScroll.push(node); };
        for (const sx of [-WX, WX]) {
            const inner = sx < 0 ? 0.22 : -0.22;
            for (let i = 0; i < 12; i++) { // ちょうど 1 周期 (12 × 14 = 168) ぶんのドア
                const d = new THREE.Group();
                const door = new THREE.Mesh(new THREE.BoxGeometry(0.1, 2.3, 1.3), doorMat);
                door.position.set(sx + inner * 1.1, 0.65, 0); d.add(door);
                const handle = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.1, 0.18), baseMat);
                handle.position.set(sx + inner * 1.6, 1.0, sx < 0 ? 0.45 : -0.45); d.add(handle);
                const win = new THREE.Mesh(new THREE.PlaneGeometry(0.55, 0.7), winMat);
                win.position.set(sx + inner * 1.7, 1.55, 0); win.rotation.y = Math.PI / 2; d.add(win);
                const lamp = new THREE.Mesh(new THREE.BoxGeometry(0.12, 0.28, 0.9), new THREE.MeshBasicMaterial({ color: 0xff1a1a }));
                lamp.position.set(sx + inner * 1.2, 2.6, 0); d.add(lamp); this.corridorPanels.push(lamp);
                const plate = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.26, 0.5), new THREE.MeshLambertMaterial({ color: 0x24242a }));
                plate.position.set(sx + inner * 1.6, 2.0, sx < 0 ? 0.82 : -0.82); d.add(plate);
                const num = new THREE.Mesh(new THREE.PlaneGeometry(0.32, 0.1), new THREE.MeshBasicMaterial({ color: 0xbfc4cc, transparent: true, opacity: 0.45 }));
                num.position.set(sx + inner * 1.85, 2.0, sx < 0 ? 0.82 : -0.82); num.rotation.y = Math.PI / 2; d.add(num);
                const call = new THREE.Mesh(new THREE.BoxGeometry(0.06, 0.12, 0.12), new THREE.MeshBasicMaterial({ color: 0x2bff7a, transparent: true, opacity: 0.5 }));
                call.position.set(sx + inner * 1.5, 1.7, sx < 0 ? -0.55 : 0.55); d.add(call);
                addScroll(d, -14 + i * 14);
            }
        }
        // 天井の蛍光灯 (赤く非常点灯・ちらつく) — 24 × 7 = 168
        for (let i = 0; i < 24; i++) {
            const p = new THREE.Mesh(new THREE.BoxGeometry(1.6, 0.12, 0.5), new THREE.MeshBasicMaterial({ color: 0xff2a2a, transparent: true, opacity: 0.7 }));
            p.position.y = H - 0.78; this.corridorPanels.push(p); addScroll(p, -14 + i * 7);
        }
        // 床の血痕のドラッグ跡
        const bloodMat = new THREE.MeshBasicMaterial({ color: 0x3a0606, transparent: true, opacity: 0.75 });
        for (let n = 0; n < 8; n++) {
            const streak = new THREE.Mesh(new THREE.PlaneGeometry(0.35 + Math.random() * 0.5, 2.4 + Math.random() * 2.4), bloodMat);
            streak.rotation.x = -Math.PI / 2; streak.rotation.z = (Math.random() - 0.5) * 0.7;
            streak.position.set((Math.random() * 2 - 1) * 2.6, 0.03, 0); addScroll(streak, -10 + n * 21);
        }
        // 壁際に放置された病院器具 (静物)
        for (let n = 0; n < 10; n++) {
            const side = n % 2 === 0 ? -1 : 1;
            const prop = buildHospitalPropModel(n % 3);
            prop.position.x = side * (LANE - 0.55);
            prop.rotation.y = side * 0.5 + (Math.random() - 0.5) * 0.6;
            addScroll(prop, -8 + n * 17);
        }
        // 病棟の両開きドア (奥へ続くランドマーク)
        for (let n = 0; n < 4; n++) {
            const side = n % 2 === 0 ? -1 : 1, inner = side < 0 ? 0.22 : -0.22;
            const dd2 = new THREE.Group();
            const dd = new THREE.Mesh(new THREE.BoxGeometry(0.12, 2.7, 2.3), new THREE.MeshLambertMaterial({ color: 0x3a3a42 }));
            dd.position.set(side * WX + inner * 1.1, 0.85, 0); dd2.add(dd);
            const seam = new THREE.Mesh(new THREE.BoxGeometry(0.16, 2.7, 0.06), new THREE.MeshLambertMaterial({ color: 0x12121a }));
            seam.position.set(side * WX + inner * 1.4, 0.85, 0); dd2.add(seam);
            for (const dz of [-0.55, 0.55]) {
                const w = new THREE.Mesh(new THREE.PlaneGeometry(0.5, 0.6), winMat);
                w.position.set(side * WX + inner * 1.7, 1.7, dz); w.rotation.y = Math.PI / 2; dd2.add(w);
            }
            addScroll(dd2, 6 + n * 40);
        }
        // 「EXIT」風の緑のサイン (奥へ続く誘導)
        for (let i = 0; i < 5; i++) {
            const sign = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.4, 0.08), new THREE.MeshBasicMaterial({ color: 0x2bff7a, transparent: true, opacity: 0.85 }));
            sign.position.y = H - 1.2; addScroll(sign, 12 + i * 33);
        }
        g.visible = false;
        this.scene.add(g);
        this.corridor = g;
    }

    /** 廊下のランドマークを手前へ流して折り返す (床テクスチャと同速 → 実際に前進している感)。 */
    private scrollCorridor(dt: number): void {
        const d = this.speed * dt;
        for (const el of this.corridorScroll) {
            el.position.z -= d;
            if (el.position.z < this.CORRIDOR_NEAR) el.position.z += this.CORRIDOR_BAND;
        }
    }

    /** 廊下ルック (赤い暗闇) の ON/OFF。OFF で現バイオームに復元。 */
    private setBackroomsLook(on: boolean): void {
        if (on) {
            (this.scene.background as THREE.Color).setHex(0x180a0a);
            this.fog.color.setHex(0x2c0c0c); this.fog.near = 12; this.fog.far = 74; // 迫る敵が見える視程
            this.dir.color.setHex(0xff7a5e); this.dir.intensity = 1.6; // 暖色寄りの赤キーライト (青いクルーも照らす)
            this.hemi.color.setHex(0x9a3a34); this.hemi.groundColor.setHex(0x140808); this.hemi.intensity = 0.9;
            this.grassMat.color.setHex(0x241212);
            for (const pool of this.propPools) pool.mesh.visible = false; // 通常のシーナリーを隠す
            for (const f of this.fenceMeshes) f.visible = false; // 柵を隠す (病院に柵は無い)
            // 走行レーンを病院の床タイルに差し替え (スクロール位相を引き継ぐ)
            this.hospitalTex.repeat.copy(this.pierTex.repeat);
            this.hospitalTex.offset.copy(this.pierTex.offset);
            this.laneMat.map = this.hospitalTex;
            this.laneMat.needsUpdate = true;
            this.pierTex = this.hospitalTex; // スクロール対象を付け替える
        } else {
            this.hemi.intensity = 1.05; // applyBiome は intensity を戻さないので既定値へ明示復元
            for (const pool of this.propPools) pool.mesh.visible = true;
            for (const f of this.fenceMeshes) f.visible = true; // 柵を戻す
            this.applyBiome(this.stage); // 空/光/フォグ/地面/レーンを元に戻す (laneMat.map と pierTex も復元)
        }
    }

    /** ごく稀に緊急イベントを発生 (1 ステージ 1 回まで・序盤すぎない位置で)。 */
    private maybeTriggerEvent(): void {
        if (this.eventActive || this.eventDoneStage === this.stage) return;
        if (this.stage < 2) return; // S1 は出さない
        if (this.stage - this.lastEventStage < 3) return; // 直近イベントから 3 ステージは空ける
        if (this.distance < 60 || this.distance > this.stageGoal - 60) return; // 中盤限定
        if (Math.random() < 0.0007) { // 毎フレ極小確率 → ごく稀に発火
            this.eventDoneStage = this.stage;
            this.startBackroomsEvent();
        }
    }

    /** 既存の隠し裂け目があれば破棄して null 化 (場面切替・入場時の後始末用)。 */
    private clearSecretGap(): void {
        if (this.secretGap) {
            this.disposeObj(this.secretGap.obj);
            this.secretGap = null;
        }
    }

    /** 不気味な「柵の裂け目」オブジェクトを組む: 暗い欠落 + Level! 色のグロー板。 */
    private buildSecretGap(side: -1 | 1): THREE.Object3D {
        const g = new THREE.Group();
        // 柵が割れた暗い欠落 (奥行きのある黒い箱)。
        const dark = new THREE.Mesh(
            new THREE.BoxGeometry(0.5, 2.6, 2.4),
            new THREE.MeshBasicMaterial({ color: 0x05050a }),
        );
        g.add(dark);
        // Level! の黄色グロー (内側=明るい黄 / 縁=くすんだ黄) を 2 枚重ねて滲ませる。
        const glowIn = new THREE.Mesh(
            new THREE.PlaneGeometry(1.6, 2.8),
            new THREE.MeshBasicMaterial({ color: 0xffe27a, transparent: true, opacity: 0.55, blending: THREE.AdditiveBlending, depthWrite: false }),
        );
        glowIn.position.set(-side * 0.4, 0.2, 0); // レーン側へ少しはみ出させて誘い込む
        g.add(glowIn);
        const glowOut = new THREE.Mesh(
            new THREE.PlaneGeometry(3.2, 4.2),
            new THREE.MeshBasicMaterial({ color: 0xd9c64a, transparent: true, opacity: 0.22, blending: THREE.AdditiveBlending, depthWrite: false }),
        );
        glowOut.position.set(-side * 0.4, 0.2, 0.02);
        g.add(glowOut);
        return g;
    }

    /** 走行中にごく稀に隠し裂け目を出す (同時 1 つ・イベント中は出さない)。 */
    private maybeSpawnSecretGap(): void {
        if (this.secretGap || this.eventActive || this.phase !== "running" || this.stage < 2) return;
        if (this.distance < 60 || this.distance > this.stageGoal - 60) return; // 中盤限定 (maybeTriggerEvent と同じ窓)
        if (Math.random() >= 0.0008) return; // 毎フレ極小確率 → 発見の演出 (常設の誘惑にしない)
        const side: -1 | 1 = Math.random() < 0.5 ? -1 : 1;
        const obj = this.buildSecretGap(side);
        obj.position.set(side * (LANE + 0.3), 0.6, SPAWN_Z);
        this.scene.add(obj);
        this.secretGap = { side, z: SPAWN_Z, obj };
    }

    /** 隠し裂け目の毎フレーム処理 (手前へ流す・きらめき・ヒント・ノークリップ入場判定)。 */
    private updateSecretGap(dt: number): void {
        const g = this.secretGap;
        if (!g) return;
        g.z -= this.speed * dt; // 通常プロップと同じ速さで手前へ流す
        g.obj.position.z = g.z;
        // 近づいたら微かなきらめき (プール VFX を毎フレ数粒だけ)。
        if (g.z < 24 && g.z > -4 && Math.random() < 0.5) {
            this.spark(new THREE.Vector3(
                g.side * (LANE + 0.3) + (Math.random() - 0.5) * 1.2,
                0.6 + Math.random() * 2.2,
                g.z + (Math.random() - 0.5) * 1.6,
            ), Math.random() < 0.5 ? 0xffe27a : 0xd9c64a);
        }
        // 近づいた瞬間に 1 度だけヒント (操作できると気付かせる)。
        if (!g.hinted && g.z < 20) {
            g.hinted = true;
            this.showBanner("👁 ノークリップ…?", "柵の隙間へ突っ込め");
        }
        // 入場判定: 裂け目が手の届く z 窓にあり、群衆が柵の外 (LANE 超え) を同じ側へ突き抜けたら自発入場。
        const reachable = g.z > CROWD_Z - 2 && g.z < CROWD_Z + 10;
        if (reachable && Math.sign(this.centroidX) === g.side && Math.abs(this.centroidX) > LANE) {
            this._v.set(this.centroidX, 0.9, CROWD_Z);
            this.popWorld("NO-CLIP!!", this.centroidX, "#ffe27a");
            this.shockRing(this._v, 0xffe27a, 6, 0.5);
            this.shockRing(this._v, 0xd9c64a, 3.4, 0.3);
            this.shake = Math.max(this.shake, 0.9);
            this.clearSecretGap();
            this.eventDoneStage = this.stage; // 乱数経路と同じ once/間隔ゲートを尊重
            this.lastEventStage = this.stage;
            this.startBackroomsEvent(); // 通常の入場経路を共有 (セットアップ/後始末を再利用)
            return;
        }
        if (g.z < -10) this.clearSecretGap(); // 取り逃し → 破棄
    }

    /** Backrooms Level ! 開始: 暗転 EMERGENCY → noclip 落下 → 赤い病院の廊下で襲撃。 */
    private startBackroomsEvent(): void {
        if (this.eventActive || this.phase !== "running") return;
        this.eventActive = true;
        this.clearSecretGap(); // 乱数発火の入場でも保留中の裂け目を片付ける
        this.lastEventStage = this.stage; // イベント間隔ゲート用に記録
        this.eventIntro = 1.4;
        this.eventTimer = 26; // 廊下を長く走る (短すぎ対策)
        this.eventSpawnT = 0.4;
        this.paTimer = 2.0;
        this.clearZoneModifiers(); // C/D の一時モディファイアを解除 (落下演出のカメラと干渉させない)
        this.returnAllRecruited(); // 一掃前に連れ去られたクルーを取り返す
        for (const e of this.entities) { if (e.type === "bruiser" && e.pounceTell) this.disposeObj(e.pounceTell); this.disposeObj(e.obj); } // 通常の脅威を一掃
        this.entities.length = 0;
        for (const s of this.arrows) this.disposeObj(s.mesh);
        this.arrows.length = 0;
        if (this.corridor) this.corridor.visible = true;
        this.setBackroomsLook(true);
        this.showEventOverlay();
        this.showBanner("⚠ EMERGENCY ⚠", "NO-CLIP — LEVEL !");
        this.sfx.setMood("level"); this.sfx.play("level");
        this.shake = Math.max(this.shake, 0.8);
        this.pulseLanding(true); // 落下の衝撃
    }

    /** イベント中の毎フレーム処理 (落下演出 → 襲撃 → 制限時間で脱出)。 */
    private updateBackroomsEvent(dt: number): void {
        const t = this.clock.elapsedTime;
        // 天井/壁の赤い照明をちらつかせる (たまに完全消灯=暗闇)
        const blackout = Math.sin(t * 0.7) > 0.93; // 周期的な瞬間停電
        for (let i = 0; i < this.corridorPanels.length; i++) {
            const m = this.corridorPanels[i].material as THREE.MeshBasicMaterial;
            m.opacity = blackout ? 0.04 : (Math.random() < 0.05 ? 0.12 : 0.55 + 0.4 * Math.abs(Math.sin(t * 9 + i)));
        }
        if (this.eventEl) this.eventEl.style.opacity = blackout ? "0.95" : (0.5 + 0.15 * Math.sin(t * 4)).toFixed(2);

        if (this.eventIntro > 0) {
            // noclip 落下: 上 (6.4) から床を突き抜けて、廊下の中の低い視点 (EVENT_CAM_Y) へ沈み込む。
            // 途中で大きくオーバーシュート (ガクッと落ちる感触)。戻らずそのまま廊下視点に居続ける。
            this.eventIntro -= dt;
            const k = clamp01(1 - this.eventIntro / 1.4);
            this.camera.position.y = 6.4 + (EVENT_CAM_Y - 6.4) * Ease.quadOut(k) - 2.4 * Math.sin(k * Math.PI);
            this.shake = Math.max(this.shake, 0.5);
            return; // 落下中はまだ襲撃しない
        }
        this.camera.position.y += (EVENT_CAM_Y - this.camera.position.y) * Math.min(1, dt * 4); // 廊下の低い視点を維持 (天井の下)
        this.scrollCorridor(dt); // ドア/器具/サインを手前へ流す (トレッドミル感の解消)

        // 前から大量の敵と病院器具が迫る
        this.eventSpawnT -= dt;
        if (this.eventSpawnT <= 0) {
            this.eventSpawnT = Math.max(0.3, 0.85 - this.stage * 0.045); // 高ステージほど波が速く詰まる
            this.spawnHorrorWave();
        }
        // 歪んだ館内放送 (途切れ途切れ)
        this.paTimer -= dt;
        if (this.paTimer <= 0) {
            this.paTimer = 4.5 + Math.random() * 4;
            this.showPA(PA_MESSAGES[Math.floor(Math.random() * PA_MESSAGES.length)]);
        }
        this.eventTimer -= dt;
        if (this.eventTimer <= 0) this.endBackroomsEvent();
    }

    private endBackroomsEvent(): void {
        if (!this.eventActive) return;
        this.eventActive = false;
        this.clearZoneModifiers(); // 念のため C/D を解除して通常走行へ戻す
        if (this.corridor) this.corridor.visible = false;
        this.setBackroomsLook(false);
        this.hideEventOverlay();
        this.flashScreen("#ffffff", 650); // 光の中へ脱出 (廊下→通常への切替を滑らかに)
        this.camera.position.y = 6.4;
        this.returnAllRecruited(); // 一掃前に連れ去られたクルーを取り返す
        for (const e of this.entities) { if (e.type === "bruiser" && e.pounceTell) this.disposeObj(e.pounceTell); this.disposeObj(e.obj); }
        this.entities.length = 0;
        this.shake = Math.max(this.shake, 0.4);
        this.showBanner("脱出！");
        this.sfx.setMood("run");
        this.unlockArmor("dark"); // Level! を生還で闇のアーマー解放
    }

    /**
     * 廊下の襲撃 1 波: 敵は liminal 系 (bacteria) に統一 + 病院器具。通常の赤い雑魚は出さない。
     * ステージが上がるほど数が増え、強敵 (パーティゴアー→シールドロボ→飛来兵) が段階的に解禁されて手強くなる。
     */
    private spawnHorrorWave(): void {
        const s = this.stage;
        const r = Math.random();
        if (r < 0.45) this.spawnBacteria(); // 大型のうごめく塊
        else if (r < 0.7) this.spawnBacteria(true); // 小型の数押し
        else this.spawnHospitalProp();
        if (Math.random() < 0.5) this.spawnBacteria(true); // さらに小型を追撃
        if (s >= 4 && Math.random() < 0.2 + s * 0.02) this.spawnBacteria(true); // 高ステージほど数押しが増える
        if (s >= 6 && Math.random() < s * 0.015) this.spawnBacteria(); // 終盤は大型も増量
        // パーティゴアー (連れ去り): 中盤の Level! から稀に
        if (s >= 4 && Math.random() < 0.08 + s * 0.008) this.spawnPartygoer();
        // シールドロボ (ミサイル持ち): 終盤の Level! で前線にミサイルの圧を足す
        if (s >= 8 && Math.random() < 0.06 + (s - 8) * 0.02) this.spawnShieldBot();
        // 飛来兵 (背後攻撃): 終盤のみ・同時 1 体まで (背後対策が揃う頃に・黒い影で廊下を埋めない)
        if (s >= 7 && Math.random() < 0.07 && !this.entities.some((e) => e.type === "bruiser" && e.variant === "flyer")) this.spawnFlyingRaider();
    }

    /** 病院器具 (車椅子 / 点滴スタンド / ストレッチャー) が前方から突っ込んでくる障害物。 */
    private spawnHospitalProp(): void {
        const x = this.laneX();
        const grp = buildHospitalPropModel(Math.floor(Math.random() * 3));
        this.scene.add(grp);
        const e: HazardEnt = {
            type: "hazard", obj: grp, z: SPAWN_Z, wx: x, resolved: false,
            halfW: 0.7, fire: false, sweepAmp: 0, sweepPhase: 0,
        };
        this.placeEntity(e); this.entities.push(e);
    }

    /** 赤いビネット + EMERGENCY 文字 + スキャンラインの DOM オーバーレイ。 */
    private showEventOverlay(): void {
        if (this.eventEl) return;
        const el = document.createElement("div");
        el.style.cssText =
            "position:absolute;inset:0;z-index:3;pointer-events:none;opacity:0.6;" +
            "box-shadow:inset 0 0 160px 60px rgba(180,0,0,0.85);" +
            "background:repeating-linear-gradient(0deg,rgba(0,0,0,0.0) 0px,rgba(0,0,0,0.0) 3px,rgba(0,0,0,0.25) 4px);";
        const txt = document.createElement("div");
        txt.textContent = "⚠ EMERGENCY ⚠";
        txt.style.cssText =
            "position:absolute;top:8%;left:0;right:0;text-align:center;color:#ff3030;" +
            "font:800 26px system-ui,sans-serif;letter-spacing:4px;text-shadow:0 0 12px #ff0000;";
        el.appendChild(txt);
        this.host.appendChild(el);
        this.eventEl = el;
    }
    private hideEventOverlay(): void {
        if (this.eventEl) { this.eventEl.remove(); this.eventEl = null; }
    }

    /** 歪んだ館内放送を画面下に途切れ途切れで表示 (フェードアウトして自動撤去)。 */
    private showPA(text: string): void {
        const el = document.createElement("div");
        el.textContent = text;
        el.style.cssText =
            "position:absolute;bottom:13%;left:0;right:0;text-align:center;z-index:4;pointer-events:none;" +
            "color:#ff7a7a;font:700 18px ui-monospace,monospace;letter-spacing:2px;text-shadow:0 0 8px #900,0 0 2px #000;" +
            "opacity:0.92;transition:opacity 1.8s ease-out;";
        this.host.appendChild(el);
        requestAnimationFrame(() => { el.style.opacity = "0"; });
        setTimeout(() => { el.remove(); }, 2000);
    }

    /** 一瞬の全画面フラッシュ (脱出の切替演出など)。CSS transition でフェードアウトして自動撤去。 */
    private flashScreen(color: string, ms = 600): void {
        const el = document.createElement("div");
        el.style.cssText =
            `position:absolute;inset:0;z-index:5;pointer-events:none;background:${color};opacity:0.92;transition:opacity ${ms}ms ease-out;`;
        this.host.appendChild(el);
        requestAnimationFrame(() => { el.style.opacity = "0"; });
        setTimeout(() => { el.remove(); }, ms + 60);
    }

    /** イベントを安全に終了 (リスタート/敗北/破棄)。restore=true で世界をバイオームに戻す。 */
    private clearEvent(restore: boolean): void {
        this.clearSecretGap(); // restart/defeat 経由で保留中の裂け目も片付ける
        const wasActive = this.eventActive;
        this.eventActive = false;
        this.eventTimer = 0;
        this.eventIntro = 0;
        if (this.corridor) this.corridor.visible = false;
        this.hideEventOverlay();
        this.camera.position.y = 6.4;
        if (wasActive && restore) this.setBackroomsLook(false);
        // Level! アンビエント(mp3)を必ず止める: イベント中に死亡/リスタートすると mood が "level" のまま残り、
        // endBackroomsEvent を通らないのでアンビエントが鳴り続ける不具合の修正。
        if (wasActive) this.sfx.setMood("run");
    }

    // ---------- 壁による弾の遮蔽 (敵味方共通) ----------
    /** この hazard が x 位置で弾を遮る固体の壁か (床/ゾーン/頭上/通過系は遮らない・隙間は通す)。 */
    private hazardBlocksX(e: Entity, x: number): boolean {
        if (e.type !== "hazard") return false;
        if (e.pull || e.drift || e.eye || e.sky || e.slam || e.warp || e.gate || e.zone || e.pit) return false; // 床/ゾーン/頭上/通過は遮らない
        if (e.gapWall) return Math.abs(x - e.wx) > e.gapWall.gapHalf; // 隙間の外だけ遮る
        if (e.crusher) { const k = clamp01(1 - e.z / SPAWN_Z); const gap = e.crusher.gap0 + (e.crusher.gap1 - e.crusher.gap0) * k; return Math.abs(x) > gap / 2; } // 中央の隙間以外
        return Math.abs(x - e.wx) < Math.max(0.4, e.halfW); // 一般の固体壁 (ノコギリ/電流/転がり岩/仕切り) は幅内を遮る
    }
    /** x 上で、z 区間 [zLo, zHi] の間に弾を遮る壁があるか (射線判定)。 */
    private wallBlocks(x: number, zLo: number, zHi: number): boolean {
        for (const e of this.entities) {
            if (e.type === "hazard" && e.z > zLo - 0.6 && e.z < zHi + 0.6 && this.hazardBlocksX(e, x)) return true;
        }
        return false;
    }

    // ---------- 射撃 ----------
    private autoFire(dt: number): void {
        // 撃てる対象を「地上」と「空 (飛行敵)」に分けて、それぞれの最前を拾う。
        // → 群衆が両方へ同時に撃つので、飛行敵が来ても地上への射撃がおろそかにならない。
        // 通常弾は「壁の手前の最寄り (unblocked)」を狙う。ロケラン等は壁ごと壊すので「壁無視の最寄り (any)」も拾う。
        let ground: HpEnt | null = null;
        let air: BruiserEnt | null = null;
        let groundAny: HpEnt | null = null;
        let airAny: BruiserEnt | null = null;
        for (const e of this.entities) {
            if (!(e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item")) continue;
            if (e.hp <= 0 || e.z <= CROWD_Z + 1 || e.z >= SPAWN_Z * 0.78) continue;
            const blocked = this.wallBlocks(e.wx, CROWD_Z, e.z); // 壁の向こうか
            if (e.type === "bruiser" && e.variant === "flyer") {
                if (!airAny || e.z < airAny.z) airAny = e;
                if (!blocked && (!air || e.z < air.z)) air = e;
            } else {
                if (!groundAny || e.z < groundAny.z) groundAny = e;
                if (!blocked && (!ground || e.z < ground.z)) ground = e;
            }
        }
        // 単一対象の武器 (ロケキャノン/ブーメラン/追尾): 壁無視で最前の 1 体 (ロケランは壁を壊して通す)。
        const front: HpEnt | null = airAny && (!groundAny || airAny.z < groundAny.z) ? airAny : groundAny;
        if (this.weaponTier === ROCKET_WEAPON) {
            if (this.rocketCdT > 0) this.rocketCdT = Math.max(0, this.rocketCdT - dt);
            if (front && this.rocketCdT <= 0) this.fireRocketBlast(front);
            return; // ロケキャノンは通常の継続ダメージ/弾を撃たない (状況用)
        }
        if (this.weaponTier === BOOMERANG_WEAPON) {
            if (!this.boomerang) this.throwBoomerang(front);
            return; // 継続火力なし (飛行中は無攻撃 = 投げ直しのゲート)
        }
        if (this.weaponTier === HOMING_WEAPON) {
            // 低レートでホーミングミサイルを一斉発射。狙いにくい敵 (飛来兵/シールドロボ/ボス) を追う。
            if (this.homingCdT > 0) this.homingCdT = Math.max(0, this.homingCdT - dt);
            if (this.homingCdT <= 0 && (front || this.boss)) { this.fireHomingVolley(); this.homingCdT = HOMING_COOLDOWN; }
            return;
        }
        // (壁の遮蔽は上の索敵で適用済み: ground/air は壁の手前の最寄りだけ。front は壁無視でロケランが壊す。)
        // ミニガン (id2) は過熱メーター制: 撃ち続けると満タンで一時ロック (銃との差別化)。
        if (this.weaponTier === MINIGUN_WEAPON) {
            if (this.minigunOverheat > 0) {
                this.minigunOverheat = Math.max(0, this.minigunOverheat - dt);
                this.minigunHeat = this.minigunOverheat / MINIGUN_OVERHEAT_LOCK; // ロック中に満タン→0 へ冷却
                if (this.minigunOverheat === 0) this.sfx.play("cool");
                return; // 過熱中は発射しない
            }
            if (ground || air) {
                this.minigunHeat += dt * MINIGUN_HEAT_RATE;
                if (this.minigunHeat >= 1) {
                    this.minigunHeat = 1;
                    this.minigunOverheat = MINIGUN_OVERHEAT_LOCK;
                    this.popWorld("OVERHEAT!", this.centroidX, "#ff7a3a");
                    this.shake = Math.max(this.shake, 0.2);
                    this.sfx.play("overheat");
                    return;
                }
            } else {
                this.minigunHeat = Math.max(0, this.minigunHeat - dt * MINIGUN_COOL_RATE);
            }
        }
        const both = !!ground && !!air;
        const F = this.firepower * FIRE_FACTOR;
        // 会心は地上系のみ (空は命中減=「当てにくい」表現なので会心しない)。
        this.critTimer -= dt;
        const crit = this.critTimer <= 0;
        if (crit) this.critTimer = 0.6 + Math.random() * 0.9;
        // --- 地上: 両対象なら配分・単独なら集中。会心はこちら。 ---
        if (ground) {
            const share = both ? GROUND_FIRE_SHARE : 1;
            let dmg = F * share * dt;
            if (crit) {
                dmg += F * share * 1.6;
                this.popWorld("CRIT!", ground.obj.position.x, "#ff9a9a");
                this.spark(ground.obj.position.clone().setY(1.2), 0xff5a5a);
            }
            this.damageEnemy(ground, dmg); // シールドロボは前面シールドが肩代わり
        }
        // --- 空: 命中減 (AIR_DMG_MUL) で撃ち落としに手間取る。 ---
        if (air) {
            const share = both ? AIR_FIRE_SHARE : 1;
            air.hp -= F * share * AIR_DMG_MUL * dt;
            if (crit) this.popWorld("MISS", air.obj.position.x, "#bcd2e8"); // たまに外す (空中は当てにくい)
        }
        // --- 弾の視覚: クールダウン 1 回で地上/空へ振り分けて撃つ (両系統が見える)。空は高度へ撃ち上げ。 ---
        if (ground || air) {
            const aLift = air ? (air as BruiserEnt).flyLift : null;
            const aAimY = 1.1 + (aLift ? aLift.position.y : 3.0);
            if (ground && air) this.spawnBullets(dt, ground.obj.position, 1.1, air.obj.position, aAimY);
            else if (ground) this.spawnBullets(dt, ground.obj.position, 1.1);
            else if (air) this.spawnBullets(dt, air.obj.position, aAimY);
        }
    }

    private spawnBullets(dt: number, target: THREE.Vector3, aimY = 1.1, target2?: THREE.Vector3 | null, aimY2 = 1.1): void {
        this.fireCooldown -= dt;
        if (this.fireCooldown > 0) return;
        const tier = this.weaponTier;
        const def = WEAPON_DEFS[tier] ?? WEAPON_DEFS[0];
        this.fireCooldown = def.interval;
        const n = def.bullets;
        const color = def.color;
        for (let i = 0; i < n; i++) {
            // 第 2 対象 (空) があるときは弾を交互に振り分けて両方へ飛ばす。
            const toSecond = target2 != null && i % 2 === 1;
            const tx = toSecond ? target2!.x : target.x;
            const ty = toSecond ? aimY2 : aimY;
            const tz = toSecond ? target2!.z : target.z;
            const m = new THREE.Mesh(
                new THREE.SphereGeometry(0.12, 6, 5),
                new THREE.MeshBasicMaterial({ color }),
            );
            const origin = new THREE.Vector3(this.centroidX + (Math.random() - 0.5) * 1.4, 1.1, CROWD_Z + 0.5);
            m.position.copy(origin);
            this.scene.add(m);
            const vel = new THREE.Vector3(tx, ty, tz).sub(origin).normalize().multiplyScalar(38);
            // トレイルを持つ武器 (ミニガン等) は小さなブルームと 1 フレームおきのトレイル
            const trail = def.trail;
            if (trail) this.projGlow(m, color, 0.9);
            this.bullets.push({ mesh: m, vel, life: 0.6, trailCol: trail ? color : undefined });
        }
        this.muzzle(color);
        this.sfx.play("shoot");
        if (def.trail) this.shake = Math.max(this.shake, 0.08);
    }

    /** ロケキャノンの巨大ブラスト: 着弾点周辺を広範囲一掃 + 壁系 hazard 破壊 + ボスへ大ダメージ。激遅 (4s 周期)。 */
    private fireRocketBlast(target: HpEnt): void {
        const cx = target.wx;
        const cz = target.z;
        // 着弾点付近 (横 ±ROCKET_AOE_HALF・前方の一定区間) の敵を一掃する。
        for (const e of this.entities) {
            if ((e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item")
                && e.hp > 0 && Math.abs(e.wx - cx) < ROCKET_AOE_HALF && e.z > CROWD_Z && e.z < cz + 10) {
                e.hp = 0; // death loop が報酬/掃除
            }
        }
        // 範囲内の壁系 hazard (仕切り壁/スパイク/ノコギリ等) を破壊して道を開く。
        let brokeWall = false;
        for (let i = this.entities.length - 1; i >= 0; i--) {
            const e = this.entities[i];
            if (e.type === "hazard" && Math.abs(e.wx - cx) < ROCKET_AOE_HALF && e.z > CROWD_Z && e.z < cz + 10) {
                this.spark(e.obj.position.clone().setY(1.0), 0xff7a3b);
                this.disposeObj(e.obj);
                this.entities.splice(i, 1);
                brokeWall = true;
            }
        }
        if (brokeWall) this.sfx.play("break"); // 壁破壊音
        // 飛来弾 (撃ち落とせない通常弾) も範囲内なら消す。
        for (let i = this.arrows.length - 1; i >= 0; i--) {
            const a = this.arrows[i];
            if (a.dmg == null && Math.abs(a.wx - cx) < ROCKET_AOE_HALF && a.z > CROWD_Z && a.z < cz + 10) {
                this.disposeObj(a.mesh);
                this.arrows.splice(i, 1);
            }
        }
        if (this.boss) this.boss.hp -= this.boss.max * 0.22 * this.bossDmgMul(this.boss); // ボスへ大ダメージ (連発できないので 1 発が重い)・海獣回避中は減衰
        // 派手な VFX
        const at = new THREE.Vector3(cx, 1.2, cz);
        this.boom(at);
        this.impactBurst(at.clone().setY(1.6), "dragon", 1.5);
        this.shockRing(new THREE.Vector3(cx, 1.0, cz), 0xff7a3b, ROCKET_AOE_HALF * 2, 0.5);
        this.muzzle(0xff7a3b);
        this.popWorld("BOOM!", cx, "#ff9a4a");
        this.shake = Math.max(this.shake, 0.6);
        this.rocketCdT = ROCKET_COOLDOWN; // この長さが「使い物にならない遅さ」の肝
    }

    private updateBullets(dt: number): void {
        for (let i = this.bullets.length - 1; i >= 0; i--) {
            const b = this.bullets[i];
            b.mesh.position.addScaledVector(b.vel, dt);
            b.life -= dt;
            if (b.trailCol != null && (this.trailTick & 1) === 0) this.trailEmit(b.mesh.position, b.trailCol, b.trailEl);
            if (b.life <= 0 || b.mesh.position.z > SPAWN_Z) {
                this.disposeObj(b.mesh);
                this.bullets.splice(i, 1);
            }
        }
    }

    /** ブーメランを投げる: メッシュを 1 度だけ生成し、対象 (なければ正面) へ飛ばす。 */
    private throwBoomerang(target: HpEnt | null): void {
        const color = WEAPON_DEFS[BOOMERANG_WEAPON].color;
        const mesh = new THREE.Mesh(
            new THREE.TorusGeometry(0.42, 0.12, 6, 12),
            new THREE.MeshBasicMaterial({ color }),
        );
        mesh.rotation.x = Math.PI / 2; // 水平に寝かせて回転させる
        const ox = this.centroidX;
        const oz = CROWD_Z + 0.5;
        mesh.position.set(ox, 1.0, oz);
        this.scene.add(mesh);
        this.projGlow(mesh, color, 0.9);
        // 進行方向: 対象があればそちらへ、なければ正面 (+z)。
        let dx = 0;
        let dz = 1;
        if (target) {
            dx = target.wx - ox;
            dz = Math.max(4, target.z - oz);
        }
        const len = Math.hypot(dx, dz) || 1;
        this.boomerang = {
            mesh,
            phase: "out",
            x: ox,
            z: oz,
            vx: (dx / len) * BOOM_SPEED,
            vz: (dz / len) * BOOM_SPEED,
            life: BOOM_MAX_LIFE,
            hit: new Set<EntBase>(),
        };
        this.muzzle(color);
    }

    /** ブーメランを往路→復路へ折り返す (ヒット済みをクリアして帰りも当たるように)。 */
    private flipBoomerangBack(b: NonNullable<CrewRun3D["boomerang"]>): void {
        b.phase = "back";
        b.hit.clear();
        this.spark(b.mesh.position.clone(), 0x8fffcf);
        this.shake = Math.max(this.shake, 0.12);
    }

    /** ブーメランの飛行・貫通ダメージ・壁/壁面/レーン端での跳ね返り・帰還キャッチを毎フレーム処理。 */
    private updateBoomerang(dt: number): void {
        const b = this.boomerang;
        if (!b) return;
        // 1. 視覚: 回転 + トレイル
        b.mesh.rotation.y += dt * 18;
        if ((this.trailTick & 1) === 0) this.trailEmit(b.mesh.position, 0x8fffcf);
        // 2. 前進
        b.x += b.vx * dt;
        b.z += b.vz * dt;
        b.life -= dt;
        b.mesh.position.set(b.x, 1.0, b.z);
        // 3. 貫通ダメージ: 範囲内の未ヒット HP 個体に一撃ずつ (止まらず貫通)
        const dmg = BOOM_HIT_DMG(this.firepower);
        for (const e of this.entities) {
            if (
                (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item") &&
                e.hp > 0 &&
                !b.hit.has(e) &&
                Math.abs(e.wx - b.x) < BOOM_HIT_R &&
                Math.abs(e.z - b.z) < BOOM_HIT_R
            ) {
                e.hp -= dmg;
                b.hit.add(e);
                this.spark(e.obj.position.clone().setY(1.2), 0x8fffcf);
            }
        }
        // ボスにも当たれば削る (ボス戦中・重なっている間だけ毎フレーム少しずつ)
        if (this.boss && Math.abs(b.x) < 2.4 && Math.abs(b.z - this.boss.z) < 2.0) {
            this.boss.hp -= this.boss.max * 0.6 * dt * this.bossDmgMul(this.boss); // 海獣回避中は減衰
            this.spark(this.boss.obj.position.clone().setY(1.6), 0x8fffcf);
        }
        // 4. 壁系 hazard で往路なら折り返す (最初に当たった 1 個で反転)
        if (b.phase === "out") {
            for (const e of this.entities) {
                if (
                    e.type === "hazard" && !e.zone && !e.warp && !e.sky && !e.pull && !e.drift &&
                    Math.abs(b.x - e.wx) < e.halfW + 0.4 &&
                    Math.abs(b.z - e.z) < 1.2
                ) {
                    this.flipBoomerangBack(b);
                    break;
                }
            }
        }
        // 5. レーン端で跳ね返り (往復どちらでも)
        if (Math.abs(b.x) > LANE) {
            b.vx = -b.vx;
            b.x = Math.sign(b.x) * LANE;
        }
        // 6. 往路の自動折返し (寿命切れ or 奥行き到達)
        if (b.phase === "out" && (b.life <= 0 || b.z > BOOM_RANGE_Z)) this.flipBoomerangBack(b);
        // 7. 復路ホーミング: 動くプレイヤーへ毎フレーム再照準して曲がって戻る
        if (b.phase === "back") {
            const tx = this.centroidX;
            const tz = CROWD_Z + 0.5;
            const dirx = tx - b.x;
            const dirz = tz - b.z;
            const hl = Math.hypot(dirx, dirz) || 1;
            b.vx = (dirx / hl) * BOOM_RETURN_SPEED;
            b.vz = (dirz / hl) * BOOM_RETURN_SPEED;
            // 8. キャッチ: プレイヤーに十分近づいたら受け取って消す (= 再投擲可)
            if (hl < BOOM_CATCH_R) {
                this.disposeObj(b.mesh);
                this.boomerang = null;
                this.popWorld("CATCH", tx, "#8fffcf");
                this.spark(new THREE.Vector3(tx, 1.0, tz), 0x8fffcf);
                return;
            }
        }
        // 9. セーフティ: 寿命が大きく負になっても捕まらなければ強制回収 (詰み回避)
        if (b.life < -4) {
            this.disposeObj(b.mesh);
            this.boomerang = null;
        }
    }

    /** 最前の HP 敵 (雑魚/アーチャー/突進/ロケット/アイテム台) を 1 体返す。なければ null。 */
    private frontTarget(): HpEnt | null {
        let target: HpEnt | null = null;
        for (const e of this.entities) {
            if (
                (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item") &&
                e.hp > 0 && e.z > CROWD_Z + 1 && e.z < SPAWN_Z * 0.78
            ) {
                if (!target || e.z < target.z) target = e;
            }
        }
        return target;
    }

    /**
     * 援軍メカのホーミングミサイル。火力ブーストとは別の「おまけ」攻撃なのでダメージは控えめ。
     * 機体数ぶんスタッガして発射 → 対象へ毎フレーム再照準 (旋回率上限で弧) → 近接で起爆 (小 AoE)。
     * 対象なし (非戦闘) では撃たない。走行/ボス両フェーズで飛ばす。
     */
    private updateRobotMissiles(dt: number): void {
        if (this.isPartyArmor) return; // パーティゴアアーマー中は援軍ミサイルなし
        const rn = this.members.length > 0 ? Math.min(6, this.robots) : 0;
        // --- 発射: タイマーが切れて最前の対象があれば 1 発撃つ (機体数で間隔を前倒し) ---
        if (rn > 0 && this.missiles.length < rn) {
            this.robotFireT -= dt;
            if (this.robotFireT <= 0) {
                const target = this.frontTarget();
                if (target) {
                    this.spawnMissile(this.missiles.length % rn, target);
                    this.robotFireT = MISSILE_INTERVAL / Math.max(1, rn) + Math.random() * 0.3; // スタッガ
                } else {
                    this.robotFireT = 0.2; // 対象がいない間は短く待つ (出てきたらすぐ撃つ)
                }
            }
        }
        // --- 飛行・ホーミング・起爆 ---
        const baseDmg = this.firepower * FIRE_FACTOR * 2; // 援軍メカ弾の控えめな追加ダメージ
        const bvx = this.bossVisualX();
        for (let i = this.missiles.length - 1; i >= 0; i--) {
            const m = this.missiles[i];
            m.life -= dt;
            // 対象が生きていれば再照準して旋回。ランチャー弾は対象が無くてもボスを追う。
            if (m.target && m.target.hp > 0) {
                const dx = m.target.wx - m.x, dy = 1.0 - m.y, dz = m.target.z - m.z;
                const dl = Math.hypot(dx, dy, dz) || 1;
                const dvx = (dx / dl) * MISSILE_SPEED, dvy = (dy / dl) * MISSILE_SPEED, dvz = (dz / dl) * MISSILE_SPEED;
                const k = Math.min(1, MISSILE_TURN * dt); // 旋回率 → 線形補間係数
                m.vx += (dvx - m.vx) * k; m.vy += (dvy - m.vy) * k; m.vz += (dvz - m.vz) * k;
            } else if (m.launcher && this.boss) {
                const dx = bvx - m.x, dy = 3.0 - m.y, dz = this.boss.z - m.z; // ボスの見た目位置へ
                const dl = Math.hypot(dx, dy, dz) || 1;
                const k = Math.min(1, MISSILE_TURN * dt);
                m.vx += ((dx / dl) * MISSILE_SPEED - m.vx) * k; m.vy += ((dy / dl) * MISSILE_SPEED - m.vy) * k; m.vz += ((dz / dl) * MISSILE_SPEED - m.vz) * k;
            } else {
                m.target = null; // 対象消失 → 直進して寿命切れ
            }
            m.x += m.vx * dt; m.y += m.vy * dt; m.z += m.vz * dt;
            m.mesh.position.set(m.x, m.y, m.z);
            // 進行方向へ機首を向ける (コーンの軸 +z を速度へ)
            m.mesh.lookAt(m.x + m.vx, m.y + m.vy, m.z + m.vz);
            if ((this.trailTick & 1) === 0) this.trailEmit(m.mesh.position, m.launcher ? 0x9fe8ff : 0xff8a3b);
            // 起爆判定: 近接した HP 敵があれば爆発
            let hit: HpEnt | null = null;
            for (const e of this.entities) {
                if (
                    (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item") &&
                    e.hp > 0 && Math.abs(e.wx - m.x) < MISSILE_HIT_R && Math.abs(e.z - m.z) < MISSILE_HIT_R
                ) { hit = e; break; }
            }
            const bossHit = this.boss && Math.abs(bvx - m.x) < 1.8 && Math.abs(this.boss.z - m.z) < 1.8;
            if (hit || bossHit || m.life <= 0 || m.z > SPAWN_Z || m.z < CROWD_Z - 4) {
                if (hit || bossHit) this.explodeMissile(m, m.launcher ? 0 : baseDmg, hit);
                this.disposeObj(m.mesh);
                this.missiles.splice(i, 1);
            }
        }
    }

    /** 前面ダメージの適用。シールドロボはシールド残量が肩代わりし、割れてから本体へ通る。 */
    private damageEnemy(e: HpEnt, dmg: number): void {
        if (e.type === "bruiser" && e.variant === "shieldbot" && !e.shieldBroken && !e.botEscaping) {
            e.shieldHp = (e.shieldHp ?? 0) - dmg;
            if ((e.shieldHp ?? 0) <= 0) {
                e.shieldBroken = true;
                const y = e.lift?.position.y ?? 1.5;
                this.shockRing(new THREE.Vector3(e.wx, y, e.obj.position.z), 0x49e0ff, 4, 0.4);
                this.spark(new THREE.Vector3(e.wx, 1.4, e.obj.position.z), 0x49e0ff);
                this.popWorld("シールド破壊!", e.wx, "#49e0ff");
            }
            return;
        }
        e.hp -= dmg;
    }

    /** ボスの「見た目」ワールド x。leviathan は obj に、mech/smiler は root(parts.torso) にオフセットを乗せる。 */
    private bossVisualX(): number {
        const b = this.boss;
        if (!b) return 0;
        return b.obj.position.x + (b.parts.torso ? b.parts.torso.position.x : 0);
    }

    /** ミサイル 1 発を生成し、指定機体のホバー位置から対象へ向けて撃ち出す。 */
    private spawnMissile(idx: number, target: HpEnt): void {
        const side = (idx % 2 === 0 ? -1 : 1) * (1.6 + Math.floor(idx / 2) * 0.9);
        const ox = this.centroidX + side, oy = 2.3, oz = CROWD_Z - 1.6;
        const mesh = new THREE.Mesh(
            new THREE.ConeGeometry(0.1, 0.42, 7),
            new THREE.MeshBasicMaterial({ color: 0xcfe8ff }),
        );
        mesh.position.set(ox, oy, oz);
        this.scene.add(mesh);
        this.projGlow(mesh, 0xff8a3b, 0.9);
        const dx = target.wx - ox, dy = 1.0 - oy, dz = target.z - oz;
        const dl = Math.hypot(dx, dy, dz) || 1;
        this.missiles.push({
            mesh, x: ox, y: oy, z: oz,
            vx: (dx / dl) * MISSILE_SPEED, vy: (dy / dl) * MISSILE_SPEED, vz: (dz / dl) * MISSILE_SPEED,
            life: MISSILE_LIFE, target,
        });
        this.spark(new THREE.Vector3(ox, oy, oz), 0xff8a3b); // 発射の小フラッシュ
    }

    /** ミサイル起爆: 主対象 + 周囲 AoE にダメージ、ボス重なり時は削り、爆発 VFX。 */
    private explodeMissile(m: { x: number; y: number; z: number; launcher?: boolean }, dmg: number, primary: HpEnt | null): void {
        if (m.launcher) {
            // 追尾ミサイルランチャー: 命中した道中の敵は一撃で撃破 (シールドの正面防御も貫通) + 軽い AoE。
            if (primary) primary.hp = 0;
            for (const e of this.entities) {
                if (e === primary) continue;
                if (
                    (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item") &&
                    e.hp > 0 && Math.abs(e.wx - m.x) < MISSILE_AOE_R && Math.abs(e.z - m.z) < MISSILE_AOE_R
                ) e.hp = 0;
            }
            // ボス特効: mech (とシールドロボ) には大ダメージ、その他は控えめ。
            if (this.boss && Math.abs(this.bossVisualX() - m.x) < 2.0 && Math.abs(this.boss.z - m.z) < 2.0) {
                const sup = this.boss.kind === "mech" ? HOMING_SUPER_MUL : 1;
                this.boss.hp -= this.boss.max * HOMING_BOSS_FRAC * sup * this.bossDmgMul(this.boss);
            }
        } else {
            if (primary) primary.hp -= dmg;
            for (const e of this.entities) {
                if (e === primary) continue;
                if (
                    (e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket" || e.type === "item") &&
                    e.hp > 0 && Math.abs(e.wx - m.x) < MISSILE_AOE_R && Math.abs(e.z - m.z) < MISSILE_AOE_R
                ) e.hp -= dmg * 0.5;
            }
            if (this.boss && Math.abs(this.bossVisualX() - m.x) < 1.8 && Math.abs(this.boss.z - m.z) < 1.8) {
                this.boss.hp -= dmg * 0.6 * this.bossDmgMul(this.boss);
            }
        }
        const at = new THREE.Vector3(m.x, Math.max(0.9, m.y), m.z);
        this.boom(at);
        this.impactBurst(at, m.launcher ? "mech" : "crew", m.launcher ? 1.0 : 0.8);
        this.shake = Math.max(this.shake, m.launcher ? 0.18 : 0.1);
    }

    /** 追尾ミサイルランチャーの一斉発射: 狙いにくい敵 (飛来兵/シールドロボ) を優先し、最大 HOMING_VOLLEY 発。 */
    private fireHomingVolley(): void {
        const cand: HpEnt[] = [];
        for (const e of this.entities) {
            if ((e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket") &&
                e.hp > 0 && e.z > CROWD_Z + 0.5 && e.z < SPAWN_Z) cand.push(e);
        }
        // 飛来兵 / シールドロボ (= 飛行・シールド持ち) を最優先、その他は手前から。
        const pri = (e: HpEnt): number => (e.type === "bruiser" && (e.variant === "flyer" || e.variant === "shieldbot" || e.variant === "orbiter")) ? 0 : 1;
        cand.sort((a, b) => pri(a) - pri(b) || a.z - b.z);
        for (let i = 0; i < HOMING_VOLLEY; i++) {
            this.spawnLauncherMissile(i, cand[i] ?? cand[0] ?? null);
        }
        this.muzzle(0x9fe8ff);
        this.shake = Math.max(this.shake, 0.12);
    }

    private spawnLauncherMissile(idx: number, target: HpEnt | null): void {
        const side = (idx % 2 === 0 ? -1 : 1) * (0.8 + idx * 0.4);
        const ox = this.centroidX + side, oy = 1.4, oz = CROWD_Z + 0.5;
        const mesh = new THREE.Mesh(new THREE.ConeGeometry(0.14, 0.6, 8), new THREE.MeshBasicMaterial({ color: 0xeaf6ff }));
        mesh.position.set(ox, oy, oz);
        this.scene.add(mesh);
        this.projGlow(mesh, 0x9fe8ff, 1.0);
        // 初速は少し上向きに撃ち出してから対象へ旋回 (撃ち上げ感)
        const tx = target ? target.wx : this.bossVisualX();
        const tz = target ? target.z : (this.boss ? this.boss.z : SPAWN_Z * 0.5);
        const dx = tx - ox, dy = 3.0 - oy, dz = tz - oz;
        const dl = Math.hypot(dx, dy, dz) || 1;
        this.missiles.push({
            mesh, x: ox, y: oy, z: oz,
            vx: (dx / dl) * MISSILE_SPEED * 0.6, vy: MISSILE_SPEED * 0.5, vz: (dz / dl) * MISSILE_SPEED * 0.6,
            life: 2.6, target, launcher: true,
        });
        this.spark(new THREE.Vector3(ox, oy, oz), 0x9fe8ff);
    }

    // ---------- 敵ミサイル (mech / シールドロボ) ----------
    /** 敵ミサイルを発射: from から targetX(クルー列=CROWD_Z) へ飛来。床に予告リング。着弾で AoE。 */
    private spawnEnemyMissile(from: THREE.Vector3, targetX: number, opts: { R?: number; overhead?: boolean; col?: number; t?: number } = {}): void {
        const R = opts.R ?? 1.4, col = opts.col ?? 0xff7a3b, overhead = opts.overhead ?? false, t = opts.t ?? 1.0;
        const tx = Math.max(-(LANE - 0.4), Math.min(LANE - 0.4, targetX));
        const mesh = new THREE.Mesh(new THREE.ConeGeometry(0.16, 0.7, 8), new THREE.MeshBasicMaterial({ color: 0xffe2b0 }));
        mesh.position.copy(from);
        this.scene.add(mesh);
        this.projGlow(mesh, col, 1.1);
        // 床の予告リング (overhead=赤系で警告)
        const tell = new THREE.Mesh(
            new THREE.RingGeometry(R * 0.62, R, 26),
            new THREE.MeshBasicMaterial({ color: overhead ? 0xff4d4d : col, transparent: true, opacity: 0.32, depthWrite: false, side: THREE.DoubleSide }),
        );
        tell.rotation.x = -Math.PI / 2;
        tell.position.set(tx, 0.07, CROWD_Z);
        this.scene.add(tell);
        this.enemyMissiles.push({ mesh, tell, x: from.x, y: from.y, z: from.z, targetX: tx, targetZ: CROWD_Z, t, R, overhead, col });
        this.spark(from.clone(), col);
    }

    private updateEnemyMissiles(dt: number): void {
        const tt = this.clock.elapsedTime;
        // ロボのポイントディフェンス: robots が居れば飛来中の敵ミサイルを着弾前に迎撃して撃ち落とす。
        // 迎撃間隔は台数に反比例 (台数が多いほど高頻度)。全弾は止めない (取りこぼし有り) ＝盾を完全代替しない。
        if (this.robotPdT > 0) this.robotPdT -= dt;
        if (this.robots > 0 && this.robotPdT <= 0 && this.enemyMissiles.length > 0) {
            // 最も着弾が近い (m.t 最小) 弾を 1 発撃ち落とす。
            let bi = -1, bt = Infinity;
            for (let i = 0; i < this.enemyMissiles.length; i++) {
                if (this.enemyMissiles[i].t < bt) { bt = this.enemyMissiles[i].t; bi = i; }
            }
            if (bi >= 0) {
                const m = this.enemyMissiles[bi];
                this.boom(new THREE.Vector3(m.x, m.y, m.z)); // 空中で爆散 (着弾させない)
                this.impactBurst(new THREE.Vector3(m.x, m.y, m.z), "mech", 0.8);
                this.disposeObj(m.tell);
                this.disposeObj(m.mesh);
                this.enemyMissiles.splice(bi, 1);
                this.robotPdT = ROBOT_PD_INT / this.robots;
            }
        }
        for (let i = this.enemyMissiles.length - 1; i >= 0; i--) {
            const m = this.enemyMissiles[i];
            m.t -= dt;
            const k = Math.min(1, dt / Math.max(0.05, m.t + dt)); // 残り時間で着弾点へ詰める
            const ty = 0.7;
            m.x += (m.targetX - m.x) * k;
            m.y += (ty - m.y) * k;
            m.z += (m.targetZ - m.z) * k;
            m.mesh.position.set(m.x, m.y, m.z);
            m.mesh.lookAt(m.targetX, ty, m.targetZ);
            if ((this.trailTick & 1) === 0) this.trailEmit(m.mesh.position, m.col);
            (m.tell.material as THREE.MeshBasicMaterial).opacity = 0.25 + 0.35 * Math.abs(Math.sin(tt * 14));
            if (m.t <= 0 || (Math.abs(m.x - m.targetX) < 0.15 && Math.abs(m.z - m.targetZ) < 0.4)) {
                this.boom(new THREE.Vector3(m.targetX, 1.0, m.targetZ));
                this.collideCrowd(m.targetX, m.R, false, m.overhead); // 着弾点に居たら被弾 (横へ逃げれば無傷)
                this.disposeObj(m.tell);
                this.disposeObj(m.mesh);
                this.enemyMissiles.splice(i, 1);
            }
        }
    }

    private clearEnemyMissiles(): void {
        for (const m of this.enemyMissiles) { this.disposeObj(m.tell); this.disposeObj(m.mesh); }
        this.enemyMissiles.length = 0;
    }

    // ---------- 360°タレット ----------
    /** タレットを展開 (既にあれば耐久を満タンに補給)。群衆に追従し全方位の最寄り敵を撃つ。 */
    private acquireTurret(): void {
        if (this.turret) { this.turret.hp = this.turret.max; this.showBanner("🔧 タレット補給"); return; }
        const obj = new THREE.Group();
        const steel = new THREE.MeshStandardMaterial({ color: 0x3c4a5e, roughness: 0.4, metalness: 0.72, envMapIntensity: 1 });
        const trim = new THREE.MeshStandardMaterial({ color: 0xc8a23a, roughness: 0.34, metalness: 0.8, envMapIntensity: 1 }); // 金のトリム
        const glow = new THREE.MeshBasicMaterial({ color: 0xffe24a });
        // 脚 (三脚) + 台座
        for (let i = 0; i < 3; i++) {
            const a = (i / 3) * Math.PI * 2;
            const leg = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 0.9, 6), steel);
            leg.position.set(Math.cos(a) * 0.36, 0.45, Math.sin(a) * 0.36);
            leg.rotation.z = Math.cos(a) * 0.28; leg.rotation.x = -Math.sin(a) * 0.28;
            obj.add(leg);
        }
        const base = new THREE.Mesh(new THREE.CylinderGeometry(0.42, 0.5, 0.3, 16), steel);
        base.position.y = 0.95; obj.add(base);
        // 旋回ヘッド (yaw・360°回す)
        const yaw = new THREE.Object3D();
        yaw.position.y = 1.15; obj.add(yaw);
        const body = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.5, 0.7), steel);
        yaw.add(body);
        const core = new THREE.Mesh(new THREE.SphereGeometry(0.16, 12, 10), glow); // 光るコア
        core.position.set(0, 0.05, 0); yaw.add(core);
        for (const dx of [-0.45, 0.45]) { // 弾倉ドラム (両脇)
            const drum = new THREE.Mesh(new THREE.CylinderGeometry(0.18, 0.18, 0.34, 12), trim);
            drum.rotation.z = Math.PI / 2; drum.position.set(dx, 0.05, -0.1); yaw.add(drum);
        }
        // 仰角ノード (pitch) + 砲身 (+z を向く) + 砲口コア
        const pitch = new THREE.Object3D();
        pitch.position.set(0, 0.08, 0.1); yaw.add(pitch);
        const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.13, 1.0, 12), trim);
        barrel.rotation.x = Math.PI / 2; barrel.position.set(0, 0, 0.55); pitch.add(barrel);
        const barrel2 = new THREE.Mesh(new THREE.CylinderGeometry(0.07, 0.07, 0.9, 10), steel);
        barrel2.rotation.x = Math.PI / 2; barrel2.position.set(0, 0.16, 0.5); pitch.add(barrel2);
        const muzzle = new THREE.Mesh(new THREE.SphereGeometry(0.13, 12, 10), new THREE.MeshBasicMaterial({ color: 0xfff0a0, transparent: true, opacity: 0.85 }));
        muzzle.position.set(0, 0, 1.1); pitch.add(muzzle);
        const bar = this.makeBarSprite();
        bar.position.set(0, 2.0, 0);
        obj.add(bar);
        obj.position.set(this.centroidX, 0, CROWD_Z - 0.4);
        this.scene.add(obj);
        this.turret = { obj, yaw, pitch, muzzle, hp: TURRET_MAX_HP, max: TURRET_MAX_HP, bar, fireT: 0 };
        this.showBanner("🛡 360°タレット 展開！", "全方位を撃ち落とす");
    }

    /** タレットの毎フレーム: 群衆へ追従 → 最寄りの敵へ旋回 → 周期射撃 (耐久を消費) → 0 で破壊。 */
    private updateTurret(dt: number): void {
        const tr = this.turret;
        if (!tr) return;
        if (this.isPartyArmor) { tr.obj.visible = false; return; } // パーティゴアアーマー中は援軍タレットを止める
        tr.obj.visible = true;
        // 群衆へ横追従 (操作に従って移動)。
        tr.obj.position.x += (this.centroidX - tr.obj.position.x) * Math.min(1, dt * 8);
        tr.obj.position.z = CROWD_Z - 0.4;
        const tx = tr.obj.position.x, tz = tr.obj.position.z;
        // 最寄りの生存敵を全方位から探索 (前/後/横/飛行)。アイテム台は撃たない。
        let best: HpEnt | null = null, bestD = TURRET_RANGE * TURRET_RANGE;
        let bestY = 0.8;
        for (const e of this.entities) {
            if (!(e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket")) continue;
            if (e.hp <= 0) continue;
            const ez = e.obj.position.z;
            const dx = e.wx - tx, dz = ez - tz;
            const d = dx * dx + dz * dz;
            if (d < bestD) {
                bestD = d; best = e;
                bestY = (e.type === "bruiser" && (e.variant === "flyer" || e.variant === "shieldbot") && e.flyLift) ? e.flyLift.position.y + 0.4
                    : (e.type === "bruiser" && e.variant === "shieldbot" && e.lift) ? e.lift.position.y + 0.2
                        : (e.type === "bruiser" && e.variant === "orbiter" && e.lift) ? e.lift.position.y + 0.2 : 0.8;
            }
        }
        // 旋回/仰角を目標へ滑らかに追従。
        if (best) {
            const dx = best.wx - tx, dz = best.obj.position.z - tz;
            const horiz = Math.hypot(dx, dz) || 0.001;
            const targetYaw = Math.atan2(dx, dz);
            const targetPitch = -Math.atan2(bestY - 1.25, horiz);
            // 角度を最短回りで補間
            let dyaw = targetYaw - tr.yaw.rotation.y;
            while (dyaw > Math.PI) dyaw -= Math.PI * 2;
            while (dyaw < -Math.PI) dyaw += Math.PI * 2;
            tr.yaw.rotation.y += dyaw * Math.min(1, dt * 10);
            tr.pitch.rotation.x += (targetPitch - tr.pitch.rotation.x) * Math.min(1, dt * 10);
        }
        // 射撃 (照準が概ね合っていて耐久があれば)。
        tr.fireT -= dt;
        const mat = tr.muzzle.material as THREE.MeshBasicMaterial;
        if (best && tr.fireT <= 0 && tr.hp > 0) {
            tr.fireT = TURRET_FIRE_INT;
            const dmg = (this.crew * 0.05 + 9) * this.stageMul; // 武器非依存 (常に頼れる火力)
            this.damageEnemy(best, dmg); // シールドロボはシールドが肩代わり
            tr.hp -= TURRET_SHOT_COST;
            // 砲口閃光 + トレーサー弾 (見た目) + 着弾スパーク
            mat.opacity = 1;
            tr.muzzle.getWorldPosition(this._v);
            const tracer = new THREE.Mesh(new THREE.SphereGeometry(0.1, 6, 5), new THREE.MeshBasicMaterial({ color: 0xffe24a }));
            tracer.position.copy(this._v);
            this.scene.add(tracer);
            const dest = new THREE.Vector3(best.wx, bestY, best.obj.position.z);
            const vel = dest.sub(this._v).normalize().multiplyScalar(46);
            this.bullets.push({ mesh: tracer, vel, life: 0.4, trailCol: 0xffe24a });
            this.spark(new THREE.Vector3(best.wx, bestY, best.obj.position.z), 0xffe24a);
            this.sfx.play("turret");
        } else {
            mat.opacity = 0.5 + 0.3 * Math.abs(Math.sin(this.clock.elapsedTime * 4)); // アイドルの脈動
        }
        // 耐久バー + 破壊。
        tr.bar.scale.x = 1.6 * Math.max(0, tr.hp / tr.max);
        (tr.bar.material as THREE.SpriteMaterial).color.setHex(tr.hp < tr.max * 0.3 ? 0xff6a6a : 0xffd24a);
        if (tr.hp <= 0) {
            this.impactBurst(tr.obj.position.clone().setY(1.2), "mech", 1.2);
            this.shockRing(tr.obj.position.clone().setY(0.6), 0xffd24a, 5, 0.5);
            this.popWorld("タレット破壊!", tr.obj.position.x, "#ffd24a");
            this.clearTurret();
        }
    }

    private clearTurret(): void {
        if (this.turret) { this.disposeObj(this.turret.obj); this.turret = null; }
    }

    // ---------- 周回ガンドローン ----------
    private grantDrone(): void {
        this.drones = Math.min(DRONE_MAX, this.drones + 1);
        this.rebuildDrones();
        this.showBanner(`🛸 ガンドローン ×${this.drones}`, "群れで全方位を撃つ");
    }

    private rebuildDrones(): void {
        for (const d of this.droneRig) this.disposeObj(d.obj);
        this.droneRig.length = 0;
        const steel = new THREE.MeshStandardMaterial({ color: 0x46566a, roughness: 0.4, metalness: 0.72, envMapIntensity: 1 });
        const trim = new THREE.MeshStandardMaterial({ color: 0x9fc8e8, roughness: 0.35, metalness: 0.7 });
        for (let i = 0; i < this.drones; i++) {
            const obj = new THREE.Group();
            const hull = new THREE.Mesh(new THREE.SphereGeometry(0.28, 12, 10), steel);
            hull.scale.set(1, 0.7, 1); obj.add(hull);
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.1, 8, 8), new THREE.MeshBasicMaterial({ color: 0x6fe0ff }));
            eye.position.set(0, 0.06, 0); obj.add(eye);
            for (const s of [-1, 1]) { // 小さなローター/フィン
                const fin = new THREE.Mesh(new THREE.BoxGeometry(0.34, 0.04, 0.12), trim);
                fin.position.set(s * 0.3, 0.08, 0); obj.add(fin);
            }
            const yaw = new THREE.Object3D(); obj.add(yaw);
            const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.06, 0.5, 8), trim);
            barrel.rotation.x = Math.PI / 2; barrel.position.set(0, -0.02, 0.3); yaw.add(barrel);
            const muzzle = new THREE.Mesh(new THREE.SphereGeometry(0.07, 8, 6), new THREE.MeshBasicMaterial({ color: 0xaef0ff, transparent: true, opacity: 0.85 }));
            muzzle.position.set(0, -0.02, 0.55); yaw.add(muzzle);
            this.scene.add(obj);
            this.droneRig.push({ obj, yaw, muzzle, fireT: i * 0.12 });
        }
    }

    /** ドローンの毎フレーム: 群衆を旋回しながら各機が最寄りの敵へ旋回 → 周期射撃。 */
    private updateDrones(dt: number): void {
        if (this.droneRig.length === 0) return;
        if (this.isPartyArmor) { for (const d of this.droneRig) d.obj.visible = false; return; } // 援軍ドローンを止める
        const t = this.clock.elapsedTime;
        const n = this.droneRig.length;
        for (const d of this.droneRig) d.obj.visible = true;
        for (let i = 0; i < n; i++) {
            const d = this.droneRig[i];
            const a = t * 1.0 + (i / n) * Math.PI * 2; // 群れで等間隔に周回
            const dx2 = this.centroidX + Math.cos(a) * DRONE_ORBIT_R;
            const dz2 = CROWD_Z + Math.sin(a) * DRONE_ORBIT_R;
            d.obj.position.set(dx2, 1.7 + Math.sin(t * 3 + i) * 0.12, dz2);
            // 最寄りの生存敵へ旋回 (この機から見て)。
            let best: HpEnt | null = null, bestD = TURRET_RANGE * TURRET_RANGE, bestY = 0.8;
            for (const e of this.entities) {
                if (!(e.type === "mob" || e.type === "archer" || e.type === "bruiser" || e.type === "rocket") || e.hp <= 0) continue;
                const ex = e.wx - dx2, ez = e.obj.position.z - dz2, dd = ex * ex + ez * ez;
                if (dd < bestD) {
                    bestD = dd; best = e;
                    bestY = (e.type === "bruiser" && e.lift) ? e.lift.position.y + 0.2
                        : (e.type === "bruiser" && e.flyLift) ? e.flyLift.position.y + 0.2 : 0.8;
                }
            }
            const mat = d.muzzle.material as THREE.MeshBasicMaterial;
            if (best) {
                const ax = best.wx - dx2, az = best.obj.position.z - dz2;
                const targetYaw = Math.atan2(ax, az);
                let dyaw = targetYaw - d.yaw.rotation.y;
                while (dyaw > Math.PI) dyaw -= Math.PI * 2; while (dyaw < -Math.PI) dyaw += Math.PI * 2;
                d.yaw.rotation.y += dyaw * Math.min(1, dt * 12);
                d.fireT -= dt;
                if (d.fireT <= 0) {
                    d.fireT = DRONE_FIRE_INT;
                    const dmg = (this.crew * 0.03 + 6) * this.stageMul; // 1 機は控えめ (台数で稼ぐ)
                    this.damageEnemy(best, dmg);
                    mat.opacity = 1;
                    d.muzzle.getWorldPosition(this._v);
                    const tracer = new THREE.Mesh(new THREE.SphereGeometry(0.08, 6, 5), new THREE.MeshBasicMaterial({ color: 0xaef0ff }));
                    tracer.position.copy(this._v); this.scene.add(tracer);
                    const vel = new THREE.Vector3(best.wx, bestY, best.obj.position.z).sub(this._v).normalize().multiplyScalar(46);
                    this.bullets.push({ mesh: tracer, vel, life: 0.35, trailCol: 0x6fe0ff });
                    this.sfx.play("turret");
                }
            } else {
                mat.opacity = 0.5 + 0.3 * Math.abs(Math.sin(t * 4 + i));
            }
        }
    }

    private clearDrones(): void {
        for (const d of this.droneRig) this.disposeObj(d.obj);
        this.droneRig.length = 0;
        this.drones = 0;
    }

    private fireArrow(a: ArcherEnt): void {
        const mesh = new THREE.Mesh(
            new THREE.CylinderGeometry(0.04, 0.04, 0.8, 5),
            new THREE.MeshLambertMaterial({ color: 0x5a3a1c }),
        );
        mesh.rotation.x = Math.PI / 2;
        mesh.position.set(a.wx, 1.0, a.z);
        this.scene.add(mesh);
        const targetWx = this.centroidX + (Math.random() - 0.5) * 1.0;
        this.arrows.push({ mesh, wx: targetWx, z: a.z, resolved: false, halfW: 0.45, blast: false });
    }

    /** 扇状シューターの 3 連弾: プレイヤーを中心に左/中/右へ撃つ。隙間に寄って避ける。 */
    private fireSpread(a: ArcherEnt): void {
        for (const off of [-2.3, 0, 2.3]) {
            const mesh = new THREE.Mesh(
                new THREE.CylinderGeometry(0.05, 0.05, 0.7, 5),
                new THREE.MeshBasicMaterial({ color: 0xb060ff }),
            );
            mesh.rotation.x = Math.PI / 2;
            mesh.position.set(a.wx, 1.0, a.z);
            this.scene.add(mesh);
            const targetWx = Math.max(-(LANE - 0.3), Math.min(LANE - 0.3, this.centroidX + off));
            this.arrows.push({ mesh, wx: targetWx, z: a.z, resolved: false, halfW: 0.5, blast: false, trailCol: 0xb060ff });
        }
        this.spark(new THREE.Vector3(a.wx, 1.0, a.z), 0xb060ff);
    }

    /** 砲撃手 (bomber) の爆風弾: 着弾点に広範囲ダメージを落とす。 */
    private fireBlast(a: ArcherEnt): void {
        const mesh = new THREE.Mesh(
            new THREE.SphereGeometry(0.32, 8, 6),
            new THREE.MeshLambertMaterial({ color: 0x2b2f36 }),
        );
        mesh.position.set(a.wx, 1.2, a.z);
        this.scene.add(mesh);
        const targetWx = this.centroidX + (Math.random() - 0.5) * 1.4;
        this.arrows.push({ mesh, wx: targetWx, z: a.z, resolved: false, halfW: 1.4, blast: true });
    }

    /** ボスの攻撃弾: クルー方向へ赤い弾を飛ばし、着弾で dmg 体ぶんのクルーを削る (回避不可)。 */
    /** 確定チップの小弾 (回避不可だが CHIP_FRAC_PER_S で上限済 → 一撃死しない)。 */
    private fireBossShot(from: THREE.Vector3, dmg: number): void {
        const capped = Math.min(dmg, Math.ceil(this.crew * 0.04) + 1); // 念のための一撃死ガード
        const mesh = new THREE.Mesh(
            new THREE.SphereGeometry(0.3, 8, 6),
            new THREE.MeshBasicMaterial({ color: 0xff3b30 }),
        );
        mesh.position.set(from.x, 1.4, from.z);
        this.scene.add(mesh);
        const glow = this.projGlow(mesh, 0xff5a4f);
        this.arrows.push({ mesh, wx: this.centroidX, z: from.z, resolved: false, halfW: 0.45, blast: false, dmg: capped, bossSpeed: true, trailCol: 0xff5a4f, glow });
        this.spark(from.clone().setY(1.6), 0xff5a4f);
    }

    private updateArrows(dt: number): void {
        const hazardSp = this.speed * 2.4;
        for (let i = this.arrows.length - 1; i >= 0; i--) {
            const s = this.arrows[i];
            const sp = s.slow ? this.speed * 1.25 : s.bossSpeed ? BOSS_ARROW_SPEED : hazardSp; // 追尾オーブは低速 / ボス弾は一定速
            s.z -= sp * dt;
            // 追尾: プレイヤーの横位置へ寄る (上限付き → 決め打ちで横へ走れば振り切れる)
            if (s.homing) {
                const maxStep = 3.2 * dt;
                s.wx += THREE.MathUtils.clamp(this.centroidX - s.wx, -maxStep, maxStep);
            }
            // 撃ち落とし: 飛行中に firepower で hp を削る (火力 = 防御の第2レバー)
            if (s.hp != null && !s.resolved) {
                s.hp -= this.firepower * FIRE_FACTOR * dt;
                if (s.hp <= 0) {
                    this.boom(s.mesh.position.clone().setY(1.0));
                    this.disposeObj(s.mesh);
                    this.arrows.splice(i, 1);
                    continue;
                }
            }
            // sweep: 進行度で wx を補間 (動く壁/笏)。spawn z = ボス停止 z(18) で正規化。
            if (s.sweep) s.wx = THREE.MathUtils.lerp(s.sweep.from, s.sweep.to, THREE.MathUtils.clamp(1 - s.z / 18, 0, 1));
            s.mesh.position.set(THREE.MathUtils.lerp(s.mesh.position.x, s.wx, 0.2), 1.0, s.z);
            // 尾: ボスのヘビー弾は毎フレーム引く (色付きのトレイル)
            if (s.trailCol != null) this.trailEmit(s.mesh.position, s.trailCol, s.trailEl);
            // 壁の遮蔽: 進路上の固体壁に当たった敵弾は止まる (敵味方共通の遮蔽)。
            if (!s.resolved && this.wallBlocks(s.wx, s.z, s.z)) {
                this.spark(s.mesh.position.clone().setY(1.0), 0xcfd6e0);
                this.disposeObj(s.mesh);
                this.arrows.splice(i, 1);
                continue;
            }
            if (!s.resolved && s.z <= CROWD_Z) {
                s.resolved = true;
                if (s.feint) {
                    // 囮レーン: 当たり判定なし (見た目だけ)
                } else if (s.dmg != null) {
                    // 確定チップ弾: 着弾分だけクルー減 (回避不可・小さく上限済)
                    this.changeCrew(-s.dmg);
                    this.impactBurst(new THREE.Vector3(this.centroidX, 1.0, CROWD_Z), "crew", 0.5);
                    this.popWorld(`-${s.dmg}`, this.centroidX, "#ff8f8f");
                    this.shake = Math.max(this.shake, 0.07);
                } else {
                    if (s.blast) this.boom(s.mesh.position.clone().setY(1.0)); // 爆風弾は着弾で爆発
                    if (s.bossHit) this.impactBurst(s.mesh.position.clone().setY(1.0), this.boss?.kind ?? "crew", Math.min(2, s.halfW)); // ボスヘビーの着弾
                    this.collideCrowd(s.wx, s.halfW, s.bossHit === true); // 触れた列だけ消滅 (避ければ無傷)
                }
                if (this.crew <= 0) this.defeat();
            }
            if (s.z < -6) {
                this.disposeObj(s.mesh);
                this.arrows.splice(i, 1);
            }
        }
    }

    // ---------- ボス ----------
    private startBattle(): void {
        this.phase = "battle";
        this.clearSecretGap(); // ボス戦に裂け目を持ち越さない
        this.clearZoneModifiers(); // C/D の一時モディファイアを解除 (ビュー/操作を元に戻す)
        this.returnAllRecruited(); // 一掃前に連れ去られたクルーを取り返す
        this.smilerExitDark(); // 前ステージが smiler 消灯中でも照明を戻す
        this.clearEnemyMissiles(); // 道中のシールドロボのミサイルを持ち越さない
        for (const e of this.entities) { if (e.type === "bruiser" && e.pounceTell) this.disposeObj(e.pounceTell); this.disposeObj(e.obj); }
        this.entities.length = 0;

        // ステージごとに king → dragon → angel → leviathan を巡回 (序盤から種類が変わる)。
        const kind = BOSS_ROTATION[(this.stage - 1) % BOSS_ROTATION.length];
        const { obj: grp, bar, spin, tell, jaw, parts } = this.buildBoss(kind);
        this.scene.add(grp);
        // HP は控えめ＆フラット底を厚めに (削りでなく回避で勝つ設計)。上限はソフトキャップ担当。
        const startZ = 42;
        const hpMul = kind === "dragon" ? 1.12 : kind === "angel" ? 0.9 : 1;
        const max = Math.ceil((40 + this.crew * 0.5 + this.distance * 0.1) * this.stageMul * hpMul);
        this.boss = {
            obj: grp, hp: max, max, z: startZ, bar, kind, windup: 0, spin,
            phase: 0, heavyTimer: 1.2, heavyCarry: 0, patternIdx: 0, tell, jaw,
            strikeT: 0, idlePh: Math.random() * Math.PI * 2, parts,
            // 海獣サブステート (他ボスは未使用)。obj は毎回作り直すのでオフセットの持ち越しは無い。
            levMode: kind === "leviathan" ? "evade" : undefined,
            levT: 0, levPhaseT: 0, levStrafe: 0, levChargeZ: 0, levTilt: 0,
            // mech サブステート
            mechX: 0, mechTargetX: 0, mechDashT: 0, mechHover: 0, mechMelee: "none", mechMeleeT: 0, mechMeleeX: 0,
            // smiler サブステート (点灯状態で開始)
            smilerLight: true, smilerLightT: 3.0, smilerLungeT: 0, smilerLungeX: 0, smilerFromX: 0,
        };
        this.battleTime = 0;
        this.drainCarry = 0;
        this.bossFireTimer = kind === "angel" ? 0.6 : 0.9; // チップの間合い (小さな確定削り)
        grp.position.set(0, 0, startZ);
        this.updateBossBar();
        this.showBanner(`⚔ STAGE ${this.stage} ${BOSS_NAME[kind]} ⚔`);
        this.sfx.setMood("boss"); this.sfx.play("boss");
    }

    /** 種類ごとのボスモデルを組み立てる。spin は毎フレーム回す/羽ばたかせるパーツ。 */
    private buildBoss(kind: BossKind): BossBuild {
        if (kind === "dragon") return this.buildDragonBoss();
        if (kind === "angel") return this.buildAngelBoss();
        if (kind === "leviathan") return this.buildLeviathanBoss();
        if (kind === "chronos") return this.buildChronosBoss();
        if (kind === "mech") return this.buildMechBoss();
        if (kind === "smiler") return this.buildSmilerBoss();
        return this.buildKingBoss();
    }

    /** 時の番人ボス: 浮遊する大時計。文字盤・金縁・12 時刻マーク・止まる針 (spin)・外周歯車・核 (tell)。 */
    private buildChronosBoss(): BossBuild {
        const grp = new THREE.Group();
        const dark = new THREE.MeshLambertMaterial({ color: 0x3a2f5a });
        const gold = new THREE.MeshLambertMaterial({ color: 0xcaa23a });
        const faceMat = new THREE.MeshLambertMaterial({ color: 0xe8e2f5 });
        const CY = 3.4; // 時計の中心高さ
        // 文字盤 (円盤・クルー側 -z を向く)
        const face = new THREE.Mesh(new THREE.CylinderGeometry(2.2, 2.2, 0.45, 32), faceMat);
        face.rotation.x = Math.PI / 2;
        face.position.set(0, CY, 0);
        grp.add(face);
        // 金の縁
        const rim = new THREE.Mesh(new THREE.TorusGeometry(2.25, 0.2, 12, 36), gold);
        rim.position.set(0, CY, -0.24);
        grp.add(rim);
        // 12 時刻マーク (文字盤の前面 -z 側に置く)
        for (let i = 0; i < 12; i++) {
            const a = (i / 12) * Math.PI * 2;
            const mk = new THREE.Mesh(new THREE.BoxGeometry(i % 3 === 0 ? 0.18 : 0.1, 0.34, 0.06), dark);
            mk.position.set(Math.cos(a) * 1.82, CY + Math.sin(a) * 1.82, -0.28);
            mk.rotation.z = a;
            grp.add(mk);
        }
        // 針 (短針/長針) — 中心 pivot から上に伸ばし rotation.z で回す/止める
        const mkHand = (len: number, w: number, col: number): THREE.Object3D => {
            const pivot = new THREE.Object3D();
            pivot.position.set(0, CY, -0.34);
            const hand = new THREE.Mesh(new THREE.BoxGeometry(w, len, 0.06), new THREE.MeshLambertMaterial({ color: col }));
            hand.position.y = len / 2; // 下端を中心に
            pivot.add(hand);
            grp.add(pivot);
            return pivot;
        };
        const hourHand = mkHand(1.0, 0.14, 0x2a2440);
        const minHand = mkHand(1.6, 0.1, 0x2a2440);
        hourHand.rotation.z = -0.8;
        minHand.rotation.z = 1.4;
        // 中心の留め + 光る核 (tell)
        const hubMat = gold;
        const hub = new THREE.Mesh(new THREE.SphereGeometry(0.2, 12, 10), hubMat);
        hub.position.set(0, CY, -0.4);
        grp.add(hub);
        const core = new THREE.Mesh(new THREE.SphereGeometry(0.5, 14, 12),
            new THREE.MeshBasicMaterial({ color: 0xc0a0ff, transparent: true, opacity: 0.55 }));
        core.position.set(0, CY, 0.1);
        grp.add(core);
        // 外周歯車 (背面・ゆっくり回る spin)
        const gear = new THREE.Group();
        for (let i = 0; i < 16; i++) {
            const a = (i / 16) * Math.PI * 2;
            const tooth = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.3, 0.3), gold);
            tooth.position.set(Math.cos(a) * 2.6, CY + Math.sin(a) * 2.6, 0.3);
            gear.add(tooth);
        }
        grp.add(gear);
        // 浮遊台座 (砂時計の脚)
        const stand = new THREE.Mesh(new THREE.ConeGeometry(1.0, 1.6, 6), dark);
        stand.position.set(0, 1.0, 0.2);
        grp.add(stand);
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 6.6, 0);
        grp.add(bar);
        return { obj: grp, bar, spin: [hourHand, minHand, gear], tell: core, jaw: null, parts: { body: face } };
    }

    /** 王ボス: 紫の鎧＋王冠＋笏＋胸の宝石 (tell)、背後に取り巻き 16 体の弧。 */
    private buildKingBoss(): BossBuild {
        const grp = new THREE.Group();
        const purple = new THREE.MeshLambertMaterial({ color: 0x6b4a8f });
        const gold = new THREE.MeshLambertMaterial({ color: 0xcaa23a });
        // 護衛 16 体 (背後の弧・ゆっくり回る spin[1])
        const guards = new THREE.Group();
        const im = new THREE.InstancedMesh(this.enemyGeo, this.crewMat, 16);
        for (let i = 0; i < 16; i++) {
            const a = (i / 16) * Math.PI * 2;
            const r = 5.0 + (i % 2) * 0.7;
            this.dummy.position.set(Math.cos(a) * r, 0, Math.sin(a) * r + 1.5);
            this.dummy.rotation.set(0, Math.PI, 0);
            this.dummy.scale.setScalar(0.9);
            this.dummy.updateMatrix();
            im.setMatrixAt(i, this.dummy.matrix);
        }
        im.instanceMatrix.needsUpdate = true;
        guards.add(im);
        grp.add(guards);
        // 脚 (接地・grp 直付け＝腰から下は動かさない)
        for (const dx of [-0.6, 0.6]) {
            const leg = new THREE.Mesh(new THREE.BoxGeometry(0.7, 2.0, 0.7), purple);
            leg.position.set(dx, 1.0, 0);
            grp.add(leg);
        }
        // 腰 pivot (torso): 上半身を丸ごとここにぶら下げ、攻撃で全身を捻る/前傾させる (full-body モーション)。
        // 子は元のワールド y から PIVOT_Y を引いてローカルに置く (見た目の初期位置は不変)。
        const PIVOT_Y = 2.2; // 腰の高さ
        const torso = new THREE.Object3D();
        torso.position.set(0, PIVOT_Y, 0);
        grp.add(torso);
        // 胴＋マント
        const body = new THREE.Mesh(new THREE.BoxGeometry(2.6, 3.0, 1.6), purple);
        body.scale.z = 0.8;
        body.position.set(0, 3.4 - PIVOT_Y, 0);
        torso.add(body);
        const cape = new THREE.Mesh(new THREE.BoxGeometry(2.4, 3.4, 0.18), new THREE.MeshLambertMaterial({ color: 0x3a2750 }));
        cape.position.set(0, 3.2 - PIVOT_Y, 0.9);
        torso.add(cape);
        for (const dx of [-1.7, 1.7]) {
            const p = new THREE.Mesh(new THREE.SphereGeometry(0.9, 10, 8), gold);
            p.scale.set(1, 0.6, 1);
            p.position.set(dx, 4.5 - PIVOT_Y, 0);
            torso.add(p);
        }
        // 胸の宝石 (tell)
        const gem = new THREE.Mesh(new THREE.OctahedronGeometry(0.34), new THREE.MeshBasicMaterial({ color: 0xff4d4d, transparent: true, opacity: 0.9 }));
        gem.position.set(0, 3.9 - PIVOT_Y, -0.9);
        torso.add(gem);
        // 頭＋目
        const head = new THREE.Mesh(new THREE.SphereGeometry(0.7, 14, 12), new THREE.MeshLambertMaterial({ color: 0xe0c9a0 }));
        head.position.set(0, 5.6 - PIVOT_Y, 0);
        torso.add(head);
        for (const dx of [-0.25, 0.25]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.1, 8, 8), new THREE.MeshBasicMaterial({ color: 0xffe34d }));
            eye.position.set(dx, 5.6 - PIVOT_Y, -0.62);
            torso.add(eye);
        }
        // 王冠
        const band = new THREE.Mesh(new THREE.TorusGeometry(0.62, 0.1, 8, 20), gold);
        band.rotation.x = Math.PI / 2;
        band.position.set(0, 6.2 - PIVOT_Y, 0);
        torso.add(band);
        for (let i = 0; i < 5; i++) {
            const a = (i / 5) * Math.PI * 2;
            const spike = new THREE.Mesh(new THREE.ConeGeometry(0.16, i === 0 ? 1.1 : 0.7, 4), gold);
            spike.position.set(Math.cos(a) * 0.6, 6.5 - PIVOT_Y, Math.sin(a) * 0.6);
            torso.add(spike);
        }
        // 腕: 肩→肘→手 の 3 関節 FK リグ (torso の子)。右腕だけが笏を振り、左腕は添えるだけ (片手攻撃)。
        // 各セグメントは親の先端からぶら下げ、肩の y は腰 pivot 相対に置く。
        const buildArm = (dx: number): { shoulder: THREE.Object3D; elbow: THREE.Object3D; hand: THREE.Object3D } => {
            const shoulder = new THREE.Object3D();
            shoulder.position.set(dx, 4.4 - PIVOT_Y, 0); // 肩 (腰 pivot 相対)
            const upper = new THREE.Mesh(new THREE.BoxGeometry(0.5, 1.4, 0.5), purple);
            upper.position.set(0, -0.7, 0); // 上腕 (長さ1.4の中央)
            shoulder.add(upper);
            const elbow = new THREE.Object3D();
            elbow.position.set(0, -1.4, 0); // 上腕の先 = 肘
            shoulder.add(elbow);
            const fore = new THREE.Mesh(new THREE.BoxGeometry(0.44, 1.3, 0.44), purple);
            fore.position.set(0, -0.65, 0); // 前腕
            elbow.add(fore);
            const hand = new THREE.Object3D();
            hand.position.set(0, -1.3, 0); // 前腕の先 = 手
            elbow.add(hand);
            const fist = new THREE.Mesh(new THREE.BoxGeometry(0.52, 0.5, 0.52), gold); // 籠手
            hand.add(fist);
            torso.add(shoulder);
            return { shoulder, elbow, hand };
        };
        const armLrig = buildArm(-1.6);
        const armRrig = buildArm(1.6);
        // 笏: 右手に握らせる (手の動きへ完全追従 → 振り下ろしで笏が一緒に走る)。手ローカルで上へ伸ばす。
        const scepter = new THREE.Group();
        scepter.add(new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.1, 4, 8), gold));
        const orb = new THREE.Mesh(new THREE.OctahedronGeometry(0.5), new THREE.MeshBasicMaterial({ color: 0xff5a4f }));
        orb.position.y = 2.2;
        scepter.add(orb);
        scepter.position.set(0, 1.2, 0); // 握り: 手から上にシャフトが伸びる
        armRrig.hand.add(scepter);
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 7.6, 0);
        grp.add(bar);
        return {
            obj: grp, bar, spin: [scepter, guards], tell: gem, jaw: null,
            parts: {
                body, cape, torso, armL: armLrig.shoulder, armR: armRrig.shoulder,
                elbowL: armLrig.elbow, elbowR: armRrig.elbow, handL: armLrig.hand, handR: armRrig.hand,
            },
        };
    }

    /**
     * 巨大ロボ「OVERSEER」: ジェットパックで常時浮遊する人型メカ。FK の右腕(拳叩き)・右脚(蹴り)・
     * 肩のミサイルポッド・光るバイザー(tell)・背中のスラスター(jet)。strafe/hover は root ノードで動かす
     * (boss.obj.position は毎フレ (0,0,z) に上書きされるので、横移動/浮遊は子の root に乗せる)。
     */
    private buildMechBoss(): BossBuild {
        const grp = new THREE.Group();
        const root = new THREE.Object3D(); // strafe(x)/hover(y)/tilt(z) を乗せる土台 (= parts.torso)
        grp.add(root);
        const steel = new THREE.MeshStandardMaterial({ color: 0x46566a, roughness: 0.4, metalness: 0.72, envMapIntensity: 1 });
        const steelLt = new THREE.MeshStandardMaterial({ color: 0x8d9cae, roughness: 0.34, metalness: 0.72, envMapIntensity: 1 });
        const accent = new THREE.MeshStandardMaterial({ color: 0xcf3a36, roughness: 0.5, metalness: 0.5, envMapIntensity: 0.8 });
        const eyeMat = new THREE.MeshBasicMaterial({ color: 0x6fe0ff, transparent: true, opacity: 0.95 }); // バイザー = tell
        const jetMat = new THREE.MeshBasicMaterial({ color: 0x49e0ff, transparent: true, opacity: 0.85 });
        // 骨盤
        const pelvis = new THREE.Mesh(new THREE.BoxGeometry(1.7, 0.9, 1.1), steel);
        pelvis.position.set(0, 2.5, 0); root.add(pelvis);
        // 胸 (body = 呼吸スケールの基準)
        const body = new THREE.Mesh(new THREE.BoxGeometry(2.8, 2.0, 1.6), steel);
        body.position.set(0, 3.9, 0); root.add(body);
        const chestV = new THREE.Mesh(new THREE.BoxGeometry(1.0, 0.8, 0.2), eyeMat); // 胸のコア発光
        chestV.position.set(0, 3.9, -0.82); root.add(chestV);
        for (const dx of [-1, 1]) { // 胸ダクト
            const d = new THREE.Mesh(new THREE.BoxGeometry(0.5, 1.1, 0.3), steelLt);
            d.position.set(dx * 0.9, 4.0, -0.7); root.add(d);
        }
        // 首＋頭＋バイザー (tell)＋V字アンテナ
        const head = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.8, 0.9), steelLt);
        head.position.set(0, 5.25, 0); root.add(head);
        const visor = new THREE.Mesh(new THREE.BoxGeometry(0.7, 0.22, 0.1), eyeMat);
        visor.position.set(0, 5.28, -0.46); root.add(visor);
        for (const s of [-1, 1]) {
            const fin = new THREE.Mesh(new THREE.ConeGeometry(0.08, 0.5, 4), accent);
            fin.position.set(s * 0.28, 5.8, 0); fin.rotation.z = -s * 0.5; root.add(fin);
        }
        // 肩アーマー＋ミサイルポッド (片側 6 連)
        for (const s of [-1, 1]) {
            const pauld = new THREE.Mesh(new THREE.BoxGeometry(1.0, 1.0, 1.3), steelLt);
            pauld.position.set(s * 1.85, 4.5, 0); root.add(pauld);
            const pod = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.7, 0.7), accent);
            pod.position.set(s * 1.95, 5.05, -0.2); root.add(pod);
            for (let i = 0; i < 6; i++) { // 発射口
                const tube = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 0.5, 6), steel);
                tube.rotation.x = Math.PI / 2;
                tube.position.set(s * 1.95 - 0.28 + (i % 3) * 0.28, 5.2 - Math.floor(i / 3) * 0.28, -0.5);
                root.add(tube);
            }
        }
        // 腕 (肩→肘→手): 右手は大きな拳 (叩きつけ)。各セグメントは親の先端からぶら下げる。
        const buildArm = (dx: number, big: boolean): { shoulder: THREE.Object3D; elbow: THREE.Object3D; hand: THREE.Object3D } => {
            const shoulder = new THREE.Object3D();
            shoulder.position.set(dx, 4.5, 0);
            const upper = new THREE.Mesh(new THREE.BoxGeometry(0.6, 1.5, 0.6), steel);
            upper.position.set(0, -0.75, 0); shoulder.add(upper);
            const elbow = new THREE.Object3D();
            elbow.position.set(0, -1.5, 0); shoulder.add(elbow);
            const fore = new THREE.Mesh(new THREE.BoxGeometry(0.55, 1.4, 0.55), steelLt);
            fore.position.set(0, -0.7, 0); elbow.add(fore);
            const hand = new THREE.Object3D();
            hand.position.set(0, -1.4, 0); elbow.add(hand);
            const fist = new THREE.Mesh(new THREE.BoxGeometry(big ? 1.0 : 0.6, big ? 1.0 : 0.6, big ? 1.0 : 0.6), big ? accent : steelLt);
            hand.add(fist);
            root.add(shoulder);
            return { shoulder, elbow, hand };
        };
        const armL = buildArm(-1.95, false);
        const armR = buildArm(1.95, true);
        // 脚 (股→膝→足): 右脚は蹴りに使う。
        const buildLeg = (dx: number): THREE.Object3D => {
            const hip = new THREE.Object3D();
            hip.position.set(dx, 2.3, 0);
            const thigh = new THREE.Mesh(new THREE.BoxGeometry(0.7, 1.3, 0.8), steel);
            thigh.position.set(0, -0.65, 0); hip.add(thigh);
            const shin = new THREE.Mesh(new THREE.BoxGeometry(0.65, 1.2, 0.7), steelLt);
            shin.position.set(0, -1.7, 0.05); hip.add(shin);
            const foot = new THREE.Mesh(new THREE.BoxGeometry(0.75, 0.4, 1.2), steel);
            foot.position.set(0, -2.4, 0.25); hip.add(foot);
            root.add(hip);
            return hip;
        };
        const legL = buildLeg(-0.7);
        const legR = buildLeg(0.7);
        // ジェットパック (jet): 背中のブースター＋下向きノズル。炎は updateBattle で粒子放出。
        const jet = new THREE.Object3D();
        jet.position.set(0, 3.9, 1.0); root.add(jet);
        const pack = new THREE.Mesh(new THREE.BoxGeometry(1.8, 1.8, 0.7), steel);
        jet.add(pack);
        for (const s of [-1, 1]) {
            const noz = new THREE.Mesh(new THREE.CylinderGeometry(0.28, 0.42, 0.8, 10), steelLt);
            noz.position.set(s * 0.6, -1.0, 0.2); noz.rotation.x = 0.4; jet.add(noz);
            const flame = new THREE.Mesh(new THREE.ConeGeometry(0.32, 1.1, 8), jetMat);
            flame.position.set(s * 0.6, -1.7, 0.35); flame.rotation.x = Math.PI; jet.add(flame);
        }
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 7.0, 0);
        grp.add(bar);
        return {
            obj: grp, bar, spin: [], tell: visor, jaw: null,
            parts: {
                torso: root, body, head, jet, legL, legR,
                armL: armL.shoulder, armR: armR.shoulder, elbowL: armL.elbow, elbowR: armR.elbow, handL: armL.hand, handR: armR.hand,
            },
        };
    }

    /**
     * Backrooms ボス「THE SMILER」: 暗闇に浮かぶ巨大な笑顔。点灯時は脆く攻撃でき、消灯(必殺)時は
     * 画面が真っ暗になり光る笑顔だけが見える→その x へ突進(横に避ける)。眼と歯は常時発光。
     */
    private buildSmilerBoss(): BossBuild {
        const grp = new THREE.Group();
        const root = new THREE.Object3D(); // 浮遊/突進の視覚オフセット土台 (= parts.torso)
        grp.add(root);
        const CY = 3.6;
        // 本体: ほぼ見えない暗い塊 (闇の中の質量感)。点灯時にうっすら浮かぶ。
        const bodyMat = new THREE.MeshBasicMaterial({ color: 0x0a0d0a, transparent: true, opacity: 0.92 });
        const body = new THREE.Mesh(new THREE.SphereGeometry(2.6, 20, 16), bodyMat);
        body.scale.set(1.15, 1.0, 0.8);
        body.position.set(0, CY, 0); root.add(body);
        // 発光する眼 2 つ (tell = 全体の発光を司る親)
        const face = new THREE.Object3D();
        face.position.set(0, CY, -1.9); root.add(face);
        const eyeMat = new THREE.MeshBasicMaterial({ color: 0xd6ffb0, fog: false }); // 消灯中も暗い fog で沈まないよう fog 無効
        for (const dx of [-0.85, 0.85]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.28, 12, 10), eyeMat);
            eye.position.set(dx, 0.7, 0); face.add(eye);
            const glow = this.projGlow(eye, 0x9bff6a, 1.6);
            glow.userData.smileGlow = true;
        }
        // にやりと並んだ歯 (弧状)。これが「笑顔」。
        const toothMat = new THREE.MeshBasicMaterial({ color: 0xeaffd0, fog: false });
        for (let i = 0; i < 11; i++) {
            const t = (i / 10 - 0.5) * 2; // -1..1
            const tooth = new THREE.Mesh(new THREE.BoxGeometry(0.26, 0.42 - Math.abs(t) * 0.14, 0.18), toothMat);
            tooth.position.set(t * 2.0, -0.5 - (1 - Math.abs(t)) * 0.5, 0); // 下に凸の弧 (にやり)
            tooth.rotation.z = -t * 0.5;
            face.add(tooth);
            this.projGlow(tooth, 0x9bff6a, 0.7).userData.smileGlow = true;
        }
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 6.8, 0);
        grp.add(bar);
        // tell = face (眼+歯の親)。消灯時に膨らみ/明滅させる。
        return { obj: grp, bar, spin: [], tell: face, jaw: null, parts: { torso: root, body, head: face } };
    }

    /** ドラゴンボス: 胴＋頭＋角＋光る目＋羽ばたく翼。炎ブレスを吐く。 */
    private buildDragonBoss(): BossBuild {
        const grp = new THREE.Group();
        const scale = new THREE.Group();
        scale.scale.setScalar(1.5);
        grp.add(scale);
        const scaleMat = new THREE.MeshLambertMaterial({ color: 0x7d2230 });
        const bellyMat = new THREE.MeshLambertMaterial({ color: 0xc77a2e });
        // 胴 (前傾した楕円)
        const body = new THREE.Mesh(new THREE.SphereGeometry(1.3, 14, 12), scaleMat);
        body.scale.set(1, 1.15, 1.5);
        body.position.set(0, 2.0, 0);
        scale.add(body);
        const belly = new THREE.Mesh(new THREE.SphereGeometry(0.8, 12, 10), bellyMat);
        belly.scale.set(1, 1.0, 1.4);
        belly.position.set(0, 1.5, -0.5);
        scale.add(belly);
        // 首から先は headGrp にまとめ、首の付け根 (0,3.0,-1.1) を pivot に回せるようにする。
        // 各子の local 位置から pivot を引いて、見た目が動かないようにする。
        const headGrp = new THREE.Group();
        headGrp.position.set(0, 3.0, -1.1);
        scale.add(headGrp);
        // 首＋頭 (クルー側 = -z を向く)
        const neck = new THREE.Mesh(new THREE.CylinderGeometry(0.45, 0.7, 1.6, 10), scaleMat);
        neck.rotation.x = -0.5;
        neck.position.set(0, 0, 0); // (0,3.0,-1.1) - (0,3.0,-1.1)
        headGrp.add(neck);
        const head = new THREE.Mesh(new THREE.BoxGeometry(0.9, 0.8, 1.3), scaleMat);
        head.position.set(0, 0.5, -0.9); // (0,3.5,-2.0) - pivot
        headGrp.add(head);
        const snout = new THREE.Mesh(new THREE.ConeGeometry(0.42, 1.0, 8), scaleMat);
        snout.rotation.x = -Math.PI / 2;
        snout.position.set(0, 0.4, -1.8); // (0,3.4,-2.9) - pivot
        headGrp.add(snout);
        for (const dx of [-0.34, 0.34]) {
            const horn = new THREE.Mesh(new THREE.ConeGeometry(0.16, 0.9, 6), new THREE.MeshLambertMaterial({ color: 0xf0e2c0 }));
            horn.rotation.x = 0.7;
            horn.position.set(dx, 1.1, -0.6); // (dx,4.1,-1.7) - pivot
            headGrp.add(horn);
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.14, 8, 8), new THREE.MeshBasicMaterial({ color: 0xffe34d }));
            eye.position.set(dx * 0.7, 0.6, -1.4); // (dx*0.7,3.6,-2.5) - pivot
            headGrp.add(eye);
        }
        // 翼 (羽ばたく): 薄い扇形を左右に
        const spin: THREE.Object3D[] = [];
        for (const side of [-1, 1]) {
            const wing = new THREE.Group();
            const memb = new THREE.Mesh(
                new THREE.ConeGeometry(1.7, 2.8, 3),
                new THREE.MeshLambertMaterial({ color: 0x4a1620, side: THREE.DoubleSide }),
            );
            memb.scale.set(1, 0.12, 1);
            memb.rotation.z = Math.PI / 2;
            memb.position.set(side * 1.8, 0, 0);
            wing.add(memb);
            wing.position.set(side * 0.9, 2.6, 0.2);
            wing.rotation.z = side * 0.3;
            scale.add(wing);
            spin.push(wing);
        }
        // 前爪 (肩 pivot から下げ・連続 sine で交互に揺れる)。scale の子なので竜の比率を保つ。
        const claws: THREE.Object3D[] = [];
        for (const dx of [-0.7, 0.7]) {
            const pivot = new THREE.Object3D();
            pivot.position.set(dx, 1.3, -0.7); // 肩 (胴の前下)
            const fore = new THREE.Mesh(new THREE.CylinderGeometry(0.16, 0.1, 0.9, 6), scaleMat);
            fore.position.set(0, -0.45, 0); // 肩 pivot から下げ
            pivot.add(fore);
            const tip = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.3, 4), new THREE.MeshLambertMaterial({ color: 0xf0e2c0 }));
            tip.position.set(0, -0.95, 0);
            pivot.add(tip);
            scale.add(pivot);
            claws.push(pivot);
        }
        // 尾
        const tail = new THREE.Mesh(new THREE.ConeGeometry(0.5, 2.6, 8), scaleMat);
        tail.rotation.x = Math.PI / 2.2;
        tail.position.set(0, 1.6, 1.8);
        scale.add(tail);
        // 背びれ
        for (let i = 0; i < 6; i++) {
            const plate = new THREE.Mesh(new THREE.ConeGeometry(0.18, 0.55, 4), new THREE.MeshLambertMaterial({ color: 0x5a1822 }));
            plate.position.set(0, 2.7 - i * 0.06, 0.2 + i * 0.35);
            scale.add(plate);
        }
        // 下顎 (jaw・吐く前に開く) — headGrp の子だが jaw 参照は同じオブジェクトのまま (rotation.x はローカルで効く)
        const jaw = new THREE.Group();
        const jawMesh = new THREE.Mesh(new THREE.BoxGeometry(0.8, 0.28, 1.0), scaleMat);
        jawMesh.position.set(0, -0.12, -0.5);
        jaw.add(jawMesh);
        jaw.position.set(0, 0.3, -0.9); // (0,3.3,-2.0) - pivot
        headGrp.add(jaw);
        // 喉袋 (tell・吐く前に膨らんで光る)
        const sac = new THREE.Mesh(new THREE.SphereGeometry(0.45, 10, 8), new THREE.MeshBasicMaterial({ color: 0xff7a1e, transparent: true, opacity: 0.4 }));
        sac.position.set(0, 0.5, -0.3); // (0,3.5,-1.4) - pivot
        headGrp.add(sac);
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 7.2, 0);
        grp.add(bar);
        return { obj: grp, bar, spin, tell: sac, jaw, parts: { body: scale, head: headGrp, tail, claws } };
    }

    /** 天使ボス: 光る人型＋頭上の光輪＋複数の翼＋回転する炎の輪 (オファニム)。 */
    private buildAngelBoss(): BossBuild {
        const grp = new THREE.Group();
        const glow = new THREE.MeshBasicMaterial({ color: 0xfff4cf });
        const goldGlow = new THREE.MeshBasicMaterial({ color: 0xffd86b });
        // 胴 (淡く光る human 形)
        const torso = new THREE.Mesh(new THREE.CapsuleGeometry(0.7, 1.6, 6, 12), glow);
        torso.position.set(0, 3.0, 0);
        grp.add(torso);
        const head = new THREE.Mesh(new THREE.SphereGeometry(0.55, 14, 12), glow);
        head.position.set(0, 4.5, 0);
        grp.add(head);
        // 頭上の光輪 (ヘイロー)
        const haloGeo = new THREE.TorusGeometry(0.6, 0.07, 10, 28);
        const halo = new THREE.Mesh(haloGeo, goldGlow);
        halo.rotation.x = Math.PI / 2;
        halo.position.set(0, 5.3, 0);
        grp.add(halo);
        // 複数の翼 (左右 3 対の薄い羽根)
        for (const side of [-1, 1]) {
            for (let i = 0; i < 3; i++) {
                const feather = new THREE.Mesh(
                    new THREE.ConeGeometry(0.5, 2.2 - i * 0.3, 5),
                    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.85, side: THREE.DoubleSide }),
                );
                feather.scale.set(1, 0.1, 1);
                feather.rotation.z = Math.PI / 2;
                feather.rotation.y = side * (0.5 + i * 0.28);
                feather.position.set(side * 1.0, 3.4 + i * 0.45, 0.1);
                grp.add(feather);
            }
        }
        // 祝福の腕 (肩 pivot からぶら下げ・連続 sine でゆっくり上下)。pivot で肩を中心に回す。
        const arms: THREE.Object3D[] = [];
        for (const dx of [-0.7, 0.7]) {
            const pivot = new THREE.Object3D();
            pivot.position.set(dx, 3.6, 0); // 肩
            const arm = new THREE.Mesh(new THREE.CapsuleGeometry(0.18, 0.9, 4, 8), glow);
            arm.position.set(0, -0.6, 0); // 肩 pivot からぶら下げ
            pivot.add(arm);
            grp.add(pivot);
            arms.push(pivot);
        }
        // 回転する炎の輪 (オファニム): 胴を囲む 2 つの大きな光輪
        const spin: THREE.Object3D[] = [];
        for (let i = 0; i < 2; i++) {
            const ring = new THREE.Mesh(
                new THREE.TorusGeometry(1.5 - i * 0.25, 0.06, 8, 32),
                new THREE.MeshBasicMaterial({ color: i ? 0xff9f43 : 0xffe9a8, transparent: true, opacity: 0.8 }),
            );
            ring.position.set(0, 3.0, 0);
            ring.rotation.set(i ? 1.0 : 0.4, i ? 0.6 : -0.4, 0);
            grp.add(ring);
            spin.push(ring);
        }
        // 回転する刃の冠 (弾幕の出どころ・縦リング・spin)
        const blades = new THREE.Group();
        blades.position.set(0, 3.0, 0);
        for (let i = 0; i < 8; i++) {
            const a = (i / 8) * Math.PI * 2;
            const blade = new THREE.Mesh(new THREE.BoxGeometry(0.06, 1.0, 0.08), new THREE.MeshBasicMaterial({ color: 0x9fe0ff }));
            blade.position.set(Math.cos(a) * 2.0, Math.sin(a) * 2.0, 0);
            blade.rotation.z = a + Math.PI / 2; // 放射状に向ける
            blades.add(blade);
        }
        grp.add(blades);
        spin.push(blades);
        // 胸の眼 (tell・狙撃の前に開いて赤く光る)
        const eye = new THREE.Mesh(new THREE.SphereGeometry(0.4, 12, 10), new THREE.MeshBasicMaterial({ color: 0xfff4cf, transparent: true, opacity: 0.5 }));
        eye.scale.set(1, 0.5, 1);
        eye.position.set(0, 3.4, -0.7);
        grp.add(eye);
        const iris = new THREE.Mesh(new THREE.CircleGeometry(0.16, 12), new THREE.MeshBasicMaterial({ color: 0x1a3a55 }));
        iris.position.set(0, 3.4, -0.86);
        grp.add(iris);
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 6.9, 0);
        grp.add(bar);
        return { obj: grp, bar, spin, tell: eye, jaw: null, parts: { body: torso, halo, armL: arms[0], armR: arms[1] } };
    }

    /** 海獣ボス (新): 深海のタイダル・カイジュウ。冷たい青緑＋発光する誘灯と口内。 */
    private buildLeviathanBoss(): BossBuild {
        const grp = new THREE.Group();
        const hull = new THREE.MeshLambertMaterial({ color: 0x123c4a });
        const glowMat = new THREE.MeshBasicMaterial({ color: 0x7affe0 });
        const teal = new THREE.MeshBasicMaterial({ color: 0x6affd0 });
        // 盛り上がる背
        const hump = new THREE.Mesh(new THREE.SphereGeometry(1.6, 16, 12), hull);
        hump.scale.set(1.6, 1.0, 1.8);
        hump.position.set(0, 2.4, 0.8);
        grp.add(hump);
        const shell = new THREE.Mesh(new THREE.IcosahedronGeometry(1.75, 0), new THREE.MeshLambertMaterial({ color: 0x0c2630, transparent: true, opacity: 0.7 }));
        shell.scale.set(1.6, 1.0, 1.8);
        shell.position.set(0, 2.4, 0.8);
        grp.add(shell);
        // 上顎 (クルー側 -z)
        const upper = new THREE.Mesh(new THREE.BoxGeometry(2.6, 0.7, 1.8), hull);
        upper.position.set(0, 2.7, -1.8);
        grp.add(upper);
        // 下顎 (jaw・開閉)
        const jaw = new THREE.Group();
        const lower = new THREE.Mesh(new THREE.BoxGeometry(2.4, 0.6, 1.7), hull);
        lower.position.set(0, -0.25, -0.85);
        jaw.add(lower);
        for (let i = 0; i < 6; i++) {
            const tooth = new THREE.Mesh(new THREE.ConeGeometry(0.12, 0.5, 4), new THREE.MeshLambertMaterial({ color: 0xbfe6ff }));
            tooth.position.set(-1.0 + i * 0.4, 0.1, -1.5);
            jaw.add(tooth);
        }
        jaw.position.set(0, 2.3, -1.0);
        grp.add(jaw);
        // 口内グロー (tell)
        const maw = new THREE.Mesh(new THREE.SphereGeometry(0.8, 12, 10), new THREE.MeshBasicMaterial({ color: 0x7affe0, transparent: true, opacity: 0.6 }));
        maw.position.set(0, 2.4, -1.6);
        grp.add(maw);
        for (const dx of [-0.7, 0.7]) {
            const eye = new THREE.Mesh(new THREE.SphereGeometry(0.18, 10, 8), glowMat);
            eye.position.set(dx, 3.3, -1.5);
            grp.add(eye);
        }
        // 誘灯 (アンコウの提灯・spin[0])
        const lure = new THREE.Group();
        const stalk = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 2.6, 6), hull);
        stalk.position.y = 1.3;
        lure.add(stalk);
        const orb = new THREE.Mesh(new THREE.SphereGeometry(0.4, 12, 10), glowMat);
        orb.position.y = 2.6;
        lure.add(orb);
        lure.position.set(0, 3.2, -3.0);
        lure.rotation.x = -0.4;
        grp.add(lure);
        // 背の発光輪
        for (let i = 0; i < 5; i++) {
            const vent = new THREE.Mesh(new THREE.TorusGeometry(0.22, 0.07, 8, 12), teal);
            vent.rotation.x = Math.PI / 2;
            vent.position.set(0, 3.6 - i * 0.12, 0.3 + i * 0.5);
            grp.add(vent);
        }
        // ひれ (両脇)
        const fins: THREE.Object3D[] = [];
        for (const dx of [-2.6, 2.6]) {
            const fin = new THREE.Mesh(new THREE.ConeGeometry(1.2, 2.2, 3), hull);
            fin.scale.set(1, 0.2, 1);
            fin.rotation.z = Math.PI / 2;
            fin.position.set(dx, 2.0, 0.5);
            grp.add(fin);
            fins.push(fin);
        }
        // 爪 (顎下の両脇・肩 pivot から下げ・連続 sine で揺れる)。pivot で根元を中心に回す。
        const claws: THREE.Object3D[] = [];
        for (const dx of [-1.4, 1.4]) {
            const pivot = new THREE.Object3D();
            pivot.position.set(dx, 1.6, -1.4); // 顎下の付け根
            const arm = new THREE.Mesh(new THREE.CylinderGeometry(0.2, 0.12, 1.1, 6), hull);
            arm.position.set(0, -0.55, 0); // pivot から下げ
            pivot.add(arm);
            const tip = new THREE.Mesh(new THREE.ConeGeometry(0.14, 0.4, 4), new THREE.MeshLambertMaterial({ color: 0xbfe6ff }));
            tip.position.set(0, -1.15, 0);
            pivot.add(tip);
            grp.add(pivot);
            claws.push(pivot);
        }
        const bar = this.makeBarSprite(true);
        bar.position.set(0, 7.4, 0);
        grp.add(bar);
        return { obj: grp, bar, spin: [lure], tell: maw, jaw, parts: { body: hump, fins, claws } };
    }

    /**
     * 被ダメ倍率。海獣が「回避 (evade)」中は滑って受け流すので 0.25 倍 (即死防止の本丸)。
     * 突進 (charge) 中・その他のボスは等倍 → 突進が確定ダメージ窓になる。
     */
    private bossDmgMul(boss: NonNullable<CrewRun3D["boss"]> | null): number {
        if (boss == null) return 1;
        if (boss.kind === "smiler") return boss.smilerLight ? 1 : 0; // 消灯(必殺)中は無敵 — 点灯した脆い窓でだけ削れる
        if (boss.kind !== "leviathan") return 1;
        // 回避中は滑って被ダメ減 (行動が見える長さに延ばす)、突進中は隙=被ダメ増で帳尻を合わせる。
        // 海獣はこのぶん長引くので、プレイヤーへの削り (chip/heavy) も別途軽くして勝てる長さに収める。
        return boss.levMode === "evade" ? 0.5 : 1.5;
    }

    /**
     * 海獣の回避↔突進タイムライン。updateBattle から leviathan のときだけ毎フレーム呼ぶ。
     * EVADE: 左右にウィービングして弾幕を避ける (被ダメ 0.25 倍・滑る)。
     * CHARGE: 予告 → 群衆へダイブして幅広弾 (被ダメ等倍・確定ダメージ窓)。
     * フェーズが上がるほど回避が短く突進が頻繁になる (2/3 でも決着する)。
     */
    private updateLeviathan(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        if (boss.levMode === undefined) { // 初回: 回避から始める
            boss.levMode = "evade"; boss.levT = 0; boss.levPhaseT = 0;
            boss.levStrafe = 0; boss.levChargeZ = 0; boss.levTilt = 0;
        }
        const t = this.clock.elapsedTime;
        boss.levT = (boss.levT ?? 0) + dt;
        // フェーズスケール: phase 0→1→2 で回避が短く突進ダイブが深く/速くなる。
        const evadeDur = [1.7, 1.3, 1.0][boss.phase];
        const chargeDur = [1.4, 1.3, 1.2][boss.phase];
        const tellDur = [0.55, 0.45, 0.38][boss.phase];

        if (boss.levMode === "evade") {
            // プレイヤーの照準 (centroidX) から離れる向きへウィーブ = 「狙いを外す」よう読ませる。
            const away = -Math.sign(this.centroidX || 1);
            const weave = Math.sin(boss.levT * 3.0) * 2.6 + away * 0.6;
            boss.levStrafe = THREE.MathUtils.lerp(boss.levStrafe ?? 0, weave, Math.min(1, dt * 6));
            boss.levTilt = Math.sin(boss.levT * 3.0) * 0.18; // バンク (横傾き)
            boss.levChargeZ = THREE.MathUtils.lerp(boss.levChargeZ ?? 0, 0, Math.min(1, dt * 4));
            if (boss.levT >= evadeDur) { // → 突進へ
                boss.levMode = "charge"; boss.levT = 0; boss.levPhaseT = 0;
            }
        } else {
            // CHARGE: 前半 tellDur は予告 (滞空して睨む)、その後ダイブ。
            const inTell = boss.levT < tellDur;
            if (boss.levPhaseT === 0 && inTell) {
                // 予告は 1 回だけ: 群衆へ向けた床のレーン + バナー + 滞空。
                boss.levPhaseT = 1;
                const tx = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1.2), LANE - 1.2);
                this.floorTell(tx, 0x6affd0, 2.0);
                this.showBanner("🌊 突進！");
                this.impactBurst(boss.obj.position.clone().setY(3), "leviathan", 1.2);
            }
            // ストレイフは予告中に 0 へ寄せ、狙った位置へ構える。
            boss.levTilt = THREE.MathUtils.lerp(boss.levTilt ?? 0, -0.25, Math.min(1, dt * 5));
            boss.levStrafe = THREE.MathUtils.lerp(boss.levStrafe ?? 0, 0, Math.min(1, dt * 4));
            // ダイブの深さ: 予告後に群衆の眼前まで深く突っ込み、終盤で戻る (0→max→0 のイージング)。
            // ボスは z=18 に居るので、群衆 (z=0) に届かせるには大きく前進させる必要がある (旧 6-8 では手前で止まっていた)。
            const k = boss.levT < tellDur ? 0 : (boss.levT - tellDur) / Math.max(0.01, chargeDur - tellDur);
            const dive = Math.sin(Math.min(1, k) * Math.PI); // 0→1→0
            boss.levChargeZ = THREE.MathUtils.lerp(boss.levChargeZ ?? 0, dive * [13, 15, 17][boss.phase], Math.min(1, dt * 10));
            if (boss.levPhaseT === 1 && k > 0) {
                // ダイブ着弾の瞬間に幅広の本命弾 (プレイヤー狙い)。1 回だけ。
                boss.levPhaseT = 2;
                const tx = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1.2), LANE - 1.2);
                this.spawnHeavyArrow(boss, tx, 2.0, 0x6affd0);
                this.shake = Math.max(this.shake, 0.45);
            }
            if (boss.levT >= chargeDur) { // → 回避へ戻る
                boss.levMode = "evade"; boss.levT = 0; boss.levPhaseT = 0;
            }
        }
        // 微小な揺らぎを加えて生っぽく (アニメは animateBoss が別途)。
        void t;
    }

    /**
     * mech (OVERSEER) の毎フレーム処理: 左右ダッシュ(strafe)でプレイヤーのレーンに被せ続け、
     * 近接(拳叩き/蹴り)中はその着弾点へ寄って攻撃を解決する。strafe は root(parts.torso)に乗せる。
     */
    private updateMech(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        // ダッシュ目標の更新 (近接中は固定)。60% でプレイヤーへ被せ、40% で反対端へ振って読ませない。
        if (boss.mechMelee === "none" || boss.mechMelee === undefined) {
            boss.mechDashT = (boss.mechDashT ?? 0) - dt;
            if ((boss.mechDashT ?? 0) <= 0) {
                boss.mechDashT = 0.7 + Math.random() * 0.7;
                boss.mechTargetX = Math.random() < 0.6
                    ? THREE.MathUtils.clamp(this.centroidX, -(LANE - 1), LANE - 1)
                    : (Math.random() < 0.5 ? -1 : 1) * (LANE - 1);
            }
        }
        const tgt = (boss.mechMelee && boss.mechMelee !== "none") ? (boss.mechMeleeX ?? 0) : (boss.mechTargetX ?? 0);
        boss.mechX = THREE.MathUtils.lerp(boss.mechX ?? 0, tgt, Math.min(1, dt * 6)); // 速いダッシュ
        // 近接タイムライン: しきい値を跨いだ瞬間に 1 回だけ着弾。
        if (boss.mechMelee && boss.mechMelee !== "none") {
            const prev = boss.mechMeleeT ?? 0;
            boss.mechMeleeT = prev - dt;
            const now = boss.mechMeleeT;
            const x = boss.mechMeleeX ?? 0;
            const impactAt = boss.mechMelee === "fist" ? 0.30 : 0.42;
            if (prev > impactAt && now <= impactAt) {
                this.impactBurst(new THREE.Vector3(x, 0.9, CROWD_Z), "mech", 1.3);
                if (boss.mechMelee === "fist") this.collideCrowd(x, 1.9, false, true); // 頭上からの拳=前面シールド貫通
                else this.collideCrowd(x, 2.4, false, false); // 横薙ぎの蹴り=前面シールド/砲台で防御可
                this.shockRing(new THREE.Vector3(x, 0.6, CROWD_Z), 0x6fe0ff, 5, 0.4);
                this.shake = Math.max(this.shake, 0.55);
            }
            if (now <= 0) { boss.mechMelee = "none"; boss.mechMeleeT = 0; }
        }
    }

    /** mech のヘビー: フェーズで近接を解禁しつつ、ミサイル斉射(上/前/斜め)を主体に回す。 */
    private mechPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        const canFist = boss.phase >= 1, canKick = boss.phase >= 2;
        const roll = boss.patternIdx % (canKick ? 4 : canFist ? 3 : 2);
        if (canKick && roll === 3) { this.mechStartMelee(boss, "kick"); return; }
        if (canFist && roll === 2) { this.mechStartMelee(boss, "fist"); return; }
        this.mechMissileBarrage(boss, boss.patternIdx % 3); // 0=上 / 1=前 / 2=斜め
    }

    private mechStartMelee(boss: NonNullable<CrewRun3D["boss"]>, kind: "fist" | "kick"): void {
        boss.mechMelee = kind;
        boss.mechMeleeT = MECH_MELEE_DUR;
        boss.mechMeleeX = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1.4), LANE - 1.4);
        this.floorTell(boss.mechMeleeX, kind === "fist" ? 0xff4d4d : 0xffa83b, kind === "fist" ? 2.0 : 2.6);
        boss.strikeT = 1; // animateBoss の打撃と同期
        this.showBanner(kind === "fist" ? "🤖 叩きつけ！" : "🤖 蹴り！");
    }

    /** ミサイル斉射: mode 0=頭上 / 1=正面 / 2=斜め。プレイヤー周辺＋ランダムレーンへ予告つきで撃つ。 */
    private mechMissileBarrage(boss: NonNullable<CrewRun3D["boss"]>, mode: number): void {
        const n = 3 + boss.phase; // フェーズで本数増
        const mx = boss.mechX ?? 0, mz = boss.z;
        for (let i = 0; i < n; i++) {
            // 半数はプレイヤー周辺、半数はランダムレーン → 横へ weave させる
            const targetX = i % 2 === 0
                ? this.centroidX + (Math.random() - 0.5) * 3.0
                : (Math.random() * 2 - 1) * (LANE - 0.6);
            let from: THREE.Vector3, overhead: boolean;
            if (mode === 0) { from = new THREE.Vector3(targetX + (Math.random() - 0.5) * 1.5, 11, CROWD_Z + 2); overhead = true; } // 頭上から降る
            else if (mode === 1) { from = new THREE.Vector3(mx + (Math.random() - 0.5) * 1.5, 4.2, mz); overhead = false; } // 正面から
            else { from = new THREE.Vector3(mx + (i % 2 ? 5 : -5), 8, mz * 0.6); overhead = false; } // 斜め上から
            this.spawnEnemyMissile(from, targetX, { R: 1.3, overhead, col: 0xff7a3b, t: 0.9 + i * 0.12 });
        }
        this.shake = Math.max(this.shake, 0.25);
    }

    /**
     * smiler (THE SMILER) の毎フレーム処理: 点灯↔消灯のサイクル。
     * 点灯=脆く攻撃でき、ゆっくり迫る「畏怖」弾を撃つ。消灯=画面が暗くなり笑顔だけが見え、
     * 今のプレイヤー位置へ突進(横に避ける)。着弾でフラッシュして点灯に戻る。
     */
    private updateSmiler(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        if (boss.smilerLight) {
            boss.smilerLightT = (boss.smilerLightT ?? 0) - dt;
            if ((boss.smilerLightT ?? 0) <= 0) {
                // → 消灯(必殺): 暗転して突進をロック。
                boss.smilerLight = false;
                boss.smilerLungeT = SMILER_LUNGE_DUR;
                boss.smilerFromX = boss.smilerLungeX ?? 0;
                boss.smilerLungeX = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1), LANE - 1);
                this.smilerEnterDark();
                this.shake = Math.max(this.shake, 0.3);
                this.showBanner("⚫ 暗闇…", "笑顔から逃げろ");
            }
        } else {
            const prev = boss.smilerLungeT ?? 0;
            boss.smilerLungeT = prev - dt;
            const now = boss.smilerLungeT ?? 0;
            const u = clamp01(1 - now / SMILER_LUNGE_DUR);
            const pu = clamp01(1 - prev / SMILER_LUNGE_DUR);
            const impactAt = 0.62;
            if (pu < impactAt && u >= impactAt) {
                this.impactBurst(new THREE.Vector3(boss.smilerLungeX ?? 0, 1.0, CROWD_Z), "smiler", 1.4);
                this.collideCrowd(boss.smilerLungeX ?? 0, 2.0, false, false); // 笑顔の突進 (横に避ける)
                this.shake = Math.max(this.shake, 0.6);
                this.flashScreen("#1a3a14", 220); // 着弾の閃光 (緑)
            }
            if (now <= 0) {
                // → 点灯へ戻る (脆い窓)。フェーズが上がると点灯が短く=消灯が頻繁。
                boss.smilerLight = true;
                boss.smilerLightT = [3.0, 2.3, 1.7][boss.phase];
                this.smilerExitDark();
            }
        }
    }

    /** smiler のヘビー (点灯中のみ): ゆっくり迫る畏怖弾 + フェーズで横薙ぎ。消灯中は突進が主役。 */
    private smilerPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        if (!boss.smilerLight) return; // 消灯中は突進に任せる
        const tx = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1.2), LANE - 1.2);
        if (boss.phase >= 1 && boss.patternIdx % 2 === 0) {
            this.spawnNotchWalls(boss, tx, 0x9bff6a); // 安全レーンを残す壁 (横へ逃げる)
        } else {
            this.spawnHeavyArrow(boss, tx, 1.6, 0x9bff6a); // プレイヤー狙いの単発 (避けられる)
        }
    }

    private smilerEnterDark(): void {
        if (!this.smilerDarkSaved) {
            this.smilerDarkSaved = { hemi: this.hemi.intensity, dir: this.dir.intensity, fogN: this.fog.near, fogF: this.fog.far, fogC: this.fog.color.getHex() };
        }
        this.hemi.intensity = 0.12; this.dir.intensity = 0.08;
        this.fog.color.setHex(0x05070a); this.fog.near = 10; this.fog.far = 44;
    }
    private smilerExitDark(): void {
        const s = this.smilerDarkSaved;
        if (!s) return;
        this.hemi.intensity = s.hemi; this.dir.intensity = s.dir;
        this.fog.near = s.fogN; this.fog.far = s.fogF; this.fog.color.setHex(s.fogC);
        this.smilerDarkSaved = null;
    }

    private updateBattle(dt: number): void {
        this.stageTime += dt;
        const boss = this.boss;
        if (!boss) return;
        const stopZ = 18;
        if (boss.z > stopZ) {
            boss.z -= this.speed * 1.4 * dt; // 素早く接近 (待ち時間を作らない)
            this.animateBoss(boss, dt);
            boss.obj.position.set(0, 0, boss.z);
            this.updateBossBar();
            return;
        }
        this.battleTime += dt;
        if (boss.kind === "chronos") this.updateTimeStop(boss, dt); // 時間停止シーケンスの進行
        if (boss.kind === "leviathan") this.updateLeviathan(boss, dt); // 回避↔突進ステートマシン
        if (boss.kind === "mech") this.updateMech(boss, dt); // 左右ダッシュ + 近接(拳/蹴り)の進行
        if (boss.kind === "smiler") this.updateSmiler(boss, dt); // 点灯↔消灯 + 突進

        // --- 1. プレイヤー→ボス DPS: ソフトキャップで一撃溶解を不可能にする ---
        // ロケキャノン選択中は sustained 0。代わりに rocketCdT 周期でボスへ大ダメージ (激遅)。
        if (this.weaponTier === ROCKET_WEAPON) {
            if (this.rocketCdT > 0) this.rocketCdT = Math.max(0, this.rocketCdT - dt);
            if (this.rocketCdT <= 0 && boss.windup <= 0) {
                boss.hp -= boss.max * 0.22 * this.bossDmgMul(boss); // 1 発が重い (連発できない)・海獣回避中は減衰
                const at = boss.obj.position.clone().setY(1.6);
                this.boom(at);
                this.impactBurst(at, "dragon", 1.5);
                this.popWorld("BOOM!", boss.obj.position.x, "#ff9a4a");
                this.shake = Math.max(this.shake, 0.6);
                this.rocketCdT = ROCKET_COOLDOWN;
            }
        }
        // 追尾ミサイルランチャー選択中は sustained 0。周期でボスへホーミング弾を斉射 (ダメージは explodeMissile が処理・mech 特効)。
        if (this.weaponTier === HOMING_WEAPON) {
            if (this.homingCdT > 0) this.homingCdT = Math.max(0, this.homingCdT - dt);
            if (this.homingCdT <= 0 && boss.windup <= 0) { this.fireHomingVolley(); this.homingCdT = HOMING_COOLDOWN; }
        }
        // ブーメラン選択中は sustained 0。飛んでいなければボスへ向けて投げ直す (ダメージは updateBoomerang が処理)。
        const boomActive = this.weaponTier === BOOMERANG_WEAPON;
        if (boomActive && !this.boomerang && boss.windup <= 0) this.throwBoomerang(null);
        const rawDps = this.firepower * FIRE_FACTOR;
        const cap = Math.max(6, boss.max / MIN_FIGHT); // 最低 MIN_FIGHT 秒は戦う構造的な底
        const capped = rawDps <= cap ? rawDps : cap * (1 + DPS_SOFT_K * Math.log2(1 + (rawDps - cap) / cap));
        const gentleRamp = 1 + this.battleTime * 0.12; // 低火力でも必ず決着 (anti-stall)
        if (boss.windup <= 0 && !boomActive) boss.hp -= capped * gentleRamp * dt * this.bossDmgMul(boss); // 遷移シールド中/ブーメラン中は sustained 無効 (海獣回避中は 0.25 倍)

        // --- 2. ボス→プレイヤー: 確定チップ(生存可能) + ヘビー(回避可能) に分割 ---
        // 海獣は回避で戦闘が長引くぶん、プレイヤーへの削りを軽くして「長いが勝てる」帯に収める。
        const drainMul = boss.kind === "leviathan" ? 0.55 : 1;
        const chipDps = Math.min((this.crew * 0.015 + CHIP_BASE) * this.stageMul, this.crew * CHIP_FRAC_PER_S) * drainMul;
        this.drainCarry += chipDps * dt;
        boss.heavyCarry += (this.crew * HEAVY_BASE + HEAVY_FLAT) * this.stageMul * dt * drainMul;

        // --- 3. フェーズ遷移 (100→66→33%) : 読みやすい一拍を強制 ---
        const frac = boss.hp / boss.max;
        if (boss.phase === 0 && frac <= 0.66) this.enterPhase(boss, 1);
        else if (boss.phase === 1 && frac <= 0.33) this.enterPhase(boss, 2);

        // --- 4. 遷移シールドの消化 / 平常時の攻撃 ---
        if (boss.windup > 0) {
            boss.windup -= dt / 0.6; // 0.6s で開ける
            if (boss.windup < 0) boss.windup = 0;
        } else {
            this.bossAttack(boss, dt); // 確定チップの小弾
            this.bossHeavy(boss, dt); // 回避可能なヘビー
            this.spawnBullets(dt, boss.obj.position, 2);
        }

        this.animateBoss(boss, dt);
        boss.obj.position.set(0, 0, boss.z);
        // 海獣の回避ストレイフ/突進ダイブは set(0,0,z) の後に視覚オフセットとして上書きする (当たり判定は論理座標のまま)。
        if (boss.kind === "leviathan") {
            boss.obj.position.x += boss.levStrafe ?? 0;
            boss.obj.position.z -= boss.levChargeZ ?? 0; // 群衆へダイブ (z を下げる)
            boss.obj.rotation.z += boss.levTilt ?? 0; // バンク (横傾き)
        }
        this.updateBossBar();
        if (boss.hp <= 0) this.stageClear();
        else if (this.crew <= 0) this.defeat();
    }

    /** フェーズ遷移: 0.6s のシールドを張り (ダメージ&攻撃を一時停止)、パターンを切り替える。 */
    private enterPhase(boss: NonNullable<CrewRun3D["boss"]>, p: 0 | 1 | 2): void {
        boss.phase = p;
        boss.windup = 1.0; // 0.6s シールド (チャージ 1→0)
        boss.heavyTimer = 0.8; // 遷移後は少し溜めてから撃つ
        boss.patternIdx = 0;
        this.shake = Math.max(this.shake, 0.4);
        this.impactBurst(boss.obj.position.clone().setY(3), boss.kind, 1.4);
        this.showBanner(`⚠ PHASE ${p + 1} ⚠`);
        this.sfx.play("phase");
    }

    /** ボスの生き生きとした待機演出＋チャージ/打撃のタイムライン (tell が光り顎が開く)。 */
    private animateBoss(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        const t = this.clock.elapsedTime;
        const ph = boss.idlePh;
        boss.strikeT = Math.max(0, boss.strikeT - dt / 0.35); // 先頭で減衰 (どの分岐より前に)
        const breath = Math.sin(t * 1.6 + ph);
        const sway = Math.sin(t * 0.9 + ph);
        // チャージ係数: 絶対 0.9s ウィンドウ (MAXHT 非依存)、フェーズシールド中は 0
        const cf = boss.windup <= 0 && boss.heavyTimer > 0 && boss.heavyTimer < 0.9
            ? Ease.smooth(1 - boss.heavyTimer / 0.9) : 0;
        // 打撃パンチ: 前半 (0.4) で素早く出て、残りで戻る
        const se = boss.strikeT;
        const sp = se > 0.6 ? 1 - (se - 0.6) / 0.4 : se / 0.6; // 0->1->0 パンチ
        const gf = clamp01(boss.windup); // フェーズシールド中のガード/ひるみ
        const o = boss.obj, P = boss.parts;

        // 共有 idle (毎フレーム適用・最大の硬直解消)
        // 胴の呼吸スクワッシュ (体積保存)。body の元スケール (竜=1.5 / 海獣=非等方) を軸ごとに基準にして乗算する。
        if (P.body) {
            const ud = P.body.userData as { baseScale?: { x: number; y: number; z: number } };
            if (ud.baseScale === undefined) ud.baseScale = { x: P.body.scale.x, y: P.body.scale.y, z: P.body.scale.z }; // 初回に基準を記録
            const b = ud.baseScale;
            const bs = 1 + 0.05 * breath - 0.06 * gf;
            const bxz = 1 - 0.025 * breath + 0.03 * gf;
            P.body.scale.set(b.x * bxz, b.y * bs, b.z * bxz);
        }
        // 体重移動の微小ロール (グループ全体の z)
        o.rotation.z = 0.03 * sway - 0.04 * gf;
        // 注意: o.position は updateBattle が animateBoss の後に set(0,0,boss.z) で上書きするので、
        // 縦の hover/bob は必ず子 (P.body / 各 kind の hover ノード) へ。o.position.y は使わない。

        if (boss.kind === "king") {
            // === 片手の振り下ろしを「全身」で見せる ===
            // (1) torso(腰 pivot)で上半身ごと予備動作: チャージで反って右へ捻り→打撃で前へ踏み込み捻り込む。
            // (2) 右腕だけが笏を振りかぶって叩きつける (anticipation→action→recovery の三段)。左腕は添えるだけ。
            // se=1 開始時の右肩角がチャージ終端(cf=1→RAISE)と連結するので段差が出ない。
            const RAISE = -2.0; // 右肩: 上＆後ろへ (振りかぶり)
            const SLAM = 1.15; // 右肩: 下＆前へ (叩きつけ)
            let shoulderR: number, elbowR: number;
            if (se > 0) {
                const u = 1 - se; // 0..1 (打撃の進行)
                if (u < 0.45) { const k = Ease.quadOut(u / 0.45); shoulderR = RAISE + (SLAM - RAISE) * k; elbowR = 1.2 - 1.4 * k; } // 速い叩き下ろし + 肘が伸びる (whip)
                else { const k = (u - 0.45) / 0.55; shoulderR = SLAM * (1 - k); elbowR = -0.2 * (1 - k); } // idle へ戻す
            } else {
                shoulderR = cf * RAISE + Math.sin(t * 1.4 + ph) * 0.08; // チャージで振りかぶり + idle の揺れ
                elbowR = cf * 1.2 + 0.1; // 振りかぶりで肘を畳む (溜め)
            }
            // (1) 全身 (腰 pivot): 反り/前傾 (rotation.x) + 右肩を引く→振り抜く捻り (rotation.y) + 踏み込み (position.z)。
            //     前 = -z なので前傾は負。捻りは右肩(+x)を -z(前)へ送るのが +rotation.y。
            if (P.torso) {
                P.torso.rotation.x = cf * 0.22 - sp * 0.45 + breath * 0.015; // 反る → 叩き込む + idle 呼吸
                P.torso.rotation.y = -cf * 0.30 + sp * 0.46 + sway * 0.03; // 右肩を引く → 振り抜く
                P.torso.rotation.z = sway * 0.02;
                P.torso.position.z = -sp * 0.32; // 前へ踏み込む (脚は接地のまま=体重移動)
            }
            // (2) 右腕: 笏を振りかぶって叩きつける (片手)。
            if (P.armR) { P.armR.rotation.x = shoulderR; P.armR.rotation.z = -0.12; }
            if (P.elbowR) P.elbowR.rotation.x = elbowR;
            if (boss.spin[0]) boss.spin[0].rotation.z = Math.sin(t * 2 + ph) * 0.04; // 笏の微震 (手ローカル)
            // 左腕: 振らない。軽く曲げて構え、穏やかに呼吸するだけ (全身の捻りには torso 経由で自然に追従)。
            if (P.armL) { P.armL.rotation.x = -0.15 + Math.sin(t * 1.2 + ph) * 0.05; P.armL.rotation.z = 0.2; }
            if (P.elbowL) P.elbowL.rotation.x = 0.5;
            // 護衛の弧 (ゆっくり回る)
            if (boss.spin[1]) { boss.spin[1].rotation.y += dt * 0.15; boss.spin[1].position.y = breath * 0.15; boss.spin[1].rotation.z = sway * 0.04; }
            // 胴: 呼吸 bob のみ (前傾/踏み込みは torso 側)。マントは遅れて大きくはためく (follow-through)。
            if (P.body) P.body.position.y = (3.4 - 2.2) + breath * 0.12; // 腰 pivot 相対 (元 y=3.4 − PIVOT_Y)
            if (P.cape) P.cape.rotation.x = 0.06 * Math.sin(t * 0.9 + ph - 0.6) + sp * 0.5;
        } else if (boss.kind === "dragon") {
            const flap = Math.sin(t * 4 + ph) * 0.55;
            if (boss.spin[0]) boss.spin[0].rotation.z = -0.3 - flap;
            if (boss.spin[1]) boss.spin[1].rotation.z = 0.3 + flap;
            if (boss.spin[0]) boss.spin[0].rotation.x = Math.sin(t * 4 + ph - 0.5) * 0.12; // 翼端が遅れる
            if (boss.spin[1]) boss.spin[1].rotation.x = Math.sin(t * 4 + ph - 0.5) * 0.12;
            if (P.body) P.body.position.y = breath * 0.14; // 胴の呼吸 (scale グループ=grp の子)
            if (P.tail) P.tail.rotation.z = Math.sin(t * 4 + ph - 0.9) * 0.25; // 尾が羽ばたきに追従
            if (P.head) {
                P.head.rotation.x = -0.5 + breath * 0.06 - cf * 0.5 + sp * 0.9; // idle bob -> 反らす (チャージ) -> 突き出す (打撃)
                P.head.position.z = cf * 0.5 - sp * 0.7; // 引いてから突進
            }
            if (P.claws) for (let s = 0; s < P.claws.length; s++) P.claws[s].rotation.x = Math.sin(t * 2.2 + ph + s * Math.PI) * 0.2; // 前爪が交互に
        } else if (boss.kind === "angel") {
            for (let i = 0; i < boss.spin.length; i++) {
                const r = boss.spin[i];
                r.rotation.z += (1.8 + i * 0.6 + cf * 4) * dt; // リングごとの速度 + チャージで加速
                const sc = 1 - cf * 0.25 + sp * 0.4; // チャージで縮み、打撃で弾ける
                if (i < 2) r.scale.setScalar(sc); // (リングのみ。blades=spin[2] はサイズ維持)
            }
            if (P.halo) { P.halo.rotation.z += 0.6 * dt; P.halo.position.y = 5.3 + breath * 0.08; }
            if (P.body) { P.body.position.y = 3.0 + 0.25 * Math.sin(t * 1.5) + cf * 0.4 - sp * 0.2; P.body.rotation.y = Math.sin(t * 0.5 + ph) * 0.05; } // 浮遊 + 昇天 (torso 元 y=3.0)
            if (P.armL) P.armL.rotation.z = 0.3 + Math.sin(t * 1.1 + ph) * 0.15; // 祝福の腕がゆっくり上下
            if (P.armR) P.armR.rotation.z = -(0.3 + Math.sin(t * 1.1 + ph) * 0.15);
        } else if (boss.kind === "leviathan") {
            if (boss.spin[0]) { // 誘灯
                boss.spin[0].rotation.z = Math.sin(t * 1.3) * 0.3;
                boss.spin[0].position.x = Math.sin(t * 0.9) * 0.4;
                boss.spin[0].position.y = 3.2 + breath * 0.2;
            }
            if (P.body) { P.body.rotation.z = sway * 0.05; P.body.position.y = 2.4 + cf * 0.4 - sp * 0.6; P.body.rotation.x = -cf * 0.25; } // 飲み込む前に反らす -> サージ
            if (P.fins) for (let s = 0; s < P.fins.length; s++) P.fins[s].rotation.x = Math.sin(t * 1.4 + ph + (s ? Math.PI : 0)) * 0.18; // ひれを交互に
            if (P.claws) for (let s = 0; s < P.claws.length; s++) P.claws[s].rotation.z = Math.sin(t * 1.6 + ph + s) * 0.22; // 爪の揺れ
        } else if (boss.kind === "chronos") {
            // 時間停止中は針も歯車も完全に止まる (時が止まった演出)。通常時はカチカチ進む。
            const frozen = this.timeStop > 0;
            if (P.body) P.body.position.y = 3.4 + breath * 0.12; // 時計本体の浮遊
            if (!frozen) {
                if (boss.spin[0]) boss.spin[0].rotation.z -= dt * 0.5; // 短針
                if (boss.spin[1]) boss.spin[1].rotation.z -= dt * 1.6; // 長針 (速い)
                if (boss.spin[2]) boss.spin[2].rotation.z += dt * 0.4; // 外周歯車
            }
            // 予告 (timeStopArm) 中は核がせり上がって明滅
            if (boss.spin[1] && this.timeStopArm > 0) boss.spin[1].rotation.z += Math.sin(t * 40) * 0.04; // 針が震える
        } else if (boss.kind === "mech") {
            // ジェットパックで浮遊 + 左右ダッシュのバンク。strafe/hover は root(P.torso) に乗せる。
            const root = P.torso;
            if (root) {
                root.position.x = boss.mechX ?? 0;
                root.position.y = Math.sin(t * 1.8 + ph) * 0.2; // ホバリングの上下
                const vx = (boss.mechTargetX ?? 0) - (boss.mechX ?? 0);
                root.rotation.z = THREE.MathUtils.clamp(-vx * 0.05, -0.16, 0.16); // ダッシュ方向へバンク
                root.rotation.x = -sp * 0.12; // 攻撃で前傾
            }
            const melee = boss.mechMelee ?? "none";
            const mt = boss.mechMeleeT ?? 0;
            // 右腕 (拳叩き): 振り上げ → 叩き下ろし → 戻し。左腕は構えるだけ (片腕攻撃)。
            if (P.armR && P.elbowR) {
                if (melee === "fist") {
                    const u = clamp01(1 - mt / MECH_MELEE_DUR);
                    let sh: number, el: number;
                    if (u < 0.45) { const k = u / 0.45; sh = -2.1 * k; el = 1.3 * k; }
                    else if (u < 0.7) { const k = Ease.quadOut((u - 0.45) / 0.25); sh = -2.1 + 3.3 * k; el = 1.3 - 1.5 * k; }
                    else { const k = (u - 0.7) / 0.3; sh = 1.2 * (1 - k); el = -0.2 * (1 - k); }
                    P.armR.rotation.x = sh; P.elbowR.rotation.x = el; P.armR.rotation.z = -0.1;
                } else { P.armR.rotation.x = Math.sin(t * 1.2 + ph) * 0.1; P.elbowR.rotation.x = 0.25; P.armR.rotation.z = -0.1; }
            }
            if (P.armL && P.elbowL) { P.armL.rotation.x = Math.sin(t * 1.2 + ph + Math.PI) * 0.1; P.elbowL.rotation.x = 0.25; P.armL.rotation.z = 0.1; }
            // 右脚 (蹴り): 引く → 蹴り出す。
            if (P.legR) {
                if (melee === "kick") { const u = clamp01(1 - mt / MECH_MELEE_DUR); P.legR.rotation.x = u < 0.42 ? -0.7 * (u / 0.42) : -0.7 + 2.3 * Ease.quadOut((u - 0.42) / 0.58); }
                else P.legR.rotation.x = Math.sin(t * 1.4 + ph) * 0.05;
            }
            if (P.legL) P.legL.rotation.x = Math.sin(t * 1.4 + ph + Math.PI) * 0.05;
            // ジェット炎を背中ノズルから下向きに (走行接近中も常時噴射)
            if (P.jet && (this.trailTick & 1) === 0) {
                P.jet.getWorldPosition(this._v);
                this.p_emit(this._v.clone().add(new THREE.Vector3((Math.random() - 0.5) * 1.0, -1.5, 0.4)),
                    { tex: "soft", vy: -3 - Math.random() * 2, vx: (Math.random() - 0.5) * 0.6, grav: 5, drag: 2, size0: 0.5, size1: 0.06, col0: 0x9fe8ff, col1: 0xff8a3b, life: 0.3 });
            }
        } else if (boss.kind === "smiler") {
            const root = P.torso;
            if (root) {
                if (boss.smilerLight) {
                    root.position.set(0, Math.sin(t * 1.2 + ph) * 0.2, 0); // 点灯: ゆらゆら浮遊
                    root.scale.setScalar(1);
                    root.rotation.z = Math.sin(t * 0.7 + ph) * 0.04;
                } else {
                    // 消灯(突進): from→target へ寄せつつ群衆へ z ダイブ + 拡大 (笑顔が迫る)。
                    const u = clamp01(1 - (boss.smilerLungeT ?? 0) / SMILER_LUNGE_DUR);
                    const reach = Math.min(1, u / 0.62);
                    const back = u > 0.62 ? (u - 0.62) / 0.38 : 0;
                    const dive = Math.sin(reach * Math.PI * 0.5) * (1 - back);
                    root.position.x = THREE.MathUtils.lerp(boss.smilerFromX ?? 0, boss.smilerLungeX ?? 0, reach);
                    root.position.z = -dive * (boss.z - CROWD_Z - 3); // 群衆手前まで突っ込む (視覚)
                    root.position.y = Math.sin(t * 3) * 0.1;
                    root.scale.setScalar(1 + dive * 0.6); // 迫ると大きく
                    root.rotation.z = Math.sin(t * 9) * 0.05; // 不気味な震え
                }
            }
            // 眼/歯の発光を脈動 (消灯中ほど強く速く明滅)
            if (P.head) {
                const lit = !!boss.smilerLight;
                P.head.traverse((c) => {
                    if (!(c as THREE.Sprite).isSprite || !(c.userData && c.userData.smileGlow)) return;
                    const base = lit ? 0.6 : 1.0;
                    ((c as THREE.Sprite).material as THREE.SpriteMaterial).opacity = base * (0.7 + 0.3 * Math.abs(Math.sin(t * (lit ? 3 : 10))));
                });
            }
        }

        // 攻撃予兆: tell が光って膨らみ、顎が開く (cf + sp で駆動)
        if (boss.tell) {
            const mat = (boss.tell as THREE.Mesh).material as THREE.MeshBasicMaterial;
            if (mat) mat.opacity = Math.min(1, 0.45 + 0.2 * Math.sin(t * 4) + 0.6 * cf + 0.4 * sp);
            boss.tell.scale.setScalar(1 + cf * 0.7 + sp * 0.5);
        }
        if (boss.jaw) boss.jaw.rotation.x = cf * 0.4 + sp * 0.7; // 打撃で最も大きく開く
        // ウィンドアップ中はチャージ粒子を集める
        if (cf > 0.15 && boss.tell) {
            boss.tell.getWorldPosition(this._v);
            this.chargeGather(this._v, boss.kind, cf);
        }
    }

    /** 確定チップの小弾だけを出す (回避不可だが小さく上限済 → 一撃死しない)。 */
    private bossAttack(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        this.bossFireTimer -= dt;
        if (this.bossFireTimer > 0) return;
        this.bossFireTimer = boss.kind === "angel" ? 0.6 : 0.9;
        const dmg = Math.floor(this.drainCarry);
        if (dmg <= 0) {
            if (boss.kind === "angel") this.angelRing(boss);
            return;
        }
        this.drainCarry -= dmg;
        this.fireBossShot(boss.obj.position, dmg);
        if (boss.kind === "angel") this.angelRing(boss); // 美しいリングは演出として常に出す
    }

    /** 天使の放射リング (純演出・無害)。螺旋状に広がる光弾を bullets に流す。 */
    private angelRing(boss: NonNullable<CrewRun3D["boss"]>): void {
        const c = boss.obj.position.clone().setY(3.2);
        const m = 12;
        const phase = this.clock.elapsedTime * 1.3;
        for (let i = 0; i < m; i++) {
            const a = (i / m) * Math.PI * 2 + phase;
            const col = i % 2 ? 0xffe9a8 : 0x9fe0ff;
            const mesh = new THREE.Mesh(
                new THREE.SphereGeometry(0.13, 6, 5),
                new THREE.MeshBasicMaterial({ color: col }),
            );
            mesh.position.copy(c);
            this.scene.add(mesh);
            this.projGlow(mesh, col, 1.2);
            this.bullets.push({ mesh, vel: new THREE.Vector3(Math.cos(a) * 9, Math.sin(a) * 9, 0), life: 0.7, trailCol: col, trailEl: "angel" });
        }
    }

    /** 回避可能なヘビー攻撃の配信。kind×phase でパターンを切り替える。 */
    private bossHeavy(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        // 時間停止の予告中/最中は新しいパターンを始めない (シーケンスが終わるまで待つ)。
        if (boss.kind === "chronos" && (this.timeStop > 0 || this.timeStopArm > 0)) return;
        // 海獣の突進中は専用ヘビー (updateLeviathan が撃つ) に任せ、通常パターンは止める。
        if (boss.kind === "leviathan" && boss.levMode === "charge") return;
        boss.heavyTimer -= dt;
        if (boss.heavyTimer > 0) return;
        const budget = Math.floor(boss.heavyCarry);
        boss.heavyCarry -= budget; // 蓄積を消化 (見た目情報・無制限蓄積防止)
        boss.patternIdx++;
        boss.heavyTimer = [3.2, 2.6, 2.0][boss.phase]; // フェーズが上がるほど詰まる
        // 海獣の回避中は通常パターンを少なめに (突進が主役。間引いて回避を読ませる)。
        if (boss.kind === "leviathan" && boss.levMode === "evade") boss.heavyTimer = [4.6, 3.8, 3.0][boss.phase];
        switch (boss.kind) {
            case "king": this.kingPattern(boss); break;
            case "dragon": this.dragonPattern(boss); break;
            case "angel": this.angelPattern(boss); break;
            case "leviathan": this.leviathanPattern(boss); break;
            case "chronos": this.chronosPattern(boss); break;
            case "mech": this.mechPattern(boss); break;
            case "smiler": this.smilerPattern(boss); break;
        }
        boss.strikeT = 1; // 視覚スラムを弾の発射に同期
        this.signatureBurst(boss.kind, boss.obj.position.clone().setY(3));
    }

    /** ボスごとの必殺 VFX (bossHeavy から呼ぶ)。 */
    private signatureBurst(kind: BossKind, pos: THREE.Vector3): void {
        if (kind === "king") {
            this.emitBurst(pos, 6, { speed: 5, up: true, grav: 7, size0: 0.5, size1: 0.08, col0: 0xffd86b, col1: 0xff8a3b, life: 0.7 });
        } else if (kind === "dragon") {
            this.emitBurst(pos, 8, { speed: 4, up: true, grav: -3, size0: 0.6, size1: 0.1, col0: 0xffae3b, col1: 0x3a1a0a, life: 0.8, tex: "soft" });
            this.p_emit(pos, { tex: "soft", vy: 1, grav: -0.5, size0: 0.6, size1: 2, col0: 0x6a6a6a, col1: 0x2a2a2a, life: 0.9, add: false });
        } else if (kind === "angel") {
            for (let i = 0; i < 5; i++) this.p_emit(pos, { tex: "soft", vx: (Math.random() - 0.5) * 2, vy: 0.5 + Math.random(), grav: -0.6, size0: 0.3, size1: 0.7, col0: 0xfff4cf, col1: 0xffd86b, life: 1, hump: true });
            for (let i = 0; i < 2; i++) this.p_emit(pos, { tex: "streak", vx: (Math.random() - 0.5) * 1.5, grav: 0.3, size0: 0.5, size1: 0.4, spin: (Math.random() - 0.5) * 2, col0: 0xffffff, life: 1.2 });
        } else {
            this.emitBurst(pos, 6, { speed: 4, up: true, grav: 9, size0: 0.4, size1: 0.1, col0: 0xbfffff, life: 0.6 });
            for (let i = 0; i < 4; i++) this.p_emit(pos, { tex: "soft", vy: 1 + Math.random(), grav: -1.5, size0: 0.15, size1: 0.3, col0: 0xeaffff, life: 0.8, hump: true });
        }
    }

    /** 着弾予定 wx の床に色付きマーカーを出し、安全レーンを読めるようにする。 */
    private floorTell(wx: number, color: number, wide = 0.7): void {
        const w = Math.max(wide, this.crowdHalfW + 0.3);
        const s = new THREE.Sprite(new THREE.SpriteMaterial({ color, transparent: true, opacity: 0.8 }));
        s.scale.set(w * 2, 0.5, 1);
        s.position.set(wx, 0.06, CROWD_Z);
        this.scene.add(s);
        this.fx.push({ obj: s, life: TELE_LEAD, max: TELE_LEAD, grow: 0 });
    }

    /** 回避可能なヘビー弾を1つ生成 (一定速で接近・床テレグラフ付き)。 */
    private spawnHeavyArrow(
        boss: NonNullable<CrewRun3D["boss"]>,
        wx: number,
        halfW: number,
        color: number,
        opts: { sweepTo?: number; feint?: boolean; hp?: number } = {},
    ): void {
        const mat = new THREE.MeshBasicMaterial({ color, transparent: !!opts.feint, opacity: opts.feint ? 0.32 : 1 });
        const mesh = new THREE.Mesh(new THREE.SphereGeometry(0.4, 8, 6), mat);
        mesh.scale.x = Math.max(1, halfW / 0.4); // 幅を見た目に反映
        mesh.position.set(wx, 1.2, boss.z);
        this.scene.add(mesh);
        const glow = opts.feint ? undefined : this.projGlow(mesh, color, 1.8); // 囮は控えめ (ブルームなし)
        this.arrows.push({
            mesh, wx, z: boss.z, resolved: false, halfW, blast: false,
            bossSpeed: true, bossHit: true,
            sweep: opts.sweepTo != null ? { from: wx, to: opts.sweepTo } : undefined,
            feint: opts.feint, hp: opts.hp,
            trailCol: opts.feint ? undefined : color, trailEl: boss.kind, glow,
        });
        if (!opts.feint) {
            const tellX = opts.sweepTo != null ? (wx + opts.sweepTo) / 2 : wx;
            const tellW = halfW + (opts.sweepTo != null ? Math.abs(opts.sweepTo - wx) / 2 : 0);
            this.floorTell(tellX, color, tellW);
        }
        // 発射のマズル: 予兆→放出 (上向きバースト + 小さな衝撃輪)
        this._v.copy(boss.obj.position).setY(3);
        this.emitBurst(this._v, 5, { speed: 4, up: true, size0: 0.45, size1: 0.06, col0: color, life: 0.25 });
        this.shockRing(this._v, color, 2.2, 0.25);
        if (!opts.feint) this.sfx.play("bossatk"); // ボス攻撃音 (囮は鳴らさない)
    }

    /** center に MIN_SAFE_GAP(可変) の安全帯を残し、左右をレーン端まで壁で塞ぐ。 */
    private spawnNotchWalls(boss: NonNullable<CrewRun3D["boss"]>, center: number, color: number, gap = MIN_SAFE_GAP): void {
        const half = gap / 2;
        const edge = LANE + 0.5;
        const c = Math.max(-LANE + half + 0.3, Math.min(LANE - half - 0.3, center)); // 安全帯をレーン内に収める
        const leftHi = c - half;
        const rightLo = c + half;
        const lW = (leftHi + edge) / 2;
        if (lW > 0.1) this.spawnHeavyArrow(boss, (-edge + leftHi) / 2, lW, color);
        const rW = (edge - rightLo) / 2;
        if (rW > 0.1) this.spawnHeavyArrow(boss, (rightLo + edge) / 2, rW, color);
    }

    // ---------- ボス攻撃パターン (kind × phase) ----------
    /** 王: 双レーン斉射 → 行進する笏 → 三列の勅令。 */
    private kingPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        const c = 0xff5a4f;
        if (boss.phase === 2) {
            for (const x of [-3, 0, 3]) this.spawnHeavyArrow(boss, x, 0.6, c); // 三列・±1.5 が安全
            return;
        }
        if (boss.phase === 1 && boss.patternIdx % 2 === 1) {
            const dir = boss.patternIdx % 4 < 2 ? 1 : -1; // 端から端へ薙ぐ笏
            this.spawnHeavyArrow(boss, -4 * dir, 1.0, c, { sweepTo: 4 * dir });
            return;
        }
        this.spawnHeavyArrow(boss, -2.2, 1.0, c); // 双レーン斉射 (中央 ~2.4 が安全)
        this.spawnHeavyArrow(boss, 2.2, 1.0, c);
    }

    /** 竜: ブレスの扇 (1スロット安全) → 炎の壁+切れ目 → 薙ぎ払い。 */
    private dragonPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        const c = 0xff5a1e;
        // 突進ダイブ: 3 回に 1 回、現在地へ幅広の一撃で突っ込んでくる (横へ避ける)。
        if (boss.patternIdx % 3 === 2) {
            const tx = THREE.MathUtils.clamp(this.centroidX, -(LANE - 1), LANE - 1);
            this.spawnHeavyArrow(boss, tx, 2.2, c); // 幅広・プレイヤー狙い (floorTell で予告)
            this.impactBurst(boss.obj.position.clone().setY(3), "dragon", 1.4);
            this.shake = Math.max(this.shake, 0.4);
            this.showBanner("🐉 ドラゴンの突進！");
            return;
        }
        if (boss.phase === 2) {
            const dir = boss.patternIdx % 2 ? 1 : -1;
            this.spawnHeavyArrow(boss, -3.4 * dir, 2.2, c, { sweepTo: 3.4 * dir }); // 尾の薙ぎ払い
            return;
        }
        if (boss.phase === 1 && boss.patternIdx % 2 === 0) {
            this.spawnNotchWalls(boss, (Math.random() * 2 - 1) * 2, c); // 炎の壁＋切れ目
            return;
        }
        // ブレスの扇: 1スロットだけ安全 (暗い火球＝囮)
        const safe = [-2, 0, 2, -1, 1][boss.patternIdx % 5];
        for (let x = -3.5; x <= 3.5; x += 1.0) {
            const feint = Math.abs(x - safe) < MIN_SAFE_GAP / 2 + 0.5;
            this.spawnHeavyArrow(boss, x, 0.5, c, feint ? { feint: true } : {});
        }
    }

    /** 天使: 撃ち落とし狙撃 → 収束レーン (2本囮) → ヘイローノヴァ。 */
    private angelPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        const c = 0x9fe0ff;
        if (boss.phase === 2) {
            this.spawnNotchWalls(boss, (Math.random() * 2 - 1) * 1.6, c); // 光の帯以外を塞ぐ
            if (boss.patternIdx % 2 === 0) this.spawnHeavyArrow(boss, this.centroidX, 0.6, c, { hp: boss.max * 0.05 });
            return;
        }
        if (boss.phase === 1) {
            const lanes = [-3, -1.2, 1.2, 3]; // 4 レーンのうち 2 本だけ live、2 本は囮
            const liveA = boss.patternIdx % 4;
            const liveB = (boss.patternIdx * 3 + 1) % 4;
            lanes.forEach((x, i) => this.spawnHeavyArrow(boss, x, 0.55, c, { feint: !(i === liveA || i === liveB) }));
            return;
        }
        // 撃ち落とし可能な狙撃 (細い・火力で破裂)
        this.spawnHeavyArrow(boss, this.centroidX + (Math.random() * 2 - 1), 0.6, c, { hp: boss.max * 0.05 });
    }

    /** 海獣: 潮流のスライド隙間 → 薙ぐ波壁 → 大渦 (1列ずつ安全)。 */
    private leviathanPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        const c = 0x6affd0;
        if (boss.phase === 2) {
            const cols = [-3, 0, 3];
            const safe = boss.patternIdx % 3;
            cols.forEach((x, i) => { if (i !== safe) this.spawnHeavyArrow(boss, x, 0.7, c); });
            return;
        }
        if (boss.phase === 1) {
            const dir = boss.patternIdx % 2 ? 1 : -1; // 薙ぐ波壁
            this.spawnHeavyArrow(boss, -3.2 * dir, 1.6, c, { sweepTo: 3.2 * dir });
            return;
        }
        // 潮流サージ: 安全な隙間が左右にスライド
        this.spawnNotchWalls(boss, Math.sin(boss.patternIdx * 0.9) * 2.2, c, 2.0);
    }

    /**
     * 時の番人: 「時間停止」攻撃のシーケンスを開始する。
     * ① 予告: 安全レーンを床に光らせる (この間は動ける) → ② 停止: 画面モノクロ + 操作不能 +
     * 安全レーン以外を塞ぐ斉射。予告中に正しい位置へ逃げておくのが攻略。phase が上がるほど安全帯が狭い。
     */
    private chronosPattern(boss: NonNullable<CrewRun3D["boss"]>): void {
        if (this.timeStop > 0 || this.timeStopArm > 0) return; // 多重起動しない
        this.chronoSafe = (Math.random() * 2 - 1) * (LANE - 1.8);
        this.timeStopArm = 1.0; // 予告 1.0s (動いて逃げる猶予)
        this.floorTell(this.chronoSafe, 0x9affd0, [2.0, 1.7, 1.4][boss.phase]); // 安全レーンを緑で予告
        this.showBanner("⏳ 時を止めるぞ…");
    }

    /** 時間停止シーケンスの毎フレーム進行 (updateBattle から chronos のときだけ呼ぶ)。 */
    private updateTimeStop(boss: NonNullable<CrewRun3D["boss"]>, dt: number): void {
        if (this.timeStopArm > 0) {
            this.timeStopArm -= dt;
            if (this.timeStopArm <= 0) {
                // === 時間停止 開始 ===
                this.timeStop = 1.5;
                this.setMonochrome(true);
                this.showBanner("⏳ TIME STOP!");
                this.shake = Math.max(this.shake, 0.5);
                const p = boss.obj.position.clone().setY(3.4);
                this.impactBurst(p, "chronos", 1.6);
                this.shockRing(p, 0xffffff, 9, 0.7);
                // 安全レーン以外を塞ぐ斉射 + 端に追い弾 (phase で安全帯が狭まる)
                const gap = [2.0, 1.7, 1.4][boss.phase];
                this.spawnNotchWalls(boss, this.chronoSafe, 0xc0a0ff, gap);
                this.spawnHeavyArrow(boss, this.chronoSafe + 2.8, 0.55, 0xc0a0ff);
                this.spawnHeavyArrow(boss, this.chronoSafe - 2.8, 0.55, 0xc0a0ff);
            }
        } else if (this.timeStop > 0) {
            this.timeStop -= dt;
            if (this.timeStop <= 0) {
                this.timeStop = 0;
                this.setMonochrome(false); // === 時間再開 ===
                this.showBanner("時は動き出す");
            }
        }
    }

    /** 時間停止のモノクロ切替 (実体は updateMonochrome に一元化。パーティゴアアーマーのモノクロと両立させる)。 */
    private setMonochrome(_on: boolean): void { this.updateMonochrome(); }

    /** 時間停止状態を完全に解除 (ステージ遷移/敗北/リスタートで安全側に倒す)。 */
    private clearTimeStop(): void {
        this.timeStop = 0;
        this.timeStopArm = 0;
        this.setMonochrome(false);
    }

    // ---------- ステージ / 終了 ----------
    private stageClear(): void {
        if (this.phase !== "battle") return;
        const bossKind = this.boss?.kind; // boss はこの後破棄するので種類を先に控える (アーマー解放判定用)
        this.logStageStats("CLEAR");
        this.phase = "clear";
        this.clearTimeStop(); // 時間停止中にボスを倒しても確実にモノクロ解除
        this.smilerExitDark(); // 消灯中にボスを倒しても照明を戻す
        this.clearEnemyMissiles(); // 飛来中の敵ミサイルを片付ける
        this.clearTimer = 2.0;
        if (this.boss) {
            this.disposeObj(this.boss.obj);
            this.boss = null;
        }
        for (const s of this.arrows) this.disposeObj(s.mesh);
        this.arrows.length = 0;
        this.crew = Math.min(MAX_CREW, Math.ceil(this.crew * 1.1));
        this.syncMembers();
        this.pulseLanding(true); // 一斉のお祝いジャンプ
        this.shake = Math.max(this.shake, 0.25);
        this.showBanner(`✨ STAGE ${this.stage} CLEAR! ✨`, "クルー +10%");
        // アーマー解放: 進行(S5クリア)でテック / 終盤(S8+)のドラゴン撃破で炎
        if (this.stage >= 5) this.unlockArmor("tech");
        if (bossKind === "dragon" && this.stage >= 8) this.unlockArmor("blaze");
    }

    // ---------- ステージ間ショップ ----------
    /** ボス撃破後、次ステージ前に開く買い物画面。コインを消費して強化を買う。 */
    private openShop(): void {
        if (this.phase === "shop") return;
        this.sfx.setMood("shop");
        this.phase = "shop";
        type Buy = { icon: string; label: string; cost: number; min: number; can: () => boolean; act: () => void };
        // 全商品プール (cost は 10x スケール)。min=解禁ステージ。序盤は安い弱い品、終盤に高い強い品が解禁。
        const pool: Buy[] = [
            { icon: "👥", label: "クルー +15", cost: 50, min: 1, can: () => true, act: () => this.changeCrew(15) },
            { icon: "👥", label: "クルー +30", cost: 90, min: 2, can: () => true, act: () => this.changeCrew(30) },
            { icon: "✨", label: "クルー +25%", cost: 140, min: 2, can: () => true, act: () => this.changeCrew(Math.ceil(this.crew * 0.25) + 4) },
            { icon: "🛡", label: "アーマー +1", cost: 110, min: 1, can: () => this.armor < ARMOR_MAX, act: () => { this.armor++; } },
            { icon: "🔵", label: "シールド +3", cost: 120, min: 1, can: () => true, act: () => { this.shieldHits += SHIELD_GRANT; } },
            { icon: "🔵", label: "シールド +6", cost: 230, min: 3, can: () => true, act: () => { this.shieldHits += SHIELD_GRANT * 2; } },
            { icon: "🛡", label: "バックシールド +3", cost: 140, min: 3, can: () => true, act: () => { this.backShieldHits += SHIELD_GRANT; } },
            { icon: "🟣", label: "天井シールド +3", cost: 160, min: 6, can: () => true, act: () => { this.ceilShieldHits += SHIELD_GRANT; } },
            { icon: "🪖", label: "ライフル入手", cost: 200, min: 2, can: () => !this.ownedWeapons.has(1), act: () => this.grantWeapon(1) },
            { icon: "🔥", label: "ミニガン入手", cost: 480, min: 4, can: () => this.ownedWeapons.has(1) && !this.ownedWeapons.has(2), act: () => this.grantWeapon(2) },
            { icon: "🚀", label: "ロケキャノン入手", cost: 360, min: 5, can: () => !this.ownedWeapons.has(ROCKET_WEAPON), act: () => this.grantWeapon(ROCKET_WEAPON) },
            { icon: "🪃", label: "ブーメラン入手", cost: 360, min: 4, can: () => !this.ownedWeapons.has(BOOMERANG_WEAPON), act: () => this.grantWeapon(BOOMERANG_WEAPON) },
            { icon: "🎯", label: "追尾ミサイル入手", cost: 400, min: 6, can: () => !this.ownedWeapons.has(HOMING_WEAPON), act: () => this.grantWeapon(HOMING_WEAPON) },
            { icon: "🛡", label: "360°タレット展開/補給", cost: 300, min: 6, can: () => true, act: () => this.acquireTurret() },
            { icon: "🛸", label: "ガンドローン +1", cost: 280, min: 7, can: () => this.drones < DRONE_MAX, act: () => this.grantDrone() },
            { icon: "🤖", label: "ロボ +1", cost: 380, min: 4, can: () => true, act: () => { this.robots++; this.rebuildRobots(); } },
            { icon: "🤖", label: "ロボ +2", cost: 720, min: 6, can: () => true, act: () => { this.robots += 2; this.rebuildRobots(); } },
            { icon: "⚡", label: "波動砲", cost: 560, min: 7, can: () => this.cannonStock < CANNON_MAX_STOCK, act: () => this.acquireCannon() },
        ];
        // 解禁済み＋今は無意味でない (can) ものから 3 つをランダム出品 (毎回違う品揃え)。
        const avail = pool.filter((b) => b.min <= this.stage && b.can());
        for (let i = avail.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [avail[i], avail[j]] = [avail[j], avail[i]]; }
        const items = avail.slice(0, 3);
        const el = document.createElement("div");
        el.style.cssText =
            "position:absolute;inset:0;display:flex;align-items:center;justify-content:center;z-index:4;" +
            "background:rgba(6,16,28,0.55);font-family:system-ui,sans-serif;";
        this.host.appendChild(el);
        this.shopEl = el;
        const render = (): void => {
            const cards = items.map((it, i) => {
                const affordable = it.can() && this.coins >= it.cost;
                const bg = affordable ? "#1d3650" : "#243040";
                const op = it.can() ? "1" : "0.4";
                return (
                    `<button data-i="${i}" ${affordable ? "" : "disabled"} style="opacity:${op};` +
                    `display:flex;flex-direction:column;align-items:center;gap:2px;padding:10px 6px;border:2px solid ${affordable ? "#3a72c0" : "#33404f"};` +
                    `border-radius:12px;background:${bg};color:#fff;font-weight:700;cursor:${affordable ? "pointer" : "default"};">` +
                    `<div style="font-size:26px;">${it.icon}</div>` +
                    `<div style="font-size:13px;">${it.label}</div>` +
                    `<div style="font-size:13px;color:${affordable ? "#ffe27a" : "#8aa0b8"};">🪙 ${it.cost}</div>` +
                    `</button>`
                );
            }).join("");
            // アーマー切替パネル: 解放済みは選択可・装備中はハイライト・未解放は🔒＋解放条件をツールチップ表示。
            const armorChips = ARMORS.map((a) => {
                const unlocked = a.id === "none" || this.unlockedArmors.has(a.id);
                const eq = this.equippedArmorId === a.id;
                const bd = eq ? "#ffe27a" : unlocked ? "#3a72c0" : "#33404f";
                const bg = eq ? "#2a5a8c" : unlocked ? "#1d3650" : "#243040";
                return (
                    `<button data-armor="${a.id}" ${unlocked ? "" : "disabled"} title="${unlocked ? a.name : a.unlockHint}" ` +
                    `style="opacity:${unlocked ? 1 : 0.45};display:flex;flex-direction:column;align-items:center;gap:1px;padding:6px 4px;` +
                    `border:2px solid ${bd};border-radius:10px;background:${bg};color:#fff;font-weight:700;cursor:${unlocked ? "pointer" : "default"};min-width:52px;">` +
                    `<div style="font-size:20px;">${unlocked ? a.icon : "🔒"}</div>` +
                    `<div style="font-size:10px;">${a.name}</div>` +
                    `</button>`
                );
            }).join("");
            // 装備中アーマーの説明 (チップを選ぶと再描画されてここも切り替わる)
            const eqDef = this.equippedArmorDef;
            const descBlock =
                `<div style="font-size:12px;color:#cfe3f5;margin-top:8px;min-height:46px;line-height:1.35;padding:6px 8px;background:#0a1a2a;border-radius:8px;">` +
                `<b style="color:#ffe27a;">${eqDef.icon} ${eqDef.name}</b><br>${eqDef.desc}</div>`;
            el.innerHTML =
                `<div style="background:#0e2236;border:3px solid #ffd75e;border-radius:16px;padding:20px 22px;text-align:center;color:#fff;width:min(92%,380px);">` +
                `<div style="font-size:24px;font-weight:800;color:#ffe27a;">🛒 ショップ</div>` +
                `<div style="font-size:16px;margin-top:4px;">所持コイン: <b style="color:#ffd75e;">🪙 ${this.coins}</b></div>` +
                `<div style="font-size:14px;font-weight:800;color:#9fd0ff;margin-top:14px;">🛡 アーマー</div>` +
                `<div style="display:flex;gap:6px;justify-content:center;flex-wrap:wrap;margin-top:6px;">${armorChips}</div>` +
                descBlock +
                `<div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px;margin-top:14px;">${cards}</div>` +
                `<button id="shop-next" style="margin-top:16px;width:100%;padding:12px;border:none;border-radius:10px;background:#2f6fed;color:#fff;font-weight:800;font-size:16px;cursor:pointer;">▶ 次のステージへ</button>` +
                `</div>`;
            el.querySelectorAll<HTMLButtonElement>("button[data-i]").forEach((btn) => {
                btn.onclick = () => {
                    const it = items[Number(btn.dataset.i)];
                    if (!it.can() || this.coins < it.cost) return;
                    this.coins -= it.cost;
                    it.act();
                    this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), 0xffe27a);
                    render(); // コイン残高と購入可否を更新
                };
            });
            el.querySelectorAll<HTMLButtonElement>("button[data-armor]").forEach((btn) => {
                btn.onclick = () => {
                    const id = btn.dataset.armor!;
                    if (id !== "none" && !this.unlockedArmors.has(id)) return;
                    this.equipArmor(id);
                    this.spark(new THREE.Vector3(this.centroidX, 1.2, CROWD_Z), this.equippedArmorDef.accent || 0x9fd0ff);
                    render(); // 装備ハイライトを更新
                };
            });
            el.querySelector<HTMLButtonElement>("#shop-next")!.onclick = () => {
                this.closeShop();
                this.nextStage();
            };
        };
        render();
    }

    private closeShop(): void {
        if (this.shopEl) {
            this.shopEl.remove();
            this.shopEl = null;
        }
    }

    private nextStage(): void {
        this.stage++;
        this.sfx.setMood("run");
        this.applyBiome(this.stage); // (stage-1)%N でバイオームを切替 (森→砂漠→雪→海→火山→墓地→空)
        this.distance = 0;
        this.spawnTimer = 0.6;
        this.speed = BASE_SPEED;
        this.phase = "running";
        this.stageTime = 0;
        this.spawnLog = {};
        this.turnWallT = 3.0; // Level 0 突入時は少し待って最初の行き止まりを出す
        if (this.biomeIdx === LEVEL0_BIOME) this.showBanner("LEVEL 0", "つきあたりは曲がれ！");
        else this.showBanner(`STAGE ${this.stage}`);
    }

    /** バランス検証ログ: 現ステージの所要秒・クルー・出現ギミック数を console に出す。 */
    private logStageStats(tag: string): void {
        const sp = Object.entries(this.spawnLog)
            .map(([k, v]) => `${k}:${v}`)
            .join(" ");
        console.log(`[CrewRun] S${this.stage} ${tag} time=${this.stageTime.toFixed(1)}s crew=${this.crew} wpn=${this.weaponTier} spawns{ ${sp} }`);
    }

    private defeat(): void {
        if (this.phase === "result") return;
        this.logStageStats("DEFEAT");
        this.clearTimeStop();
        this.smilerExitDark(); // 消灯中に敗北しても照明を戻す
        this.clearEnemyMissiles();
        this.clearZoneModifiers(); // C/D のビュー/操作反転を結果画面前に戻す
        this.clearEvent(true);
        this.phase = "result";
        const score = this.crew;
        const best = saveBest(score);
        this.showResult(score, best);
    }

    private showResult(score: number, best: number): void {
        const isNew = score >= best && score > this.bestAtStart;
        const el = document.createElement("div");
        el.style.cssText =
            "position:absolute;inset:0;display:flex;align-items:center;justify-content:center;z-index:4;" +
            "background:rgba(6,16,28,0.4);font-family:system-ui,sans-serif;";
        el.innerHTML =
            `<div style="background:#0e2236;border:3px solid #d64541;border-radius:16px;padding:24px 28px;text-align:center;color:#fff;min-width:260px;">` +
            `<div style="font-size:28px;font-weight:800;color:#ff8f8f;">💀 やられた…</div>` +
            `<div style="font-size:19px;font-weight:700;color:#ffe27a;margin-top:14px;">STAGE ${this.stage} まで到達</div>` +
            `<div style="font-size:16px;margin-top:8px;">生き残ったクルー: ${score}</div>` +
            `<div style="font-size:14px;color:#9fb3c8;margin-top:4px;">ベスト人数: ${best}${isNew ? " 🆕" : ""}</div>` +
            `<div style="display:flex;gap:10px;margin-top:20px;">` +
            `<button id="cr-retry" style="flex:1;padding:12px;border:none;border-radius:10px;background:#2f6fed;color:#fff;font-weight:700;cursor:pointer;">もう一回</button>` +
            `<button id="cr-back" style="flex:1;padding:12px;border:none;border-radius:10px;background:#40526a;color:#fff;font-weight:700;cursor:pointer;">もどる</button>` +
            `</div></div>`;
        this.host.appendChild(el);
        this.resultEl = el;
        el.querySelector<HTMLButtonElement>("#cr-retry")!.onclick = () => this.restart();
        el.querySelector<HTMLButtonElement>("#cr-back")!.onclick = () => this.exit();
    }

    private restart(): void {
        for (const e of this.entities) { if (e.type === "bruiser" && e.pounceTell) this.disposeObj(e.pounceTell); this.disposeObj(e.obj); }
        this.entities.length = 0;
        for (const b of this.bullets) this.disposeObj(b.mesh);
        this.bullets.length = 0;
        for (const a of this.arrows) this.disposeObj(a.mesh);
        this.arrows.length = 0;
        for (const f of this.fx) this.disposeObj(f.obj);
        this.fx.length = 0;
        if (this.boss) {
            this.disposeObj(this.boss.obj);
            this.boss = null;
        }
        if (this.weaponMesh) {
            this.scene.remove(this.weaponMesh);
            this.weaponMesh = null;
        }
        if (this.robotMesh) {
            this.scene.remove(this.robotMesh);
            this.robotMesh = null;
        }
        if (this.resultEl) {
            this.resultEl.remove();
            this.resultEl = null;
        }
        this.closeShop();
        this.clearTimeStop();
        this.smilerExitDark();
        this.clearEnemyMissiles();
        this.clearCannon();
        this.clearTurret();
        this.clearDrones();
        this.clearEvent(false); // 世界はこの後 applyBiome(1) で復元
        this.sfx.setMood("run"); // リスタートは必ず通常BGMへ (ボス/Level死亡時に前の mood が残らないように)
        this.eventDoneStage = 0;
        this.phase = "running";
        this.stage = 1;
        this.applyBiome(1); // S1 (forest) に戻す (死亡が雪/海ステージでも世界を復帰)
        this.crew = START_CREW;
        this.noHitT = 0;
        this.crewPeak = START_CREW;
        this.regenCarry = 0;
        this.weaponTier = 0;
        this.ownedWeapons.clear();
        this.rocketCdT = 0;
        this.homingCdT = 0;
        this.minigunHeat = 0;
        this.minigunOverheat = 0;
        if (this.boomerang) { this.disposeObj(this.boomerang.mesh); this.boomerang = null; }
        for (const m of this.missiles) this.disposeObj(m.mesh);
        this.missiles.length = 0;
        this.robotFireT = 0;
        this.robotPdT = 0;
        this.armor = 0;
        this.shieldHits = 0;
        this.backShieldHits = 0;
        this.ceilShieldHits = 0;
        this.coins = 0;
        this.combo = 0;
        this.comboTimer = 0;
        this.robots = 0;
        this.distance = 0;
        this.speed = BASE_SPEED;
        this.spawnTimer = 0;
        this.centroidX = 0;
        this.targetX = 0;
        this.shake = 0;
        this.drainCarry = 0;
        this.battleTime = 0;
        this.stageTime = 0;
        this.spawnLog = {};
        this.bestAtStart = loadBest();
        this.members.length = 0;
        this.syncMembers();
    }

    // ---------- 小物 ----------
    private makeBarSprite(big = false): THREE.Sprite {
        const s = new THREE.Sprite(new THREE.SpriteMaterial({ color: 0xff5a5a }));
        s.scale.set(big ? 6 : 1.6, big ? 0.3 : 0.18, 1);
        return s;
    }
    private updateBar(e: HpEnt): void {
        const ratio = Math.max(0, e.hp / e.max);
        const full = e.type === "bruiser" && e.variant === "giant" ? 4 : 1.6;
        e.bar.scale.x = full * ratio;
        e.bar.visible = ratio > 0;
    }
    private updateBossBar(): void {
        if (!this.boss) return;
        this.boss.bar.scale.x = 6 * Math.max(0, this.boss.hp / this.boss.max);
    }

    private muzzle(color: number): void {
        this._v.set(this.centroidX, 1.2, CROWD_Z + 0.8);
        this.p_emit(this._v, { tex: "glow", size0: 0.5, size1: 1.4, col0: color, life: 0.08 });
        this.emitBurst(this._v, 3, { speed: 2, up: true, size0: 0.3, size1: 0.05, col0: color, life: 0.15 });
    }
    private spark(pos: THREE.Vector3, color: number): void {
        this._v.copy(pos).setY(1.0);
        this.emitBurst(this._v, 4, { speed: 3, drag: 3, size0: 0.4, size1: 0.05, col0: color, life: 0.2 });
        this.p_emit(this._v, { tex: "glow", size0: 0.5, size1: 1.2, col0: color, life: 0.12 });
    }
    private boom(pos: THREE.Vector3): void {
        this.impactBurst(this._v.copy(pos).setY(1.2), "dragon", 1); // 橙の閃光+輪+スパーク+煙 (シェイクは impactBurst 内)
        this.sfx.play("boom");
    }
    private popWorld(text: string, wx: number, color: string): void {
        const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: makeLabelTexture(text, { fill: color, size: 56 }), transparent: true }));
        s.scale.set(2.4, 1.2, 1);
        s.position.set(wx, 2.2, CROWD_Z + 1);
        this.scene.add(s);
        this.fx.push({ obj: s, life: 0.8, max: 0.8, grow: 0 });
    }
    private hit(): void {
        this.shake = Math.max(this.shake, 0.3);
        this.noHitT = 0; // 被弾でクリーン走行回復が止まる (リスクを冒さず無傷で走った報酬にする)
    }

    /**
     * C/D 全画面ティント: カメラ子の大きな板を遅延生成し、反転(紫)/重力(水色)の残量で不透明度と色をフェード。
     * update ループから毎フレーム呼ぶ (毎フレ THREE オブジェクト確保はしない — 1 枚を使い回す)。
     */
    private updateZoneTint(): void {
        const target = Math.max(this.invertT > 0 ? Math.min(1, this.invertT) : 0,
            this.gravT > 0 ? Math.min(1, this.gravT) : 0);
        if (target <= 0 && !this.invertTint) return; // まだ一度も出していないなら何もしない
        if (!this.invertTint) {
            // カメラの少し前に置く大きな板 (near=0.1 なので 0.2 手前で全画面を覆う)
            const mat = new THREE.MeshBasicMaterial({
                color: 0xc98bff, transparent: true, opacity: 0, depthWrite: false, depthTest: false,
                blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
            });
            const quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2), mat);
            quad.position.set(0, 0, -0.2); // カメラ前方
            quad.renderOrder = 999;
            this.camera.add(quad);
            this.invertTint = quad;
        }
        const m = this.invertTint.material as THREE.MeshBasicMaterial;
        // 重力(水色)を優先表示・無ければ反転(紫)。両方なら水色寄り。
        m.color.setHex(this.gravT > 0 ? 0x7be0ff : 0xc98bff);
        m.opacity = target * 0.22; // 視界は妨げない程度の薄いティント
        this.invertTint.visible = m.opacity > 0.001;
    }

    /** C/D ゾーンモディファイアを即時解除 (ビューのロール/操作反転/天井持ち上げを元に戻す)。遷移の境界で呼ぶ。 */
    private clearZoneModifiers(): void {
        this.invertT = 0;
        this.gravT = 0;
        this.gravRoll = 0;
        this.turnSwingT = 0; // ターン壁のカメラスイングも安全側に解除
        // カメラ姿勢は毎フレ lookAt が完全に上書きするので rotation.z は触らない
        // (ここで 0 を入れると +z 向きカメラの正しい z 成分を潰して 1 フレーム反転する)。
        if (this.invertTint) this.invertTint.visible = false;
    }

    // ---------- コイン / コンボ ----------
    private readonly COMBO_WINDOW = 2.6; // この秒数内に次を倒せばコンボ継続
    /** コンボ倍率 (コイン)。20 コンボで頭打ちの ×2。 */
    private get comboMult(): number {
        return 1 + Math.min(this.combo, 20) * 0.05;
    }
    /** 撃破/破壊の報酬: コインを与え、コンボを伸ばし、浮かぶコイン表示を出す。 */
    private award(base: number, wx: number, eliteMul = 1): void {
        this.combo++;
        this.comboTimer = this.COMBO_WINDOW;
        const gain = Math.max(1, Math.round(base * eliteMul * this.comboMult));
        this.coins += gain;
        this.sfx.play("coin");
        this.popWorld(`+${gain}🪙`, wx, "#ffe27a");
        if (this.combo >= 5 && this.combo % 5 === 0) this.showBanner(`🔥 ${this.combo} COMBO!`);
    }
    /** 一定確率で敵をエリート化 (金オーラ・高 HP・大きめ・撃破コイン×3)。 */
    private maybeElite(e: HpEnt, chance = 0.12): void {
        if (Math.random() >= chance) return;
        e.elite = true;
        e.hp = Math.ceil(e.hp * 2.2);
        e.max = e.hp;
        e.obj.scale.multiplyScalar(1.22);
        this.projGlow(e.obj, 0xffd24d, 3.0); // 金色のオーラ (子なので一緒に破棄される)
        this.updateBar(e);
    }

    /** 撃破コインの基礎値 (type ごと)。entity の coin 上書きがあればそれを優先。 */
    private killCoin(e: Entity): number {
        if (e.coin != null) return e.coin;
        if (e.type === "mob") return e.weak ? 10 : 20;
        if (e.type === "archer") return e.bomber ? 40 : 30;
        if (e.type === "bruiser") return e.variant === "giant" ? 150 : e.variant === "dragon" ? 60 : e.variant === "bacteria" ? 70 : e.variant === "partygoer" ? 80 : e.variant === "flyer" ? 65 : e.variant === "orbiter" ? 75 : 50;
        if (e.type === "rocket") return 20;
        return 10;
    }

    // ---------- 天候 (バイオーム別の環境パーティクル) ----------
    private updateWeather(dt: number): void {
        const b = this.biomeIdx;
        // バイオーム別のレート (個/秒)。雪は密に、海は控えめに。
        const rate = b === 2 ? 16 : b === 0 ? 6 : b === 1 ? 11 : b === 4 ? 10 : b === 5 ? 7 : b === 6 ? 9 : 4;
        this.weatherAcc += dt * rate;
        let n = Math.floor(this.weatherAcc);
        this.weatherAcc -= n;
        n = Math.min(n, 4); // 1 フレームの上限 (combat 用プールを枯らさない)
        for (let i = 0; i < n; i++) this.emitWeather(b);
    }
    private emitWeather(b: number): void {
        const x = this.centroidX * 0.3 + (Math.random() * 2 - 1) * 14;
        const z = CROWD_Z + Math.random() * 34;
        if (b === 2) {
            this._v.set(x, 9 + Math.random() * 3, z); // 雪
            this.p_emit(this._v, { tex: "soft", vx: (Math.random() - 0.5) * 0.4, vy: -1.4, drag: 0.6, size0: 0.18, size1: 0.16, col0: 0xffffff, life: 3.2 });
        } else if (b === 0) {
            this._v.set(x, 7 + Math.random() * 3, z); // 落ち葉
            this.p_emit(this._v, { tex: "soft", vx: (Math.random() - 0.5) * 1.2, vy: -1.1, drag: 0.4, spin: 3, size0: 0.17, size1: 0.16, col0: Math.random() < 0.5 ? 0xd89a3a : 0x6fae3d, life: 3.0 });
        } else if (b === 1) {
            this._v.set(x, 1 + Math.random() * 4, z); // 砂塵 (横風)
            this.p_emit(this._v, { tex: "streak", vx: 6 + Math.random() * 3, vy: 0.2, drag: 0.3, size0: 0.5, size1: 0.1, col0: 0xd9b878, life: 1.0 });
        } else if (b === 4) {
            this._v.set(x, 0.2 + Math.random() * 1.5, z); // 火山の残り火 (上昇)
            this.p_emit(this._v, { tex: "glow", vx: (Math.random() - 0.5) * 0.5, vy: 1.6 + Math.random(), drag: 0.2, size0: 0.18, size1: 0.02, col0: 0xff7a2e, col1: 0xffd24d, life: 2.2, add: true });
        } else if (b === 5) {
            this._v.set(x, 0.5 + Math.random() * 3, z); // 墓地の蛍火
            this.p_emit(this._v, { tex: "glow", vx: (Math.random() - 0.5) * 0.6, vy: (Math.random() - 0.5) * 0.4, drag: 0.5, size0: 0.14, size1: 0.14, col0: 0x8affc0, life: 2.4, add: true, hump: true });
        } else if (b === 6) {
            this._v.set(x, 1 + Math.random() * 7, z); // 空のきらめき
            this.p_emit(this._v, { tex: "glow", vy: -0.3, drag: 0.5, size0: 0.16, size1: 0.02, col0: 0xffffff, col1: 0xbfe9ff, life: 1.8, add: true });
        } else {
            this._v.set(x, 0.1, z); // 海: 軽い水しぶき
            this.p_emit(this._v, { tex: "soft", vy: 1.2, grav: 3, size0: 0.12, size1: 0.02, col0: 0xbfe2ef, life: 0.8 });
        }
    }

    private updateFx(dt: number): void {
        for (let i = this.fx.length - 1; i >= 0; i--) {
            const f = this.fx[i];
            f.life -= dt;
            const sp = f.obj as THREE.Sprite;
            const k = f.life / f.max;
            (sp.material as THREE.SpriteMaterial).opacity = Math.max(0, k);
            if (f.grow > 0) sp.scale.multiplyScalar(1 + f.grow * dt);
            else sp.position.y += dt * 1.8;
            if (f.life <= 0) {
                this.disposeObj(f.obj);
                this.fx.splice(i, 1);
            }
        }
    }

    private disposeObj(o: THREE.Object3D): void {
        this.scene.remove(o);
        o.traverse((c) => {
            // bacteria FBX の clone は geometry/material/skeleton をキャッシュ元と共有する。
            // dispose すると元モデルが壊れて 2 体目以降が出なくなるためスキップ。
            if (c.userData.shared) return;
            const m = c as THREE.Mesh;
            // 共有クルー素体は dispose で破棄してはいけない (全インスタンス共有)。
            if (
                m.geometry &&
                m.geometry !== this.crewBodyGeo &&
                m.geometry !== this.enemyGeo &&
                m.geometry !== this.archerGeo &&
                m.geometry !== this.blobGeo
            ) {
                m.geometry.dispose();
            }
            const mat = (c as THREE.Mesh).material as THREE.Material | THREE.Material[] | undefined;
            if (mat && mat !== this.crewMat && mat !== this.woodMat && mat !== this.propMat && mat !== this.giantMat) {
                if (Array.isArray(mat)) mat.forEach((x) => x.dispose());
                else mat.dispose();
            }
        });
    }
}
