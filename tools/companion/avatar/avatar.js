// EndKnot AI 実況相棒アバター — three-vrm でVRMを描き、companion.py の WebSocket から
// {mouth, speaking, subtitle, emotion, speaker} を受けて口パク/表情、まばたき・アイドルはローカルのタイマー駆動。
// model2.vrm を置くと2体並び (--duo の掛け合い実況用) になり、speaker タグで口パク/表情を振り分ける。
// 背景透過で描くので OBS ブラウザソースにそのまま重ねられる。CDN は使わず vendor/ のローカルESMだけで動く。

import * as THREE from 'three';
import { GLTFLoader } from './vendor/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from './vendor/three-vrm.module.js';

const params = new URLSearchParams(location.search);
const SHOW_SUBTITLE = !params.has('nosub');
const SHOW_STATUS = !params.has('nostatus');
const MODEL_URL = params.get('model') || './model.vrm';
const MODEL2_URL = params.get('model2') || './model2.vrm'; // 無ければ黙って1体モード

const noticeEl = document.getElementById('notice');
const subtitleEl = document.getElementById('subtitle');
const statusEl = document.getElementById('status');
if (!SHOW_STATUS && statusEl) statusEl.style.display = 'none';

function showNotice(html) {
  if (!noticeEl) return;
  noticeEl.innerHTML = html;
  noticeEl.style.display = 'block';
}
function hideNotice() { if (noticeEl) noticeEl.style.display = 'none'; }

// ---- three.js 基本セットアップ ----
const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.setClearColor(0x000000, 0); // 完全透過
renderer.outputColorSpace = THREE.SRGBColorSpace;
document.getElementById('canvas-wrap').appendChild(renderer.domElement);

const scene = new THREE.Scene();

const camera = new THREE.PerspectiveCamera(28, window.innerWidth / window.innerHeight, 0.1, 20);
camera.position.set(0, 1.35, 1.15);

// 立ち絵向けの素直なライティング (正面やや上から + 環境光)
const dir = new THREE.DirectionalLight(0xffffff, 2.2);
dir.position.set(0.5, 2.0, 1.5);
scene.add(dir);
scene.add(new THREE.AmbientLight(0xffffff, 1.1));

window.addEventListener('resize', () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
  layoutScene();
});

// ---- VRM 読み込み (slot 0 = 主役 model.vrm / slot 1 = 相方 model2.vrm・任意) ----
// avatars[slot] = { vrm, driver, 口パク/表情/まばたきの個別状態 }
const avatars = [];

const loader = new GLTFLoader();
loader.register((parser) => new VRMLoaderPlugin(parser));

function makeAvatarState(vrm) {
  return {
    vrm,
    driver: makeFaceDriver(vrm),
    mouthTarget: 0,
    mouthCurrent: 0,
    emotion: 'neutral',
    emotionValue: 0,
    emotionCurrent: {},
    blinkTimer: 1.5 + Math.random() * 3,
    blinkPhase: -1,          // -1 = 閉じてない, 0..1 = 進行中
    baseY: 0,
    baseRotY: 0,
    idlePhase: Math.random() * 6, // 2体が同期して揺れると人形っぽいので位相をずらす
  };
}

function loadModel(url, slot, required) {
  if (required) showNotice('モデルを読み込み中…');
  loader.load(
    url,
    (gltf) => {
      const vrm = gltf.userData.vrm;
      if (!vrm) {
        if (required) showNotice('この .vrm から VRM データを取り出せませんでした。<br>VRM 0.x / 1.0 形式のモデルを置いてください。');
        return;
      }
      // 軽量化 (未使用頂点/ジョイント除去)
      try { VRMUtils.removeUnnecessaryVertices(gltf.scene); } catch (_) {}
      try { VRMUtils.combineSkeletons(gltf.scene); } catch (_) {}
      // VRM0 は -Z 向きなので 180° 回してカメラ (+Z) を向かせる。VRM1 は no-op。
      try { VRMUtils.rotateVRM0(vrm); } catch (_) {}

      vrm.scene.traverse((o) => { o.frustumCulled = false; });

      applyRestPose(vrm); // 初期は T ポーズなので腕を下ろした自然な立ち姿にする

      avatars[slot] = makeAvatarState(vrm);
      scene.add(vrm.scene);
      layoutScene();
      if (required) hideNotice();
      window.__avatars = avatars; // デバッグ用フック
      if (slot === 0) {
        window.__vrm = vrm;
        window.__setMouth = (v) => { avatars[0].mouthTarget = Math.max(0, Math.min(1, v)); };
      }
      console.log(`[avatar] VRM loaded (slot ${slot})`);
    },
    undefined,
    (err) => {
      if (required) {
        console.warn('[avatar] VRM load failed', err);
        showNotice(
          'アバターのモデルがまだありません。<br>' +
          'このフォルダに <code>model.vrm</code> を置いてください。<br>' +
          '<small>(VRoid Studio 等で作った VRM をそのままリネームで OK)</small>'
        );
      } else {
        console.log('[avatar] model2.vrm なし — 1体モードで表示します');
      }
    }
  );
}

loadModel(MODEL_URL, 0, true);
loadModel(MODEL2_URL, 1, false);

// VRM は初期状態が T ポーズ (両腕が真横=はりつけ状態) なので、上腕を下ろして自然な立ち姿にする。
// normalized ボーン空間で操作する (VRM0/VRM1 差を three-vrm が吸収してくれる)。
function applyRestPose(vrm) {
  const h = vrm.humanoid;
  if (!h) return;
  const set = (bone, z) => { const n = h.getNormalizedBoneNode(bone); if (n) n.rotation.z = z; };
  // 上腕を約63°下ろす (左腕は -Z, 右腕は +Z で下がる)。少し内側に肘を曲げると自然。
  set('leftUpperArm', -1.10);
  set('rightUpperArm', 1.10);
  set('leftLowerArm', -0.15);
  set('rightLowerArm', 0.15);
}

// ---- 配置とフレーミング ----
// 1体: 中央のバストアップ (従来どおり)。
// 2体: 主役を画面右・相方を画面左に並べ、両方の頭が必ず収まる距離までカメラを引く。
const DUO_OFFSET_X = 0.30;

function layoutScene() {
  const active = avatars.filter(Boolean);
  if (active.length === 0) return;
  const duo = active.length >= 2;
  for (let slot = 0; slot < avatars.length; slot++) {
    const av = avatars[slot];
    if (!av) continue;
    const x = duo ? (slot === 0 ? DUO_OFFSET_X : -DUO_OFFSET_X) : 0;
    av.vrm.scene.position.x = x;
    av.baseY = 0;
    // 少しだけ内側 (相方の方) を向かせて「並んでいる」感を出す
    av.baseRotY = duo ? (slot === 0 ? -0.10 : 0.10) : 0;
    av.vrm.scene.rotation.y = av.baseRotY;
  }
  frameShot(active.map((a) => a.vrm));
}

// 頭〜上半身が画面に収まるようカメラを合わせる (バストアップ)。
// 頭頂 (髪・アホ毛込み) はバウンディングボックスから取り、余白付きで必ず縦画角に収める。
// 2体のときは横方向 (両者の外側の肩) も必ず収まる距離まで引く。
function frameShot(vrms) {
  let headYSum = 0;
  let headCount = 0;
  const box = new THREE.Box3();
  for (const vrm of vrms) {
    vrm.scene.updateMatrixWorld(true);
    const headNode = vrm.humanoid?.getNormalizedBoneNode('head');
    if (headNode) {
      const p = new THREE.Vector3();
      headNode.getWorldPosition(p);
      headYSum += p.y;
      headCount++;
    }
    box.union(new THREE.Box3().setFromObject(vrm.scene));
  }
  if (headCount === 0) return;
  const headY = headYSum / headCount;
  const topY = box.max.y;
  const targetY = headY - 0.18;      // 顔のやや下を中心に
  const headroom = 0.05;
  const halfFov = THREE.MathUtils.degToRad(camera.fov / 2);
  const distV = (topY + headroom - targetY) / Math.tan(halfFov);
  // 横: 2体の外側の端 + 余白が水平画角に収まる距離 (tan(hFov/2) = tan(vFov/2) * aspect)
  const halfWidth = Math.max(Math.abs(box.max.x), Math.abs(box.min.x)) + 0.06;
  const distH = halfWidth / (Math.tan(halfFov) * camera.aspect);
  const dist = Math.max(0.8, distV, distH);
  camera.position.set(0, targetY, dist);
  camera.lookAt(0, targetY, 0);
}

// ---- 表情ドライバ ----
// 正常な VRM は expressionManager で動かすが、表情定義に bind が 1 本も入っていないモデル
// (東北ずん子 VRM 1.0 は全 expression が bind 0 本) では setValue しても何も起きない。
// その場合はメッシュの morph target (MMD 式の「あ」「目閉じ」等) を直接駆動する。
const MORPH_FALLBACK_MAP = {
  aa: ['あ'],
  blink: ['目閉じ'],
  happy: ['ニコ目', '頬染め'],
  angry: ['怒り眉'],
  sad: ['困り眉'],
  relaxed: ['ジト目'],
  surprised: ['瞳小さく'],  // 〇〇目は白目サークル化する (目のテクスチャ差し替え用) ので使わない
};

function makeFaceDriver(vrm) {
  const em = vrm.expressionManager;
  const hasBinds = !!em && em.expressions.some((e) => (e.binds?.length || 0) > 0);
  if (hasBinds) {
    console.log('[avatar] face driver: expressionManager');
    return { set: (name, v) => em.setValue(name, v) };
  }
  const meshes = [];
  vrm.scene.traverse((o) => { if (o.morphTargetDictionary && o.morphTargetInfluences) meshes.push(o); });
  console.log(`[avatar] face driver: direct morph fallback (expression binds が空 / meshes=${meshes.length})`);
  return {
    set: (name, v) => {
      const targets = MORPH_FALLBACK_MAP[name];
      if (!targets) return;
      for (const mesh of meshes) {
        for (const t of targets) {
          const i = mesh.morphTargetDictionary[t];
          if (i !== undefined) mesh.morphTargetInfluences[i] = v;
        }
      }
    },
  };
}

// ---- 表情・アニメーション状態 (発話状態はグローバル、表情/口は avatar 個別) ----
let speaking = false;

const EMOTION_PRESETS = ['happy', 'angry', 'sad', 'relaxed', 'surprised'];

function applyExpressions(av, delta) {
  // 口: WS の mouth は「喋りのスパイク」なので、速く開いてゆっくり閉じる非対称追従にする。
  // (対称に均すとスパイクが消えて口が動いて見えない — これが初回の「口が動かない」原因だった)
  const openRate = delta * 35;   // 開くのは速く
  const closeRate = delta * 9;   // 閉じるのはゆっくり (連続発話中は開きっぱなしに近くなる)
  const rate = av.mouthTarget > av.mouthCurrent ? openRate : closeRate;
  av.mouthCurrent += (av.mouthTarget - av.mouthCurrent) * Math.min(1, rate);
  av.driver.set('aa', av.mouthCurrent);

  // まばたき
  av.blinkTimer -= delta;
  if (av.blinkPhase < 0 && av.blinkTimer <= 0) av.blinkPhase = 0;
  if (av.blinkPhase >= 0) {
    av.blinkPhase += delta / 0.12; // 約0.12秒で開閉
    // 0→1→0 の三角波
    const b = av.blinkPhase < 0.5 ? av.blinkPhase * 2 : (1 - av.blinkPhase) * 2;
    av.driver.set('blink', Math.max(0, Math.min(1, b)));
    if (av.blinkPhase >= 1) { av.blinkPhase = -1; av.blinkTimer = 1.5 + Math.random() * 4; }
  }

  // 感情表情 (WS の emotion。無指定なら neutral へ減衰)
  for (const p of EMOTION_PRESETS) {
    const want = (p === av.emotion) ? av.emotionValue : 0;
    const cur = (av.emotionCurrent[p] || 0) + (want - (av.emotionCurrent[p] || 0)) * Math.min(1, delta * 6);
    av.emotionCurrent[p] = cur;
    av.driver.set(p, cur);
  }
  // 感情は数秒で自然に薄れる。しゃべり続けていても表情ロックしないよう発話中も減衰し
  // (長文でも約3秒で戻り始める)、無音になったら速めにスッと戻す。
  av.emotionValue = Math.max(0, av.emotionValue - delta * (speaking ? 0.22 : 0.5));
}

// アイドル: ごく浅い呼吸 (上下) と左右の揺れで棒立ち感を消す。
function applyIdle(av, t) {
  const tp = t + av.idlePhase;
  const breathe = Math.sin(tp * 1.6) * 0.004;
  av.vrm.scene.position.y = av.baseY + breathe;
  av.vrm.scene.rotation.y = av.baseRotY + Math.sin(tp * 0.5) * 0.03;
}

// ---- 描画ループ ----
const clock = new THREE.Clock();
function animate() {
  requestAnimationFrame(animate);
  const delta = clock.getDelta();
  const t = clock.elapsedTime;
  for (const av of avatars) {
    if (!av) continue;
    applyExpressions(av, delta);
    applyIdle(av, t);
    av.vrm.update(delta); // 表情/ボーンを実際のメッシュへ反映
  }
  renderer.render(scene, camera);
}
animate();

// ---- companion.py との WebSocket ----
// ページと同じホスト:ポートの /ws に繋ぐ (companion が静的配信も WS も同一サーバで出す)。
function connectWs() {
  let url;
  try {
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    url = `${proto}//${location.host}/ws`;
  } catch (_) {
    url = 'ws://127.0.0.1:8777/ws';
  }

  let ws;
  try { ws = new WebSocket(url); }
  catch (_) { setTimeout(connectWs, 2000); return; }

  ws.onopen = () => { if (statusEl) statusEl.classList.add('connected'); };
  ws.onclose = () => {
    if (statusEl) statusEl.classList.remove('connected');
    for (const av of avatars) { if (av) av.mouthTarget = 0; }
    speaking = false;
    setTimeout(connectWs, 2000); // 自動再接続
  };
  ws.onerror = () => { try { ws.close(); } catch (_) {} };
  ws.onmessage = (ev) => {
    let m;
    try { m = JSON.parse(ev.data); } catch (_) { return; }
    // speaker タグで口パク/表情の宛先を選ぶ (2体目が無い/タグ不明なら主役へ)
    const idx = (m.speaker === 'b' && avatars[1]) ? 1 : 0;
    const target = avatars[idx];
    if (typeof m.mouth === 'number' && target) {
      target.mouthTarget = Math.max(0, Math.min(1, m.mouth));
      for (const av of avatars) { if (av && av !== target) av.mouthTarget = 0; }
    }
    if (typeof m.speaking === 'boolean') {
      speaking = m.speaking;
      if (!speaking) { for (const av of avatars) { if (av) av.mouthTarget = 0; } }
    }
    if (typeof m.emotion === 'string' && EMOTION_PRESETS.includes(m.emotion) && target) {
      target.emotion = m.emotion; target.emotionValue = 0.7;
    }
    if (SHOW_SUBTITLE && subtitleEl && typeof m.subtitle === 'string') {
      const s = m.subtitle.trim();
      subtitleEl.textContent = s;
      subtitleEl.style.display = s ? 'block' : 'none';
    }
  };
}
connectWs();
