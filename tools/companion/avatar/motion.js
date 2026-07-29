// 外部モーションファイル (Mixamo の FBX) を VRM のボーンへ載せ替えるモジュール。
//
// Mixamo のアニメは "mixamorig:Hips" のような独自リグに付いているので、そのままでは VRM に当たらない。
// ここで各トラックを VRM の正規化ヒューマノイドボーンへ retarget して THREE.AnimationClip を作り、
// avatar.js が AnimationMixer で再生する。
//
// ⚠️ 腰の平行移動 (hips の position) は既定で捨てる。立ち絵はバストアップの画角なので、体が上下すると
//    「立ち絵がふわふわする」と見える (Phase 7 の不変条件)。腰の回転は上体のひねりに要るので残す。
import * as THREE from 'three';
import { FBXLoader } from './vendor/jsm/loaders/FBXLoader.js';

// FBXLoader はノード名の ':' を落とすので "mixamorig:Hips" は "mixamorigHips" になる。
// 念のため元表記でも引けるよう、キーは正規化 (英数字だけ・小文字) して突き合わせる。
const MIXAMO_TO_VRM = {
  Hips: 'hips',
  Spine: 'spine', Spine1: 'chest', Spine2: 'upperChest',
  Neck: 'neck', Head: 'head',
  LeftShoulder: 'leftShoulder', LeftArm: 'leftUpperArm', LeftForeArm: 'leftLowerArm', LeftHand: 'leftHand',
  LeftHandThumb1: 'leftThumbMetacarpal', LeftHandThumb2: 'leftThumbProximal', LeftHandThumb3: 'leftThumbDistal',
  LeftHandIndex1: 'leftIndexProximal', LeftHandIndex2: 'leftIndexIntermediate', LeftHandIndex3: 'leftIndexDistal',
  LeftHandMiddle1: 'leftMiddleProximal', LeftHandMiddle2: 'leftMiddleIntermediate', LeftHandMiddle3: 'leftMiddleDistal',
  LeftHandRing1: 'leftRingProximal', LeftHandRing2: 'leftRingIntermediate', LeftHandRing3: 'leftRingDistal',
  LeftHandPinky1: 'leftLittleProximal', LeftHandPinky2: 'leftLittleIntermediate', LeftHandPinky3: 'leftLittleDistal',
  RightShoulder: 'rightShoulder', RightArm: 'rightUpperArm', RightForeArm: 'rightLowerArm', RightHand: 'rightHand',
  RightHandThumb1: 'rightThumbMetacarpal', RightHandThumb2: 'rightThumbProximal', RightHandThumb3: 'rightThumbDistal',
  RightHandIndex1: 'rightIndexProximal', RightHandIndex2: 'rightIndexIntermediate', RightHandIndex3: 'rightIndexDistal',
  RightHandMiddle1: 'rightMiddleProximal', RightHandMiddle2: 'rightMiddleIntermediate', RightHandMiddle3: 'rightMiddleDistal',
  RightHandRing1: 'rightRingProximal', RightHandRing2: 'rightRingIntermediate', RightHandRing3: 'rightRingDistal',
  RightHandPinky1: 'rightLittleProximal', RightHandPinky2: 'rightLittleIntermediate', RightHandPinky3: 'rightLittleDistal',
  LeftUpLeg: 'leftUpperLeg', LeftLeg: 'leftLowerLeg', LeftFoot: 'leftFoot', LeftToeBase: 'leftToes',
  RightUpLeg: 'rightUpperLeg', RightLeg: 'rightLowerLeg', RightFoot: 'rightFoot', RightToeBase: 'rightToes',
};

const norm = (s) => s.toLowerCase().replace(/[^a-z0-9]/g, '');
const RIG_MAP = {};
for (const k in MIXAMO_TO_VRM) RIG_MAP[norm('mixamorig' + k)] = MIXAMO_TO_VRM[k];

const fbxLoader = new FBXLoader();

// FBX の中からアニメーションを1本選ぶ。Mixamo は "mixamo.com" という名前を付けるが、
// 書き出し方によっては別名 ("Take 001" 等) になるので、名前一致に頼らず先頭を採る。
function pickClip(asset) {
  const anims = asset.animations || [];
  if (anims.length === 0) return null;
  return anims.find((a) => a.name === 'mixamo.com') || anims[0];
}

/**
 * FBX を読み込んで、その VRM で再生できる AnimationClip に変換する。
 * @returns {Promise<{clip: THREE.AnimationClip, bones: Set<string>, duration: number}>}
 *   bones = このクリップが動かす VRM ヒューマノイドボーン名の集合 (avatar.js が
 *   「クリップが終わったあと元に戻すべきボーン」を知るために使う)。
 */
export async function loadMixamoMotion(url, vrm, opts = {}) {
  const withHipsPosition = !!opts.withHipsPosition; // 既定 false = 腰の上下移動は捨てる
  const asset = await fbxLoader.loadAsync(url);
  const src = pickClip(asset);
  if (!src) throw new Error(`アニメーションが入っていません (${url})`);

  const humanoid = vrm.humanoid;
  if (!humanoid) throw new Error('この VRM にヒューマノイドがありません');

  const tracks = [];
  const bones = new Set();
  const restRotationInverse = new THREE.Quaternion();
  const parentRestWorldRotation = new THREE.Quaternion();
  const _q = new THREE.Quaternion();
  const _v = new THREE.Vector3();
  const isVrm0 = vrm.meta?.metaVersion === '0';

  // 腰の高さの比でモーション側の移動量を合わせる (身長が違うと歩幅がずれる)。
  let hipsScale = 1;
  const mixamoHips = asset.getObjectByName('mixamorigHips')
    || asset.getObjectByName('mixamorig:Hips');
  const vrmHipsNode = humanoid.getNormalizedBoneNode('hips');
  if (mixamoHips && vrmHipsNode && mixamoHips.position.y > 1e-6) {
    const vrmHipsY = vrmHipsNode.getWorldPosition(_v).y;
    const vrmRootY = vrm.scene.getWorldPosition(new THREE.Vector3()).y;
    hipsScale = Math.abs(vrmHipsY - vrmRootY) / mixamoHips.position.y;
  }

  for (const track of src.tracks) {
    const dot = track.name.lastIndexOf('.');
    if (dot < 0) continue;
    const rigName = track.name.slice(0, dot);
    const property = track.name.slice(dot + 1);
    const vrmBone = RIG_MAP[norm(rigName)];
    if (!vrmBone) continue;
    const targetNode = humanoid.getNormalizedBoneNode(vrmBone);
    if (!targetNode) continue; // そのモデルに無いボーン (指など省略されがち)
    const rigNode = asset.getObjectByName(rigName);
    if (!rigNode || !rigNode.parent) continue;

    if (track instanceof THREE.QuaternionKeyframeTrack) {
      // Mixamo リグの初期姿勢ぶんを打ち消して「親から見た相対回転」に直す。
      rigNode.getWorldQuaternion(restRotationInverse).invert();
      rigNode.parent.getWorldQuaternion(parentRestWorldRotation);
      const values = Float32Array.from(track.values);
      for (let i = 0; i < values.length; i += 4) {
        _q.fromArray(values, i);
        _q.premultiply(parentRestWorldRotation).multiply(restRotationInverse);
        _q.toArray(values, i);
        if (isVrm0) { values[i] = -values[i]; values[i + 2] = -values[i + 2]; } // VRM0 は前が -Z
      }
      tracks.push(new THREE.QuaternionKeyframeTrack(`${targetNode.name}.${property}`, track.times, values));
      bones.add(vrmBone);
    } else if (track instanceof THREE.VectorKeyframeTrack && property === 'position') {
      // 位置は腰だけ意味がある (他のボーンは長さが変わってしまうので捨てる)
      if (vrmBone !== 'hips' || !withHipsPosition) continue;
      const values = Float32Array.from(track.values);
      for (let i = 0; i < values.length; i += 3) {
        values[i] *= hipsScale; values[i + 1] *= hipsScale; values[i + 2] *= hipsScale;
        if (isVrm0) { values[i] = -values[i]; values[i + 2] = -values[i + 2]; }
      }
      tracks.push(new THREE.VectorKeyframeTrack(`${targetNode.name}.${property}`, track.times, values));
      bones.add(vrmBone);
    }
  }

  if (tracks.length === 0) {
    throw new Error(`VRM に載せられるトラックがありませんでした (${url} / 先頭ノード名: ${src.tracks[0]?.name})`);
  }
  const clip = new THREE.AnimationClip(src.name || 'motion', src.duration, tracks);
  return { clip, bones, duration: clip.duration };
}

// companion.py が返すモーション一覧 (motions/ フォルダの自動索引)。
// 古い companion / フォルダ未作成なら 404 になるので、その場合は空リストで黙って進む。
export async function fetchMotionIndex() {
  try {
    const res = await fetch('./motions.json', { cache: 'no-store' });
    if (!res.ok) return [];
    const json = await res.json();
    return Array.isArray(json?.motions) ? json.motions : [];
  } catch (_) {
    return [];
  }
}
