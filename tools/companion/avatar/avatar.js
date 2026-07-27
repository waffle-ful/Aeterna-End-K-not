// EndKnot AI 実況相棒アバター — three-vrm でVRMを描き、companion.py の WebSocket から
// {mouth, speaking, subtitle, emotion, gesture, speaker} を受けて口パク/表情/身振り、
// まばたき・呼吸・常時の揺れはローカルのタイマー駆動。
// 体の動きは3層 (常時モーション + 感情の姿勢 + 単発の身振りクリップ) を毎フレーム
// 「休めポーズ + 各層の差分」の絶対値としてボーンに書く (下の buildPose 参照)。
// model2.vrm を置くと2体並び (--duo の掛け合い実況用) になり、speaker タグで口パク/表情を振り分ける。
// ?as=a|b を付けると「1体だけ・その話者専用」モードになり、OBS で立ち絵を2ソースに分けられる。
// 背景透過で描くので OBS ブラウザソースにそのまま重ねられる。CDN は使わず vendor/ のローカルESMだけで動く。

import * as THREE from 'three';
import { GLTFLoader } from './vendor/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from './vendor/three-vrm.module.js';

const params = new URLSearchParams(location.search);
// ?as=a|b : このページは1体だけを描き、その話者タグの発話にしか反応しない (立ち絵を OBS の
// 別ソースに分けるとき用)。a = 主役 (model.vrm)、b = 相方 (model2.vrm)。
const MY_TAG = ['a', 'b'].includes((params.get('as') || '').toLowerCase())
  ? params.get('as').toLowerCase() : null;
// ?face=left|right : 立ち絵を内側 (相方の方) へ少し向ける。左下に置く子は right、右下は left。
const FACE_ROT = { left: -0.10, right: 0.10 }[(params.get('face') || '').toLowerCase()] || 0;
// 2ソースに分けると内蔵字幕が二重に出るので、?as 指定時は既定オフ (?sub で復活)。
const SHOW_SUBTITLE = MY_TAG ? params.has('sub') : !params.has('nosub');
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
  const av = {
    vrm,
    driver: makeFaceDriver(vrm),
    mouthTarget: 0,
    mouthCurrent: 0,
    emotion: 'neutral',
    emotionValue: 0,
    emotionCurrent: {},
    blinkTimer: 1.5 + Math.random() * 3,
    blinkPhase: -1,          // -1 = 閉じてない, 0..1 = 進行中
    blinkDur: 0.12,          // 1回の開閉にかける秒数 (ゆっくり版は 0.30)
    blinkRepeat: 0,          // 続けて何回まばたきするか
    baseX: 0,
    baseRotY: 0,
    idlePhase: Math.random() * 6, // 2体が同期して揺れると人形っぽいので位相をずらす
    talking: false,          // この子が今しゃべっているか (--duo の話者振り分け結果)
    speakLevel: 0,           // talking をなまらせた 0..1 (体の動きの強さに使う)
    gesture: null,           // 再生中の身振りクリップ名 (GESTURES のキー)
    gestureT: 0,             // クリップ再生位置 (秒)
    idleTimer: IDLE_MIN_SEC + Math.random() * (IDLE_MAX_SEC - IDLE_MIN_SEC), // 次のアイドル仕草まで
    lastIdle: null,          // 同じアイドル仕草が連続しないように覚えておく
    bodyX: 0,                // 身振りによる体全体の左右 (重心移動)
    bodyY: 0,                // 身振りによる体全体の上下 (ジャンプ/沈み込み)
    bodyZ: 0,                // 同・前後 (のけぞり)
    bones: {},               // 正規化ボーンのキャッシュ (毎フレーム引くのを避ける)
    pose: {},                // 毎フレーム組み立てる姿勢 (bone -> [x,y,z])
  };
  for (const b of POSE_BONES) {
    av.pose[b] = [0, 0, 0];
    av.bones[b] = vrm.humanoid ? vrm.humanoid.getNormalizedBoneNode(b) : null;
  }
  return av;
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
        // デバッグ用: そのモデルで拡張表情が出せるか / 何に落ちるかを確認する
        window.__resolveEmotion = (av, name) => resolveEmotion(av, name);
        // デバッグ用: 身振りは必ず状態経由で駆動する (ボーンを直接叩いても animate が毎フレーム上書きする)
        window.__playGesture = (name, slot = 0) => {
          const av = avatars[slot];
          if (!av || !GESTURES[name]) return false;
          av.gesture = name; av.gestureT = 0;
          return true;
        };
      }
      console.log(`[avatar] VRM loaded (slot ${slot})`);
    },
    undefined,
    (err) => {
      if (required) {
        console.warn('[avatar] VRM load failed', err);
        showNotice(
          'アバターのモデルがまだありません。<br>' +
          `このフォルダに <code>${url.split('/').pop()}</code> を置いてください。<br>` +
          '<small>(VRoid Studio 等で作った VRM をそのままリネームで OK)</small>'
        );
      } else {
        console.log('[avatar] model2.vrm なし — 1体モードで表示します');
      }
    }
  );
}

// ?as=b のときは相方を slot 0 に載せる (1体モードの中央フレーミングをそのまま使うため)。
// ?as 指定時は model= / model2= のどちらでもそのページのモデルを指せる (片方だけ書けば済む)。
const SOLO_URL = MY_TAG === 'b'
  ? (params.get('model2') || params.get('model') || './model2.vrm')
  : MODEL_URL;
loadModel(MY_TAG ? SOLO_URL : MODEL_URL, 0, true);
if (!MY_TAG) loadModel(MODEL2_URL, 1, false);

// ---- 姿勢パイプライン ----
// 体の動きを足す層が増えたので、ボーンの回転は「毎フレーム絶対値で書き直す」方式にしてある。
//   休めポーズ (REST_POSE) + 身振りクリップの差分 + 感情の姿勢差分 + 常時モーション
// 差分を足し込む方式にすると、身振りが終わったときに腕が VRM 既定の T ポーズへ戻ってしまう
// (休めポーズは読み込み時の1回きりの書き込みだったため)。絶対値で書けばその事故が起きない。
const POSE_BONES = [
  'spine', 'chest', 'upperChest', 'neck', 'head',
  'leftShoulder', 'rightShoulder',
  'leftUpperArm', 'rightUpperArm', 'leftLowerArm', 'rightLowerArm',
  'leftHand', 'rightHand',
];

// VRM は初期状態が T ポーズ (両腕が真横=はりつけ状態) なので、上腕を約63°下ろして自然な立ち姿にする。
// (左腕は -Z, 右腕は +Z で下がる。少し内側に肘を曲げると自然。) 書いていないボーンは 0。
const REST_POSE = {
  leftUpperArm: [0, 0, -1.10],
  rightUpperArm: [0, 0, 1.10],
  leftLowerArm: [0, 0, -0.15],
  rightLowerArm: [0, 0, 0.15],
};

// 読み込み直後の1フレームとカメラのフレーミング計算のために、休めポーズを当てておく。
// (以降は毎フレーム buildPose が上書きする)
function applyRestPose(vrm) {
  const h = vrm.humanoid;
  if (!h) return;
  for (const bone in REST_POSE) {
    const n = h.getNormalizedBoneNode(bone);
    if (n) n.rotation.set(REST_POSE[bone][0], REST_POSE[bone][1], REST_POSE[bone][2]);
  }
}

function smoothstep(x) {
  x = Math.max(0, Math.min(1, x));
  return x * x * (3 - 2 * x);
}

function resetPose(av) {
  for (const b of POSE_BONES) {
    const p = av.pose[b];
    const r = REST_POSE[b];
    p[0] = r ? r[0] : 0;
    p[1] = r ? r[1] : 0;
    p[2] = r ? r[2] : 0;
  }
}

function addPose(av, bone, x, y, z) {
  const p = av.pose[bone];
  if (!p) return;
  p[0] += x; p[1] += y; p[2] += z;
}

function writePose(av) {
  for (const b of POSE_BONES) {
    const n = av.bones[b];
    if (!n) continue; // モデルに無いボーン (upperChest / shoulder は省略されることがある)
    const p = av.pose[b];
    n.rotation.set(p[0], p[1], p[2]);
  }
}

// ---- 第3層: 単発の身振りクリップ ----
// pose(u) は休めポーズからの「差分」を返す (u = 0..1 の再生位置)。フェードイン/アウトの重みは
// 呼び出し側が掛けるので、クリップ側は決めポーズと揺らぎだけ書けばよい。
// body(u) は体全体のオフセット (y=上下 / z=前後)。
const GESTURES = {
  // 手を振る (参加者の歓迎)
  wave: {
    dur: 1.9, fadeIn: 0.15, fadeOut: 0.25,
    pose: (u) => {
      const flap = Math.sin(u * Math.PI * 8) * 0.30; // 4往復ぶんの手振り
      return {
        rightUpperArm: [0, 0.25, -1.55],
        rightLowerArm: [0, 0.15, -1.05 + flap],
        rightHand: [0, 0, flap * 0.6],
        head: [-0.05, -0.06, 0],
      };
    },
  },
  // 指差し (視聴者コマンドの発動者を指す)
  point: {
    dur: 1.5, fadeIn: 0.12, fadeOut: 0.3,
    pose: (u) => {
      const jab = Math.max(0, Math.sin(u * Math.PI * 2)) * 0.18; // ぐっと突き出す
      return {
        rightUpperArm: [0, 1.20 + jab, -0.45],
        rightLowerArm: [0, 0.25, -0.10],
        head: [0.03, -0.10, 0],
        chest: [0, -0.06, 0],
      };
    },
  },
  // バンザイ (試合開始・勝利)
  cheer: {
    dur: 1.7, fadeIn: 0.15, fadeOut: 0.28,
    pose: (u) => {
      const b = Math.sin(u * Math.PI * 3) * 0.12;
      return {
        leftUpperArm: [0, 0, 1.75 + b],
        rightUpperArm: [0, 0, -1.75 - b],
        leftLowerArm: [0, 0, 0.30],
        rightLowerArm: [0, 0, -0.30],
        head: [-0.12, 0, 0],
        chest: [-0.05, 0, 0],
      };
    },
    body: (u) => ({ y: Math.abs(Math.sin(u * Math.PI * 3)) * 0.03 }),
  },
  // 腕組み (サボタージュにむくれる)
  arms_cross: {
    dur: 2.6, fadeIn: 0.14, fadeOut: 0.22,
    pose: () => ({
      // 肘を前へ出しつつ前腕を強く畳んで胸の前で交差させる。畳みが浅いと
      // 「前で手を組んだだけ」に見えるので、y の折りをしっかり入れる。
      leftUpperArm: [0, -0.34, -0.16],
      rightUpperArm: [0, 0.34, 0.16],
      leftLowerArm: [0, -1.72, 0.30],
      rightLowerArm: [0, 1.72, -0.30],
      head: [0.06, 0, 0],
      chest: [0.03, 0, 0],
    }),
  },
  // うなだれる (退出・勝者なし)
  slump: {
    dur: 2.3, fadeIn: 0.2, fadeOut: 0.3,
    pose: () => ({
      head: [0.42, 0, 0],
      neck: [0.16, 0, 0],
      chest: [0.10, 0, 0],
      spine: [0.10, 0, 0],
      leftShoulder: [0, 0, -0.10],
      rightShoulder: [0, 0, 0.10],
      leftUpperArm: [0, -0.12, -0.12],
      rightUpperArm: [0, 0.12, 0.12],
    }),
    body: () => ({ y: -0.02 }),
  },
  // のけぞって両手を上げる (追放・会議開始)
  startle: {
    dur: 1.3, fadeIn: 0.08, fadeOut: 0.35,
    pose: (u) => {
      const shock = Math.exp(-u * 5); // 出だしだけビクッと大きく
      return {
        head: [-0.28 - shock * 0.10, 0, 0],
        neck: [-0.10, 0, 0],
        spine: [-0.10, 0, 0],
        leftUpperArm: [0, -0.20, 1.05],
        rightUpperArm: [0, 0.20, -1.05],
        leftLowerArm: [0, -0.30, 1.35],
        rightLowerArm: [0, 0.30, -1.35],
      };
    },
    body: (u) => ({ z: -0.05 * Math.exp(-u * 4) }),
  },
  // 相槌 (場繋ぎトーク用の小さい仕草)
  nod: {
    dur: 1.0, fadeIn: 0.12, fadeOut: 0.25,
    pose: (u) => {
      const n = Math.sin(u * Math.PI * 4);
      return { head: [n * 0.20, 0, 0], neck: [n * 0.08, 0, 0] };
    },
  },
  shake: {
    dur: 1.0, fadeIn: 0.12, fadeOut: 0.25,
    pose: (u) => {
      const n = Math.sin(u * Math.PI * 5);
      return { head: [0, n * 0.26, 0], neck: [0, n * 0.10, 0] };
    },
  },
  // 小首をかしげる
  think: {
    dur: 1.8, fadeIn: 0.2, fadeOut: 0.3,
    pose: (u) => ({
      head: [-0.08, 0.20, 0.38 + Math.sin(u * Math.PI * 2) * 0.06],
      neck: [0, 0.08, 0.13],
      chest: [0, 0.04, 0.04],
      leftUpperArm: [0, -0.05, -0.05],
    }),
  },

  // --- ここから下は黙っている間に勝手に出る仕草 (IDLE_GESTURES) ---
  // 決め所のクリップより小さく・ゆっくりにして、喋っていない時間を埋めるのが目的。
  // きょろきょろ見回す
  look_around: {
    dur: 3.0, fadeIn: 0.18, fadeOut: 0.25,
    pose: (u) => {
      const sweep = Math.sin(u * Math.PI * 2); // 片側 → 反対側 → 戻る
      return {
        head: [0.02, sweep * 0.55, sweep * -0.10],
        neck: [0, sweep * 0.20, 0],
        chest: [0, sweep * 0.12, 0],
        spine: [0, sweep * 0.05, 0],
      };
    },
  },
  // 伸びをする
  stretch: {
    dur: 3.2, fadeIn: 0.28, fadeOut: 0.3,
    pose: (u) => {
      const up = Math.sin(u * Math.PI); // ゆっくり上げてゆっくり下ろす
      return {
        leftUpperArm: [0, 0, 1.45 * up],
        rightUpperArm: [0, 0, -1.45 * up],
        leftLowerArm: [0, -0.25 * up, 0.35 * up],
        rightLowerArm: [0, 0.25 * up, -0.35 * up],
        head: [-0.16 * up, 0, 0],
        chest: [-0.08 * up, 0, 0],
        spine: [-0.05 * up, 0, 0],
      };
    },
    body: (u) => ({ y: Math.sin(u * Math.PI) * 0.025 }),
  },
  // 体重を片足へ移す (立ち姿の重心移動)
  weight_shift: {
    dur: 3.4, fadeIn: 0.3, fadeOut: 0.3,
    pose: (u) => {
      const s = Math.sin(u * Math.PI);
      return {
        spine: [0, 0, s * 0.14],
        chest: [0, 0, s * 0.08],
        head: [0, s * 0.10, s * -0.17], // 頭は逆に傾けてバランスを取る
        leftUpperArm: [0, 0, s * -0.13],
        rightUpperArm: [0, 0, s * 0.13],
      };
    },
    body: (u) => ({ x: Math.sin(u * Math.PI) * 0.065 }),
  },
  // 髪(頭のあたり)に手をやる
  hair_touch: {
    dur: 2.6, fadeIn: 0.25, fadeOut: 0.3,
    pose: (u) => {
      const settle = Math.sin(u * Math.PI * 3) * 0.05;
      return {
        // 肘は下げたまま前腕だけ深く畳んで、手が頭の横に来るようにする
        // (上腕まで上げると「手を挙げている」だけに見えて wave と区別が付かない)
        rightUpperArm: [0, 0.45, -0.55],
        rightLowerArm: [0, 0.55, -1.85 + settle],
        rightHand: [0, 0, settle],
        head: [0.05, -0.06, -0.10],
        neck: [0, -0.03, -0.04],
      };
    },
  },
};

// 黙っている間に勝手に出る仕草。決め所のクリップ (wave/cheer 等) は混ぜない。
// nod は入れない — 1 秒の小さなうなずきは単体で出ても「動いた」と分からず、
// 出番だけ食って他の仕草の頻度を下げてしまう (相槌としては FILLER_GESTURES 側で使う)。
const IDLE_GESTURES = ['look_around', 'weight_shift', 'think', 'stretch', 'hair_touch'];
// 間隔。クリップ自体が 1.8〜3.4 秒あるので、これより短くすると常時動きっぱなしになる。
const IDLE_MIN_SEC = 4;
const IDLE_MAX_SEC = 10;

// 無言が続いたら、たまに小さい仕草を出す。
// ⚠️ 発話イベント由来の身振り (pending_gesture) は発話開始時に上書きで割り込む。決め所の身振りを
// 優先させたいので、その割り込みは意図どおり (アイドル仕草の途中で切り替わる)。
function updateIdleGesture(av, delta) {
  if (av.speakLevel >= 0.05 || av.gesture) {
    // 喋っている間 / 何か再生中は溜めない。喋り終わった直後に即発火しないよう間を空ける。
    av.idleTimer = Math.max(av.idleTimer, IDLE_MIN_SEC * 0.5);
    return;
  }
  av.idleTimer -= delta;
  if (av.idleTimer > 0) return;
  const pool = IDLE_GESTURES.filter((g) => g !== av.lastIdle);
  const pick = pool[Math.floor(Math.random() * pool.length)];
  av.lastIdle = pick;
  av.gesture = pick;
  av.gestureT = 0;
  av.idleTimer = IDLE_MIN_SEC + Math.random() * (IDLE_MAX_SEC - IDLE_MIN_SEC);
}

function addGesture(av, delta) {
  av.bodyX = 0;
  av.bodyY = 0;
  av.bodyZ = 0;
  const clip = GESTURES[av.gesture];
  if (!clip) { av.gesture = null; return; }
  av.gestureT += delta;
  const u = av.gestureT / clip.dur;
  if (u >= 1) { av.gesture = null; return; }
  // 出入りをなめらかにする。これが無いと決めポーズへ瞬間移動して人形が飛ぶ
  const w = smoothstep(Math.min(u / clip.fadeIn, (1 - u) / clip.fadeOut, 1));
  const p = clip.pose(u);
  for (const bone in p) {
    const d = p[bone];
    addPose(av, bone, d[0] * w, d[1] * w, d[2] * w);
  }
  if (clip.body) {
    const b = clip.body(u);
    av.bodyX = (b.x || 0) * w;
    av.bodyY = (b.y || 0) * w;
    av.bodyZ = (b.z || 0) * w;
  }
}

// ---- 第2層: 感情ごとの持続的な姿勢 ----
// 表情モーフと同じ emotionCurrent の値に比例させるので、表情が薄れると姿勢も一緒に戻る。
const EMOTION_POSTURE = {
  happy: { head: [-0.07, 0, 0], chest: [-0.06, 0, 0], leftUpperArm: [0, 0, 0.10], rightUpperArm: [0, 0, -0.10] },
  angry: { head: [0.09, 0, 0], chest: [0.07, 0, 0], leftUpperArm: [0, -0.08, -0.10], rightUpperArm: [0, 0.08, 0.10] },
  sad: { head: [0.26, 0, 0.05], neck: [0.08, 0, 0], chest: [0.09, 0, 0], leftShoulder: [0, 0, -0.08], rightShoulder: [0, 0, 0.08] },
  relaxed: { head: [0, 0, 0.10], chest: [-0.02, 0, 0] },
  surprised: { head: [-0.14, 0, 0], neck: [-0.05, 0, 0], chest: [-0.05, 0, 0] },
  // 拡張ぶん
  excited: { head: [-0.10, 0, 0], chest: [-0.09, 0, 0], leftUpperArm: [0, 0, 0.16], rightUpperArm: [0, 0, -0.16] },
  laugh: { head: [-0.12, 0, 0.05], neck: [-0.04, 0, 0], chest: [-0.07, 0, 0] },
  shy: { head: [0.16, 0.12, 0.06], neck: [0.05, 0.04, 0], leftShoulder: [0, 0, -0.05], rightShoulder: [0, 0, 0.05] },
  pale: { head: [-0.06, 0, 0], neck: [-0.03, 0, 0], spine: [-0.07, 0, 0], leftUpperArm: [0, -0.06, -0.08], rightUpperArm: [0, 0.06, 0.08] },
  doubt: { head: [0.04, 0.10, 0.14], neck: [0, 0.04, 0.05], chest: [0.02, 0, 0] },
  wink: { head: [-0.04, 0, 0.13], chest: [-0.03, 0, 0] },
};

function addEmotionPosture(av) {
  for (const p of EMOTION_PRESETS) {
    const v = av.emotionCurrent[p] || 0;
    if (v < 0.01) continue;
    const posture = EMOTION_POSTURE[p];
    if (!posture) continue;
    for (const bone in posture) {
      const d = posture[bone];
      addPose(av, bone, d[0] * v, d[1] * v, d[2] * v);
    }
  }
}

// ---- 第1層: 常時モーション ----
// 決め所の身振りは1分に1回程度しか出ないので、「生きている」感を支えているのは実はこの層。
// 低周波の正弦を2本重ねて周期が読めないようにし、しゃべっている間は振幅を上げる。
function addAmbientMotion(av, t) {
  const tp = t + av.idlePhase;
  const gain = 0.6 + av.speakLevel * 1.4; // 黙っている間は控えめ、喋ると大きく動く
  const wx = Math.sin(tp * 0.47 + 0.6) * 0.030 + Math.sin(tp * 0.23) * 0.018;
  const wy = Math.sin(tp * 0.53) * 0.055 + Math.sin(tp * 0.31 + 1.7) * 0.030;
  const wz = Math.sin(tp * 0.37 + 2.1) * 0.028;
  // 口の開きに合わせて頭が軽く動く (声と体が連動して見えるかどうかの主成分)
  const beat = av.mouthCurrent * 0.06 * av.speakLevel;
  addPose(av, 'head', wx * gain + beat, wy * gain, wz * gain);
  addPose(av, 'neck', wx * gain * 0.5, wy * gain * 0.5, wz * gain * 0.4);
  // 呼吸
  const breathe = Math.sin(tp * 1.6) * 0.012;
  addPose(av, 'chest', breathe, 0, 0);
  addPose(av, 'spine', breathe * 0.6, Math.sin(tp * 0.41) * 0.02 * gain, 0);
  // 腕もわずかに開閉させる (完全に静止した腕は人形に見える)
  const arm = Math.sin(tp * 0.9 + 0.4) * 0.035 * gain;
  addPose(av, 'leftUpperArm', 0, 0, -arm);
  addPose(av, 'rightUpperArm', 0, 0, arm);
}

// 1フレーム分の姿勢を組み立ててボーンへ書く。
function buildPose(av, t, delta) {
  av.speakLevel += ((av.talking ? 1 : 0) - av.speakLevel) * Math.min(1, delta * 4);
  updateIdleGesture(av, delta); // 無言が続いていれば勝手に仕草を仕込む (addGesture より先)
  resetPose(av);
  addGesture(av, delta); // 体オフセット (bodyX/Y/Z) も決まるので applyIdle より先
  addEmotionPosture(av);
  addAmbientMotion(av, t);
  writePose(av);
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
    av.baseX = x;                     // 重心移動 (bodyX) はここからの相対で足す
    av.vrm.scene.position.x = x;
    av.baseY = 0;
    // 少しだけ内側 (相方の方) を向かせて「並んでいる」感を出す
    av.baseRotY = duo ? (slot === 0 ? -0.10 : 0.10) : FACE_ROT;
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
// VRM の expression は「名前はあるが bind が 1 本も入っていない」ことがある (東北ずん子 VRM 1.0 は
// 全 18 expression が bind 0 本)。この場合 setValue しても値が動くだけでメッシュには何も起きない。
// なので **名前の有無ではなく bind の有無** で振り分け、bind が無い表情はメッシュの morph target を
// 直接駆動する。標準5表情しか持たない VRoid 系モデルでも、拡張表情はこの morph 経路で出せる。
//
// 1つの名前に複数のモーフ候補を並べてあるのは、MMD 式 (東北ずん子) と VRoid 式 (Fcl_*) の両方の
// 命名を1つの表から引くため。存在するものだけが書かれる。
const MORPH_MAP = {
  aa: ['あ', 'Fcl_MTH_A'],
  blink: ['目閉じ', 'Fcl_EYE_Close'],
  // --- 標準5表情 ---
  happy: ['ニコ目', '頬染め', 'Fcl_EYE_Joy', 'Fcl_MTH_Joy', 'Fcl_BRW_Joy'],
  angry: ['怒り眉', 'Fcl_BRW_Angry', 'Fcl_EYE_Angry', 'Fcl_MTH_Angry'],
  sad: ['困り眉', 'Fcl_BRW_Sorrow', 'Fcl_EYE_Sorrow', 'Fcl_MTH_Sorrow'],
  relaxed: ['ジト目', 'Fcl_ALL_Fun'],
  surprised: ['瞳小さく', 'Fcl_EYE_Surprised', 'Fcl_BRW_Surprised', 'Fcl_MTH_Surprised'],
  // 〇〇目 は目のテクスチャ差し替え用モーフで白目サークル化するので使わない (実機で事故済み)
  // --- 拡張表情 ---
  excited: ['星目', 'ニコ目', 'Fcl_EYE_Joy', 'Fcl_BRW_Joy', 'Fcl_MTH_Fun'],   // キラキラ・大盛り上がり
  laugh: ['＞＜', '頬染め', 'Fcl_EYE_Joy', 'Fcl_MTH_Joy', 'Fcl_BRW_Fun'],      // 大笑い
  shy: ['頬染め', '困り眉', 'Fcl_BRW_Sorrow', 'Fcl_ALL_Fun', 'Fcl_MTH_Small'], // 照れ
  pale: ['青ざめ', '瞳ハイライト消し', 'Fcl_EYE_Highlight_Hide', 'Fcl_BRW_Sorrow'], // 青ざめ・ドン引き
  doubt: ['ジト目', '怒り眉', 'Fcl_EYE_Sorrow', 'Fcl_MTH_Small'],              // 疑い・呆れ
  wink: ['ウインク右', 'Fcl_MTH_Joy'],                                         // お茶目
};

// bind 済みの標準 expression に相乗りしたい拡張表情の別名。
// ⚠️ モデルが bind しているモーフを直接叩いても、expressionManager が毎フレーム自分の値
// (たいてい 0) で上書きするので効かない。VRoid の片目つむりは Fcl_EYE_Close_R が blinkRight に
// bind されているのがまさにこれで、モーフ直叩きだと口だけ笑って目が閉じなかった。
const EXPR_ALIAS = {
  wink: ['blinkRight'],
};

function makeFaceDriver(vrm) {
  const em = vrm.expressionManager;
  // ⚠️ bind が入っている表情だけを expressionManager に任せる。名前の有無で振り分けると
  // ずん子の空 bind な happy/sad/... を拾ってしまい、標準表情が丸ごと無反応に戻る。
  const bound = new Set();
  if (em) {
    for (const e of em.expressions) {
      if ((e.binds?.length || 0) > 0) bound.add(e.expressionName ?? e.name);
    }
  }
  const meshes = [];
  vrm.scene.traverse((o) => { if (o.morphTargetDictionary && o.morphTargetInfluences) meshes.push(o); });
  const morphExists = (t) => meshes.some((m) => m.morphTargetDictionary[t] !== undefined);

  // 複数の表情が同じモーフを共有する (頬染めが laugh と shy 等)。単純に順番へ書くと、
  // 減衰中の表情が 0 を書いて後勝ちで打ち消してしまうので、1フレーム分を溜めて最大値で確定する。
  const acc = new Map();
  console.log(`[avatar] face driver: bound=${bound.size} / morph meshes=${meshes.length}`);
  return {
    boundNames: bound,
    set(name, v) {
      if (bound.has(name)) {
        const k = 'e:' + name;
        acc.set(k, Math.max(acc.get(k) || 0, v));
        return;
      }
      // bind 済みの標準 expression で代用できるぶんは、そちらにも流す
      for (const alias of (EXPR_ALIAS[name] || [])) {
        if (!bound.has(alias)) continue;
        const k = 'e:' + alias;
        acc.set(k, Math.max(acc.get(k) || 0, v));
      }
      for (const t of (MORPH_MAP[name] || [])) {
        if (!morphExists(t)) continue;
        const k = 'm:' + t;
        acc.set(k, Math.max(acc.get(k) || 0, v));
      }
    },
    // 溜めた値を実際に書き、次フレームのために 0 へ戻す (キーは残すので、使われなくなった
    // モーフも 0 が書かれ続けて消え残りが出ない)
    commit() {
      for (const [k, v] of acc) {
        if (k[0] === 'e') em.setValue(k.slice(2), v);
        else {
          const t = k.slice(2);
          for (const mesh of meshes) {
            const i = mesh.morphTargetDictionary[t];
            if (i !== undefined) mesh.morphTargetInfluences[i] = v;
          }
        }
        acc.set(k, 0);
      }
    },
    // このモデルでその表情が実際に出せるか (出せないものは近い標準表情へ落とす)
    has(name) {
      return bound.has(name)
        || (EXPR_ALIAS[name] || []).some((a) => bound.has(a))
        || (MORPH_MAP[name] || []).some(morphExists);
    },
  };
}

// ---- 表情・アニメーション状態 (発話状態はグローバル、表情/口は avatar 個別) ----
let speaking = false;

// 標準5表情 + 拡張6表情。拡張ぶんはモデルが素材を持っていないことがあるので、
// 出せない場合は受信時に一番近い標準表情へ落とす (無音で何も起きないのを防ぐ)。
const EMOTION_PRESETS = [
  'happy', 'angry', 'sad', 'relaxed', 'surprised',
  'excited', 'laugh', 'shy', 'pale', 'doubt', 'wink',
];
const EMOTION_FALLBACK = {
  excited: 'happy', laugh: 'happy', shy: 'relaxed',
  pale: 'sad', doubt: 'relaxed', wink: 'happy',
};

// そのモデルで実際に出せる表情に解決する (出せなければ近い標準表情へ)
function resolveEmotion(av, name) {
  if (av.driver.has(name)) return name;
  const alt = EMOTION_FALLBACK[name];
  return (alt && av.driver.has(alt)) ? alt : 'happy';
}

function applyExpressions(av, delta) {
  // 口: WS の mouth は「喋りのスパイク」なので、速く開いてゆっくり閉じる非対称追従にする。
  // (対称に均すとスパイクが消えて口が動いて見えない — これが初回の「口が動かない」原因だった)
  const openRate = delta * 35;   // 開くのは速く
  const closeRate = delta * 9;   // 閉じるのはゆっくり (連続発話中は開きっぱなしに近くなる)
  const rate = av.mouthTarget > av.mouthCurrent ? openRate : closeRate;
  av.mouthCurrent += (av.mouthTarget - av.mouthCurrent) * Math.min(1, rate);
  av.driver.set('aa', av.mouthCurrent);

  // まばたき。毎回まったく同じ速さ・同じ間隔だと機械的に見えるので、たまに
  // 「ゆっくり閉じる」「二回続けて閉じる」を混ぜる。
  av.blinkTimer -= delta;
  if (av.blinkPhase < 0 && av.blinkTimer <= 0) {
    av.blinkPhase = 0;
    if (av.blinkRepeat > 0) {
      av.blinkDur = 0.10;            // 連続まばたきの2回目は速く
    } else {
      const r = Math.random();
      if (r < 0.15) { av.blinkDur = 0.30; }                      // ゆっくり (眠そう・しみじみ)
      else if (r < 0.32) { av.blinkDur = 0.10; av.blinkRepeat = 1; } // 二回続けて
      else { av.blinkDur = 0.12; }                               // 通常
    }
  }
  if (av.blinkPhase >= 0) {
    av.blinkPhase += delta / av.blinkDur;
    // 0→1→0 の三角波
    const b = av.blinkPhase < 0.5 ? av.blinkPhase * 2 : (1 - av.blinkPhase) * 2;
    av.driver.set('blink', Math.max(0, Math.min(1, b)));
    if (av.blinkPhase >= 1) {
      av.blinkPhase = -1;
      if (av.blinkRepeat > 0) { av.blinkRepeat--; av.blinkTimer = 0.09; }
      else av.blinkTimer = 1.5 + Math.random() * 4;
    }
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

  // このフレームで要求された表情をまとめて確定する (共有モーフの後勝ち打ち消しを避けるため)
  av.driver.commit();
}

// 体全体の移動ぶん: ごく浅い呼吸 (上下) と左右の揺れ + 身振りのオフセット (跳ねる/のけぞる)。
function applyIdle(av, t) {
  const tp = t + av.idlePhase;
  const gain = 0.6 + av.speakLevel * 1.2;
  const breathe = Math.sin(tp * 1.6) * 0.005;
  av.vrm.scene.position.x = av.baseX + av.bodyX;
  av.vrm.scene.position.y = av.baseY + breathe + av.bodyY;
  av.vrm.scene.position.z = av.bodyZ;
  av.vrm.scene.rotation.y = av.baseRotY + Math.sin(tp * 0.5) * 0.045 * gain;
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
    buildPose(av, t, delta);
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
    for (const av of avatars) { if (av) { av.mouthTarget = 0; av.talking = false; } }
    speaking = false;
    setTimeout(connectWs, 2000); // 自動再接続
  };
  ws.onerror = () => { try { ws.close(); } catch (_) {} };
  ws.onmessage = (ev) => {
    let m;
    try { m = JSON.parse(ev.data); } catch (_) { return; }
    // 1人モードや話者タグが塗られる前は speaker が null で届く。これは主役の発話なので 'a' 扱いにする。
    // (null のまま素通りさせると、?as=b のページが「相方以外の発話」まで自分の身振りで演じてしまう)
    const spk = typeof m.speaker === 'string' ? m.speaker : 'a';
    // ?as=a|b のページは相方の発話を丸ごと無視する。これが無いと、2ソースに分けたとき
    // avatars[1] が居ないぶん宛先が常に slot 0 へ落ちて、両方の立ち絵が相手の台詞で口パクする。
    if (MY_TAG && spk !== MY_TAG) {
      for (const av of avatars) { if (av) { av.mouthTarget = 0; av.talking = false; } }
      return;
    }
    // speaker タグで口パク/表情の宛先を選ぶ (2体目が無い/タグ不明なら主役へ)
    const idx = (spk === 'b' && avatars[1]) ? 1 : 0;
    const target = avatars[idx];
    if (typeof m.mouth === 'number' && target) {
      target.mouthTarget = Math.max(0, Math.min(1, m.mouth));
      for (const av of avatars) { if (av && av !== target) av.mouthTarget = 0; }
    }
    if (typeof m.speaking === 'boolean') {
      speaking = m.speaking;
      // 体の動きの強さは「この子がしゃべっているか」で決まるので、話者ごとに持たせる
      for (const av of avatars) { if (av) av.talking = speaking && av === target; }
      if (!speaking) { for (const av of avatars) { if (av) av.mouthTarget = 0; } }
    }
    if (typeof m.emotion === 'string' && EMOTION_PRESETS.includes(m.emotion) && target) {
      // モデルが拡張表情の素材を持っていなければ近い標準表情へ落とす (無反応を避ける)
      target.emotion = resolveEmotion(target, m.emotion);
      target.emotionValue = 0.7;
    }
    // 身振りは emotion と同じワンショット契約 (毎 tick 載せるとクリップが先頭へ巻き戻り続けて固まる)
    if (typeof m.gesture === 'string' && GESTURES[m.gesture] && target) {
      target.gesture = m.gesture; target.gestureT = 0;
    }
    if (SHOW_SUBTITLE && subtitleEl && typeof m.subtitle === 'string') {
      const s = m.subtitle.trim();
      subtitleEl.textContent = s;
      subtitleEl.style.display = s ? 'block' : 'none';
    }
  };
}
connectWs();
