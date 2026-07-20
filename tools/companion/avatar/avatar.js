// EndKnot AI 実況相棒アバター — three-vrm でVRMを1枚描き、companion.py の WebSocket から
// {mouth, speaking, subtitle, emotion} を受けて口パク/表情、まばたき・アイドルはローカルのタイマー駆動。
// 背景透過で描くので OBS ブラウザソースにそのまま重ねられる。CDN は使わず vendor/ のローカルESMだけで動く。

import * as THREE from 'three';
import { GLTFLoader } from './vendor/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from './vendor/three-vrm.module.js';

const params = new URLSearchParams(location.search);
const SHOW_SUBTITLE = !params.has('nosub');
const SHOW_STATUS = !params.has('nostatus');
const MODEL_URL = params.get('model') || './model.vrm';

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
});

// ---- VRM 読み込み ----
let currentVrm = null;
let modelBaseY = 0;

const loader = new GLTFLoader();
loader.register((parser) => new VRMLoaderPlugin(parser));

showNotice('モデルを読み込み中…');
loader.load(
  MODEL_URL,
  (gltf) => {
    const vrm = gltf.userData.vrm;
    if (!vrm) {
      showNotice('この .vrm から VRM データを取り出せませんでした。<br>VRM 0.x / 1.0 形式のモデルを置いてください。');
      return;
    }
    // 軽量化 (未使用頂点/ジョイント除去)
    try { VRMUtils.removeUnnecessaryVertices(gltf.scene); } catch (_) {}
    try { VRMUtils.combineSkeletons(gltf.scene); } catch (_) {}
    // VRM0 は -Z 向きなので 180° 回してカメラ (+Z) を向かせる。VRM1 は no-op。
    try { VRMUtils.rotateVRM0(vrm); } catch (_) {}

    // 影を出さない (透過背景に落ちると汚いだけ)
    vrm.scene.traverse((o) => { o.frustumCulled = false; });

    applyRestPose(vrm); // 初期は T ポーズなので腕を下ろした自然な立ち姿にする

    currentVrm = vrm;
    faceDriver = makeFaceDriver(vrm);
    window.__vrm = vrm; // デバッグ用フック (headless から表情/ポーズを検証できる)
    window.__setMouth = (v) => { mouthTarget = Math.max(0, Math.min(1, v)); }; // デバッグ用 (WS を介さず口パク検証)
    scene.add(vrm.scene);
    modelBaseY = vrm.scene.position.y;

    frameHeadShot(vrm);
    hideNotice();
    console.log('[avatar] VRM loaded');
  },
  undefined,
  (err) => {
    console.warn('[avatar] VRM load failed', err);
    showNotice(
      'アバターのモデルがまだありません。<br>' +
      'このフォルダに <code>model.vrm</code> を置いてください。<br>' +
      '<small>(VRoid Studio 等で作った VRM をそのままリネームで OK)</small>'
    );
  }
);

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

// 頭〜上半身が画面に収まるようカメラを合わせる (バストアップ)。
// 頭頂 (髪・アホ毛込み) はバウンディングボックスから取り、余白付きで必ず縦画角に収める。
function frameHeadShot(vrm) {
  const headNode = vrm.humanoid?.getNormalizedBoneNode('head');
  if (!headNode) return;
  vrm.scene.updateMatrixWorld(true);
  const headPos = new THREE.Vector3();
  headNode.getWorldPosition(headPos);
  const topY = new THREE.Box3().setFromObject(vrm.scene).max.y;
  const targetY = headPos.y - 0.18;      // 顔のやや下を中心に
  const headroom = 0.05;
  const halfFov = THREE.MathUtils.degToRad(camera.fov / 2);
  const dist = Math.max(0.8, (topY + headroom - targetY) / Math.tan(halfFov));
  camera.position.set(0, targetY, dist);
  camera.lookAt(0, targetY, 0);
}

// ---- 表情ドライバ ----
// 正常な VRM は expressionManager で動かすが、表情定義に bind が 1 本も入っていないモデル
// (東北ずん子 VRM 1.0 は全 expression が bind 0 本) では setValue しても何も起きない。
// その場合はメッシュの morph target (MMD 式の「あ」「目閉じ」等) を直接駆動する。
let faceDriver = null;

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

// ---- 表情・アニメーション状態 ----
let mouthTarget = 0;    // WS から来る口の開き (0..1)
let mouthCurrent = 0;
let speaking = false;
let emotion = 'neutral';
let emotionValue = 0;
const emotionCurrent = {}; // 感情プリセットごとの現在値 (両ドライバ共通の自前状態)

// まばたき
let blinkTimer = 1.5 + Math.random() * 3;
let blinkPhase = -1;    // -1 = 閉じてない, 0..1 = 進行中

const EMOTION_PRESETS = ['happy', 'angry', 'sad', 'relaxed', 'surprised'];

function applyExpressions(vrm, delta) {
  if (!faceDriver) return;

  // 口: WS の mouth は「喋りのスパイク」なので、速く開いてゆっくり閉じる非対称追従にする。
  // (対称に均すとスパイクが消えて口が動いて見えない — これが初回の「口が動かない」原因だった)
  const openRate = delta * 35;   // 開くのは速く
  const closeRate = delta * 9;   // 閉じるのはゆっくり (連続発話中は開きっぱなしに近くなる)
  const rate = mouthTarget > mouthCurrent ? openRate : closeRate;
  mouthCurrent += (mouthTarget - mouthCurrent) * Math.min(1, rate);
  faceDriver.set('aa', mouthCurrent);

  // まばたき
  blinkTimer -= delta;
  if (blinkPhase < 0 && blinkTimer <= 0) blinkPhase = 0;
  if (blinkPhase >= 0) {
    blinkPhase += delta / 0.12; // 約0.12秒で開閉
    // 0→1→0 の三角波
    const b = blinkPhase < 0.5 ? blinkPhase * 2 : (1 - blinkPhase) * 2;
    faceDriver.set('blink', Math.max(0, Math.min(1, b)));
    if (blinkPhase >= 1) { blinkPhase = -1; blinkTimer = 1.5 + Math.random() * 4; }
  }

  // 感情表情 (WS の emotion。無指定なら neutral へ減衰)
  for (const p of EMOTION_PRESETS) {
    const want = (p === emotion) ? emotionValue : 0;
    const cur = (emotionCurrent[p] || 0) + (want - (emotionCurrent[p] || 0)) * Math.min(1, delta * 6);
    emotionCurrent[p] = cur;
    faceDriver.set(p, cur);
  }
  // 感情は数秒で自然に薄れる。しゃべり続けていても表情ロックしないよう発話中も減衰し
  // (長文でも約3秒で戻り始める)、無音になったら速めにスッと戻す。
  emotionValue = Math.max(0, emotionValue - delta * (speaking ? 0.22 : 0.5));
}

// アイドル: ごく浅い呼吸 (上下) と左右の揺れで棒立ち感を消す。
function applyIdle(vrm, t) {
  const breathe = Math.sin(t * 1.6) * 0.004;
  vrm.scene.position.y = modelBaseY + breathe;
  vrm.scene.rotation.y = Math.sin(t * 0.5) * 0.03;
}

// ---- 描画ループ ----
const clock = new THREE.Clock();
function animate() {
  requestAnimationFrame(animate);
  const delta = clock.getDelta();
  const t = clock.elapsedTime;
  if (currentVrm) {
    applyExpressions(currentVrm, delta);
    applyIdle(currentVrm, t);
    currentVrm.update(delta); // 表情/ボーンを実際のメッシュへ反映
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
    mouthTarget = 0; speaking = false;
    setTimeout(connectWs, 2000); // 自動再接続
  };
  ws.onerror = () => { try { ws.close(); } catch (_) {} };
  ws.onmessage = (ev) => {
    let m;
    try { m = JSON.parse(ev.data); } catch (_) { return; }
    if (typeof m.mouth === 'number') mouthTarget = Math.max(0, Math.min(1, m.mouth));
    if (typeof m.speaking === 'boolean') {
      speaking = m.speaking;
      if (!speaking) mouthTarget = 0;
    }
    if (typeof m.emotion === 'string' && EMOTION_PRESETS.includes(m.emotion)) {
      emotion = m.emotion; emotionValue = 0.7;
    }
    if (SHOW_SUBTITLE && subtitleEl && typeof m.subtitle === 'string') {
      const s = m.subtitle.trim();
      subtitleEl.textContent = s;
      subtitleEl.style.display = s ? 'block' : 'none';
    }
  };
}
connectWs();
