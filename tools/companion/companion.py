#!/usr/bin/env python3
"""EndKnot AI実況相棒アプリ。

mod が吐くイベントファイル (companion-events.jsonl) を tail し、
Gemini Live API に流し込んで日本語の実況音声を出力デバイスへ再生する。
OBS がそのデバイスを拾えば配信に AI 実況が乗る。

必要なもの:
  - 環境変数 GEMINI_API_KEY (各自 Google AI Studio で発行)
  - pip install -r requirements.txt
  - EndKnot 側で EnableAICommentary=true (BepInEx/config/*.cfg)

使い方:
  python companion.py --events "C:/Path/To/AmongUs/EndKnot_DATA/companion-events.jsonl"
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import queue
import random
import re
import sys
import threading
import time
from collections import deque
from datetime import datetime
from pathlib import Path

try:
    from google import genai
    from google.genai import types
except ImportError:
    print("google-genai が見つかりません。 pip install -r requirements.txt を実行してください。")
    sys.exit(1)

try:
    import sounddevice as sd
except ImportError:
    print("sounddevice が見つかりません。 pip install -r requirements.txt を実行してください。")
    sys.exit(1)

# アバター(立ち絵)配信サーバ用。未導入でも音声実況は動くので必須にはしない。
# websockets 13+ の新しい asyncio サーバ API を使う (requirements で >=13 を要求)。
try:
    from websockets.asyncio.server import serve as _ws_serve
    from websockets.http11 import Response as _WsResponse
    from websockets.datastructures import Headers as _WsHeaders
    _HAS_WS = True
except Exception:
    _ws_serve = None
    _HAS_WS = False

import array
import math
import mimetypes
from urllib.parse import unquote, urlparse

# ---- 設定 (CLI/環境変数で上書き可) ----

DEFAULT_MODEL = os.environ.get("GEMINI_LIVE_MODEL", "gemini-2.5-flash-native-audio-preview-09-2025")
# --tts voicevox-text 用 (通常 generate_content のターン制テキスト経路)。ただし無料枠は
# audio dialog 系が太くテキスト系は極端に細いので、現状の主役は「Live native-audio の音声を
# 捨てて文字起こしをずん子合成に回す」--tts voicevox の方 (非効率だが枠の実態に合う)。
# 有料 API (Claude 等) へ移行できる状態になったらこのテキスト経路が差し替え点になる。
DEFAULT_TEXT_MODEL = os.environ.get("GEMINI_TEXT_MODEL", "gemini-2.5-flash")
DEFAULT_VOICE = os.environ.get("GEMINI_VOICE", "")  # 空 = モデル既定ボイス
AUDIO_SAMPLE_RATE = 24000  # Live API の出力 PCM は 24kHz 16bit mono
VOICEVOX_DEFAULT_URL = os.environ.get("EK_VOICEVOX_URL", "http://127.0.0.1:50021")
VOICEVOX_DEFAULT_SPEAKER = "東北ずん子"  # /speakers から名前部分一致で styleId を解決する
FILLER_INTERVAL_SEC = 27.0  # 無音がこれだけ続いたら場繋ぎを一言 (コスト対策で控えめ)
MIN_SEND_GAP_SEC = 3.0      # 連続送信の最小間隔
BATCH_WINDOW_SEC = 1.0      # イベントをまとめる窓
CHAT_EVENT_MAX_AGE_SEC = 30.0  # 古すぎるチャットは実況しない
SUBTITLE_CLEAR_AFTER_SEC = 8.0  # 発話終了からこれだけ経ったら字幕を消す
SUMMARY_MAX_LINES = 12  # 再接続時に引き継ぐ「あらすじ」の行数
RECENT_SAY_KEEP = 5     # 反復禁止用に覚えておく直近発話の数
RECAP_STALE_SEC = 6 * 3600  # recap.txt がこれより古ければ前回配信の残骸とみなして捨てる

PERSONA_FILE = Path(__file__).with_name("persona.txt")

DEFAULT_PERSONA = """\
あなたは Among Us の視聴者参加型ライブ配信の AI 実況者です。
- 明るく元気な日本語で、1回の発話は1〜3文の短さで話す。
- ロビーに参加者が来たら名前を呼んで歓迎する。
- 視聴者がチャットの !コマンド (「!大地震」「!隕石」など) で
  ゲームに干渉できることを、折に触れて楽しそうに宣伝する。
- !コマンドを読み上げるときは、先頭の「!」を必ず「びっくりマーク」と声に出して
  「びっくりマーク大地震」のように読む。視聴者がそのまま真似してチャットに打つので、
  「!」を黙って飛ばして「大地震」とだけ言ってはいけない (打っても何も起きなくなる)。
- イベント情報は【イベント】などのタグ付きテキストで届く。【】で囲まれたタグ自体は
  読み上げず、内容に自然にリアクションだけする。
- 同じ挨拶や同じ宣伝文句を繰り返さない。言い回しを毎回変える。
- 一方的なアナウンスの読み上げではなく、直前の出来事や自分がさっき言ったことを
  踏まえた「続きのおしゃべり」のように話す。
- 必ず日本語だけで話す。韓国語・英語など他の言語の文を混ぜない。
- 技術的な話 (API、ファイル、エラー) は絶対に口にしない。
- 下品な言葉や攻撃的な言葉は使わない。
- 特定のプレイヤーを「怪しい」と決めつけて疑わない (ゲームの公平性を守る)。
"""


def load_persona() -> str:
    if PERSONA_FILE.exists():
        return PERSONA_FILE.read_text(encoding="utf-8")
    return DEFAULT_PERSONA


# ---- 共有状態 (phase 追跡・あらすじ・活動時刻) ----

class StreamState:
    def __init__(self, recap_file: Path | None = None) -> None:
        self.phase = "lobby"           # 最後に受けた phase イベント
        self.recap: deque[str] = deque(maxlen=SUMMARY_MAX_LINES)  # 再接続用あらすじ
        self.last_real_event = time.monotonic()  # filler 休眠判定 (実イベントのみ更新)
        self.recent_says: deque[str] = deque(maxlen=RECENT_SAY_KEEP)  # 反復禁止用の直近発話
        self.last_filler_topic = ""    # 場繋ぎのお題が連続しないように前回を覚える
        self.last_demo_idx = -1        # デモ実況テンプレの前回インデックス
        self.pending_emotion: str | None = None  # 次の発話開始時にアバターへ1回だけ送る表情タグ
        self.recap_file = recap_file
        self.restored = False          # 起動時に前プロセスの recap を復元できたか
        # プロセス再起動 (auto-rehost 等) をまたいで記憶を引き継ぐ。os._exit で落ちても
        # note() のたびに書いてあるので取りこぼさない。古すぎるファイルは前回配信の残骸。
        if recap_file is not None and recap_file.exists():
            try:
                if time.time() - recap_file.stat().st_mtime <= RECAP_STALE_SEC:
                    lines = [l.strip() for l in recap_file.read_text(encoding="utf-8").splitlines() if l.strip()]
                    for l in lines[-SUMMARY_MAX_LINES:]:
                        self.recap.append(l)
                    self.restored = bool(self.recap)
            except Exception as ex:
                print(f"[recap] 復元エラー (無視して続行): {ex}")

    def note(self, line: str) -> None:
        """あらすじ用の1行メモ (【】タグは付けない素の日本語)。書くたびにディスクへも保存する。"""
        self.recap.append(line)
        if self.recap_file is not None:
            try:
                self.recap_file.write_text("\n".join(self.recap) + "\n", encoding="utf-8")
            except Exception as ex:
                print(f"[recap] 保存エラー: {ex}")

    def remember_say(self, text: str) -> None:
        """自分の発話を反復禁止プロンプト用に短く記憶する。"""
        t = text.strip()
        if t:
            self.recent_says.append(t[:60])

    def recap_text(self) -> str:
        if not self.recap:
            return ""
        return "直前までの配信の流れ (参考情報。読み上げず、文脈として把握だけすること):\n- " + "\n- ".join(self.recap)


# ---- 音声再生 (専用スレッド。async ループをブロックしない) ----

def _rms01(pcm: bytes) -> float:
    """16bit mono PCM チャンクの音量を 0..1 で返す (アバターの口の開き用)。numpy 非依存・安価。"""
    if not pcm:
        return 0.0
    a = array.array("h")  # 'h' = int16 (Windows/x86 はリトルエンディアン = Live API の PCM と一致)
    try:
        a.frombytes(pcm if len(pcm) % 2 == 0 else pcm[:-1])
    except Exception:
        return 0.0
    n = len(a)
    if n == 0:
        return 0.0
    step = max(1, n // 1024)  # 最大 ~1024 サンプルで十分 (毎チャンク全走査は無駄)
    s = 0
    c = 0
    for i in range(0, n, step):
        v = a[i]
        s += v * v
        c += 1
    if c == 0:
        return 0.0
    return math.sqrt(s / c) / 32768.0


class AudioPlayer:
    def __init__(self, device: int | str | None = None) -> None:
        self._q: queue.Queue[bytes | None] = queue.Queue()
        self._device = device  # None = 既定デバイス。仮想ケーブル (VB-Audio 等) に出すと立ち絵アプリへ直結できる
        self.last_audio_time = 0.0  # monotonic。実況が「喋っている」判定に使う
        self.level = 0.0  # 直近チャンクの音量 (0..1)。アバターサーバが読んで口パクに使う
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def feed(self, pcm: bytes) -> None:
        self.last_audio_time = time.monotonic()
        self._q.put(pcm)

    def is_speaking(self) -> bool:
        return not self._q.empty() or (time.monotonic() - self.last_audio_time) < 1.0

    def _run(self) -> None:
        try:
            stream = sd.RawOutputStream(samplerate=AUDIO_SAMPLE_RATE, channels=1, dtype="int16",
                                        device=self._device)
            stream.start()
        except Exception as ex:
            print(f"[audio] 出力デバイスを開けません ({self._device}): {ex}")
            print("[audio] --list-audio-devices でデバイス一覧を確認してください。既定デバイスで再試行します。")
            stream = sd.RawOutputStream(samplerate=AUDIO_SAMPLE_RATE, channels=1, dtype="int16")
            stream.start()
        while True:
            chunk = self._q.get()
            if chunk is None:
                break
            try:
                stream.write(chunk)
                self.last_audio_time = time.monotonic()
                self.level = max(self.level, _rms01(chunk))  # アタックは即時、減衰はサーバ側で
            except Exception as ex:  # デバイス断などで実況全体を殺さない
                print(f"[audio] 再生エラー: {ex}")
                time.sleep(0.5)


# ---- VoiceVox TTS (--tts voicevox: Gemini はテキストだけ書き、声はローカル VoiceVox が担う) ----

# 文の切れ目で逐次合成する (全文待ちだと発話開始が遅すぎる)
_SENTENCE_END_RE = re.compile(r"[。！？!?\n]")
# 【イベント】等のタグや markdown 記号は音声に乗せない (persona は「読み上げるな」だが
# テキストモードでは書いた文字が全部声になるので、こちらで確実に落とす)
_SPEECH_STRIP_RE = re.compile(r"【[^】]*】|[*_`#]")


def split_sentences(buf: str) -> tuple[list[str], str]:
    """確定した文のリストと、まだ切れ目が来ていない残りを返す。"""
    out: list[str] = []
    start = 0
    for m in _SENTENCE_END_RE.finditer(buf):
        s = buf[start:m.end()].strip()
        if s:
            out.append(s)
        start = m.end()
    return out, buf[start:]


def _wav_to_pcm(wav: bytes) -> bytes | None:
    """VoiceVox の WAV から 16bit mono PCM を取り出す (outputSamplingRate 指定済み前提)。"""
    import io
    import wave as wave_mod
    try:
        with wave_mod.open(io.BytesIO(wav)) as w:
            if w.getsampwidth() != 2 or w.getnchannels() != 1:
                print(f"[voicevox] 想定外の WAV 形式 (width={w.getsampwidth()}, ch={w.getnchannels()})")
                return None
            if w.getframerate() != AUDIO_SAMPLE_RATE:
                print(f"[voicevox] サンプルレート不一致 ({w.getframerate()} != {AUDIO_SAMPLE_RATE}) — 再生が速く/遅くなります")
            return w.readframes(w.getnframes())
    except Exception as ex:
        print(f"[voicevox] WAV 解析エラー: {ex}")
        return None


def resolve_voicevox_style(url: str, speaker_name: str, explicit_style: int) -> tuple[int, str] | None:
    """/speakers から話者名の部分一致で styleId を解決する。「ノーマル」スタイル優先。"""
    import urllib.request
    if explicit_style >= 0:
        return explicit_style, f"styleId={explicit_style} (明示指定)"
    try:
        with urllib.request.urlopen(f"{url}/speakers", timeout=5) as resp:
            speakers = json.load(resp)
    except Exception as ex:
        print(f"[voicevox] エンジンに接続できません ({url}): {ex}")
        return None
    for sp in speakers:
        name = sp.get("name", "")
        if speaker_name in name:
            styles = sp.get("styles") or []
            for st in styles:
                if st.get("name") == "ノーマル":
                    return st["id"], f"{name} (ノーマル, styleId={st['id']})"
            if styles:
                return styles[0]["id"], f"{name} ({styles[0].get('name')}, styleId={styles[0]['id']})"
    print(f"[voicevox] 話者「{speaker_name}」が /speakers に見つかりません (VOICEVOX 0.19+ が必要です)")
    return None


class VoiceVoxTts:
    """文単位のテキストを VoiceVox で合成して AudioPlayer に流す (専用スレッドで直列 = 順序保証)。

    字幕 (obs.feed_transcript) は PCM 投入と同時に書くので、音声と字幕のズレは最小。
    busy() は「合成待ち/合成中の文が残っているか」— turn_complete 後も合成が追いつくまで
    次のプロンプト送信を待たせるために使う (player.is_speaking() だけでは隙間ができる)。
    """

    def __init__(self, url: str, style_id: int, player: AudioPlayer, obs: "ObsFiles",
                 speed: float = 1.0) -> None:
        self.url = url.rstrip("/")
        self.style_id = style_id
        self.player = player
        self.obs = obs
        self.speed = speed
        self._q: queue.Queue[str] = queue.Queue()
        self._pending = 0
        self._lock = threading.Lock()
        threading.Thread(target=self._worker, name="voicevox-tts", daemon=True).start()

    def busy(self) -> bool:
        with self._lock:
            return self._pending > 0

    def speak(self, sentence: str) -> None:
        text = _SPEECH_STRIP_RE.sub("", sentence).strip()
        if not text:
            return
        with self._lock:
            self._pending += 1
        self._q.put(text)

    def _worker(self) -> None:
        while True:
            text = self._q.get()
            try:
                pcm = self._synth(text)
                if pcm:
                    self.obs.feed_transcript(text)
                    self.player.feed(pcm)
            except Exception as ex:  # 1文の失敗で実況全体を殺さない
                print(f"[voicevox] 合成エラー ({text[:20]}…): {ex}")
            finally:
                with self._lock:
                    self._pending -= 1

    def _synth(self, text: str) -> bytes | None:
        import urllib.request
        from urllib.parse import quote
        q_url = f"{self.url}/audio_query?text={quote(text)}&speaker={self.style_id}"
        req = urllib.request.Request(q_url, data=b"", method="POST")
        with urllib.request.urlopen(req, timeout=10) as resp:
            query = json.load(resp)
        query["outputSamplingRate"] = AUDIO_SAMPLE_RATE
        query["outputStereo"] = False
        if self.speed != 1.0:
            query["speedScale"] = self.speed
        s_url = f"{self.url}/synthesis?speaker={self.style_id}"
        req = urllib.request.Request(
            s_url, data=json.dumps(query).encode("utf-8"),
            headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(req, timeout=30) as resp:
            wav = resp.read()
        return _wav_to_pcm(wav)


# ---- OBS 連携ファイル (字幕 / 口パク状態) ----

# 音声は「びっくりマーク大地震」と読ませる (視聴者が耳で聞いてそのまま打てる) が、
# 文字起こしは「!」と「びっくりマーク」の両方を残すため字幕が「!びっくりマーク大地震」に二重化する。
# 字幕は目で見てそのまま打てる「!大地震」が正なので、読みの部分だけ畳む。
_BIKKURI_RE = re.compile(r"[!！]?\s*(?:びっくりマーク|ビックリマーク)")
# ストリーミング途中の書きかけ (「!びっ」) が一瞬字幕に映るのを防ぐ。
# 誤爆を避けるため「!」が前置されている場合のみ畳む (例:「あそび」の語尾は対象外)。
_BIKKURI_PARTIAL_RE = re.compile(
    r"[!！]\s*(?:びっくりマー|びっくりマ|びっくり|びっく|びっ|び"
    r"|ビックリマー|ビックリマ|ビックリ|ビック|ビッ|ビ)$")


def sanitize_subtitle(text: str) -> str:
    """字幕用に「!びっくりマーク大地震」→「!大地震」へ畳む (音声側は変更しない)。"""
    return _BIKKURI_PARTIAL_RE.sub("!", _BIKKURI_RE.sub("!", text))


class ObsFiles:
    """subtitle.txt = 現在の発話字幕、speaking.txt = "1"/"0" (立ち絵の口パク連動用)。"""

    def __init__(self, subtitle: Path | None, speaking: Path | None) -> None:
        self.subtitle_path = subtitle
        self.speaking_path = speaking
        self._subtitle_text = ""
        self._last_transcript_time = 0.0
        self._speaking_last = None  # 前回書いた値 (変化時のみ書く)

    def _write(self, path: Path | None, text: str) -> None:
        if path is None:
            return
        try:
            path.write_text(text, encoding="utf-8")
        except Exception as ex:
            print(f"[obs] 書き込みエラー ({path.name}): {ex}")

    def feed_transcript(self, chunk: str) -> None:
        self._subtitle_text += chunk
        self._last_transcript_time = time.monotonic()
        # 畳みはチャンク境界をまたぐので、累積テキスト側に対して掛ける
        self._write(self.subtitle_path, sanitize_subtitle(self._subtitle_text).strip())

    def new_utterance(self) -> None:
        self._subtitle_text = ""

    def tick(self, speaking: bool) -> None:
        """定期呼び出し: 口パク状態の更新と、古い字幕の消去。"""
        if speaking != self._speaking_last:
            self._speaking_last = speaking
            self._write(self.speaking_path, "1" if speaking else "0")
        if (self._subtitle_text and not speaking
                and time.monotonic() - self._last_transcript_time > SUBTITLE_CLEAR_AFTER_SEC):
            self._subtitle_text = ""
            self._write(self.subtitle_path, "")

    def current_subtitle(self) -> str:
        """アバターサーバ用: 現在の字幕テキスト (畳み済み)。ファイル出力の有無に関係なく取れる。"""
        return sanitize_subtitle(self._subtitle_text).strip()


# ---- 会話ログ (デバッグ用: 「何を送って何を喋ったか」を文脈ごと残す) ----

class ConvLog:
    """companion-log.jsonl に送信全文・発話全文・セッション区切りを追記する。

    「稀に意味不明なことを言う」を後から追うための記録で、
    send (こちらが送った指示全文) と say (Gemini の発話 transcript) を
    タイムスタンプ付きで対にして読めるようにする。
    """

    def __init__(self, path: Path | None) -> None:
        self.path = path

    def log(self, kind: str, **fields) -> None:
        if self.path is None:
            return
        rec = {"time": datetime.now().isoformat(timespec="seconds"), "kind": kind, **fields}
        try:
            with self.path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
        except Exception as ex:
            print(f"[convlog] 書き込みエラー: {ex}")


# ---- イベントファイルの tail ----

async def tail_events(path: Path, out: asyncio.Queue) -> None:
    """JSONL を末尾から追う。mod 側の truncate (サイズリセット) にも追従する。"""
    offset = path.stat().st_size if path.exists() else 0
    buf = ""
    while True:
        await asyncio.sleep(0.5)
        try:
            if not path.exists():
                offset = 0
                continue
            size = path.stat().st_size
            if size < offset:  # mod 側で truncate された
                offset = 0
                buf = ""
            if size == offset:
                continue
            with path.open("r", encoding="utf-8", errors="replace") as f:
                f.seek(offset)
                chunk = f.read()
                offset = f.tell()
            buf += chunk
            while "\n" in buf:
                line, buf = buf.split("\n", 1)
                line = line.strip()
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                ev["_recv"] = time.monotonic()
                await out.put(ev)
        except Exception as ex:
            print(f"[tail] 読み取りエラー: {ex}")


# ---- イベント → 実況指示テキスト ----

def emotion_for_event(ev: dict) -> str | None:
    """イベント種別 → アバター表情タグ (avatar.js の EMOTION_PRESETS)。None は表情変更なし。

    テキスト感情推定はしない — イベント駆動の静的マッピングが最小構成 (過剰設計回避)。
    """
    t = ev.get("type")
    if t in ("intervention", "eject"):
        return "surprised"
    if t == "sabotage":
        return "angry"
    if t in ("join", "_joins"):
        return "happy"
    if t == "leave":
        return "sad"
    if t == "gameEnd":
        return "sad" if ev.get("noVictors") else "happy"
    if t == "phase":
        return {
            "ingame": "happy",
            "meeting": "surprised",
            "lobby": "relaxed",
            "ended": "relaxed",
        }.get(ev.get("phase"))
    return None  # chat / demo / filler 等は表情そのまま


def format_event(ev: dict, state: StreamState, quiet_meeting: bool) -> str | None:
    t = ev.get("type")
    if t == "join":
        state.note(f"{ev.get('name', '?')} がロビーに参加")
        return f"【イベント】プレイヤー「{ev.get('name', '?')}」がロビーに参加した。名前を呼んで歓迎して。"
    if t == "leave":
        return f"【イベント】プレイヤー「{ev.get('name', '?')}」が退出した。軽く見送って。"
    if t == "intervention":
        state.note(f"視聴者 {ev.get('author', '?')} が {ev.get('kindName', '?')} を発動")
        return (f"【イベント】視聴者「{ev.get('author', '?')}」がコマンドで"
                f"「{ev.get('kindName', ev.get('kind', '?'))}」を発動した！大きくリアクションして、"
                f"発動した視聴者の名前を呼んで褒めて。")
    if t == "demo":
        kind = ev.get("kindName", ev.get("kind", "?"))
        # 毎回同じ指示だと毎回同じ宣伝文句になるので、切り口をローテーションする
        idx = random.choice([i for i in range(len(DEMO_TEMPLATES)) if i != state.last_demo_idx])
        state.last_demo_idx = idx
        return DEMO_TEMPLATES[idx].format(kind=kind) + anti_repeat_block(state)
    if t == "chat":
        if quiet_meeting and state.phase == "meeting":
            return None  # 会議中はプレイヤーの議論が主役。チャット読みで被せない
        text = str(ev.get("text", "")).strip()
        # !コマンドは intervention イベント側で実況するので二重反応を避ける
        if not text or text.startswith(("!", "！")):
            return None
        if time.monotonic() - ev.get("_recv", 0) > CHAT_EVENT_MAX_AGE_SEC:
            return None
        return f"【視聴者チャット】{ev.get('author', '?')}: {text[:80]} — 一言だけ軽く反応して。"
    if t == "eject":
        if ev.get("skipped"):
            state.note("会議は追放なしで終了")
            return "【イベント】投票の結果、誰も追放されなかった！スキップの緊張感を実況して。"
        name = ev.get("name") or "誰か"  # mod 側で名前解決に失敗した稀ケース
        role = ev.get("roleName")
        if role:
            state.note(f"{name} が追放された (役職: {role})")
            return (f"【イベント】投票の結果「{name}」が追放された！役職は「{role}」だった。"
                    f"この結果に大きくリアクションして。")
        state.note(f"{name} が追放された")
        return f"【イベント】投票の結果「{name}」が追放された！役職は明かされていない。ドキドキ感を実況して。"
    if t == "gameEnd":
        team = ev.get("winnerTeam", "?")
        winners = ev.get("winners") or []
        if ev.get("noVictors"):
            # 勝者なし終了 (ホスト中断/全滅/タイマー等)。winnerTeam には「理由の文言」が入っているので
            # 「勝利したのは〜」の文型にすると意味不明になる
            state.note(f"試合終了 ({team})")
            return (f"【イベント】試合はここで終了。理由は「{team}」。勝者はいない。"
                    f"勝利宣言はせず、仕切り直しの一言で軽く締めて。")
        state.note(f"試合終了: {team} の勝利")
        names = "、".join(str(w) for w in winners[:8])
        extra = f" 勝者は {names}。名前を呼んで讃えて。" if names else ""
        return f"【イベント】試合終了！勝利したのは「{team}」！{extra}盛大に締めて。"
    if t == "sabotage":
        kind = ev.get("kindName", ev.get("kind", "?"))
        return (f"【イベント】サボタージュ「{kind}」が発生した！緊急事態を盛り上げて実況して。"
                f"ただし誰の仕業かの推測はしない。")
    if t == "phase":
        phase = ev.get("phase")
        prev_phase = state.phase
        if phase:
            if phase == prev_phase:
                return None  # mod 再起動などで同じ phase が重複して届いた場合の保険
            state.phase = phase
        if phase == "ingame" and (ev.get("resumed") or prev_phase == "meeting"):
            # 会議明けの試合再開。直後に eject (追放結果) の実況が来るので、
            # ここで「試合が始まった」と言わせると開始実況の繰り返しになる。黙って通す。
            state.note("会議が終わり試合再開")
            return None
        if phase == "ingame":
            map_name = ev.get("map")
            count = ev.get("playerCount")
            mode = ev.get("mode")
            state.note(f"試合開始 ({map_name or '?'}・{count or '?'}人)")
            detail = ""
            if map_name:
                detail += f"マップは「{map_name}」、"
            if count:
                detail += f"{count}人での試合、"
            if mode and mode not in ("Standard", "標準"):
                detail += f"ゲームモードは「{mode}」、"
            return f"【イベント】試合が始まった！{detail}盛り上げて。"
        if phase == "meeting" and quiet_meeting:
            state.note("会議が始まった")
            return "【イベント】緊急会議が始まった。一言だけ短く触れて、あとはプレイヤーの議論を邪魔しないよう静かにする。"
        return {
            "lobby": "【イベント】ロビーに戻った。次の試合の参加者を募集するアナウンスをして。",
            "meeting": "【イベント】緊急会議が始まった。誰が怪しいかワクワクするコメントをして。",
            "ended": "【イベント】試合が終了した。お疲れさまの一言を。",
        }.get(phase)
    return None


def merge_joins(events: list[dict]) -> list[dict]:
    """同一バッチ内の join 連投を1件にまとめる (満員ロビーの入退室ラッシュ対策)。"""
    joins = [ev for ev in events if ev.get("type") == "join"]
    if len(joins) <= 1:
        return events
    names = [str(ev.get("name", "?")) for ev in joins]
    merged = dict(joins[-1])
    merged["type"] = "_joins"
    merged["names"] = names
    out = [ev for ev in events if ev.get("type") != "join"]
    out.append(merged)
    return out


def format_merged_joins(ev: dict, state: StreamState) -> str:
    names = ev.get("names", [])
    state.note(f"{len(names)}人がロビーに参加")
    listed = "」「".join(names[:6])
    return f"【イベント】プレイヤー「{listed}」たち{len(names)}人が続けてロビーに参加した！まとめて歓迎して。"


# ---- 場繋ぎ / デモのバリエーション生成 ----
# 「毎回同じセリフ」の主犯は毎回同一の場繋ぎプロンプトだった (2026-07-20 ログ分析:
# 送信の48%が場繋ぎで、1セッション内に同一文が最大87回)。お題を毎回変えて渡す。

SIMPLE_CMDS = ["!大地震", "!隕石", "!停電", "!リアクター", "!通信", "!ドア", "!偽死体"]
ARG_CMDS = [
    ("!呪い", "びっくりマーク呪いのあとに対象プレイヤーの名前"),
    ("!祝福", "びっくりマーク祝福のあとに助けたい人の名前"),
    ("!天の声", "びっくりマーク天の声のあとに画面に出したいメッセージ"),
]
CMD_RULE = ("なお、この配信の視聴者コマンドは決まった10種だけなので、"
            "名前を変えたり存在しないコマンドを作ったりしないこと。")

CHITCHAT_TOPICS = [
    "Among Us のマップの好きな場所や思い出をひとこと雑談する",
    "インポスターを見破るコツをひとつ、もったいぶらずに軽く語る",
    "自分がクルーだったらどのタスクが好きか、理由つきで話す",
    "視聴者に「どのマップが一番好き?」のような答えやすい質問を投げかける",
    "緊急会議での言い訳あるあるをひとつ挙げて笑いにする",
    "この配信にはたくさんの特殊役職が出ることを、ワクワク感を込めて紹介する",
    "「もし自分が宇宙船に乗ったら」の妄想ネタでひとボケする",
    "視聴者に「今日はどんな試合が見たい?」と軽く聞いてみる",
    "インポスターにバレずにキルする緊張感について、視聴者と共感トークをする",
    "初めて Among Us を見る人向けに、このゲームの面白さをひとことで表現する",
]

RECRUIT_ANGLES = [
    "ゲームへの参加をゆるく募集する。",
    "初見さんに向けて、誰でも今から参加できることを伝える。",
    "今のロビーの様子に触れながら参加を誘う。",
    "「観てるだけでも、参加しても楽しいよ」というスタンスで誘う。",
]

DEMO_TEMPLATES = [
    "【イベント】自動デモで「{kind}」の演出が再生された。視聴者もチャットに !コマンドを打てば同じことを起こせると宣伝して。",
    "【イベント】画面で「{kind}」の演出が再生された。今の演出がどんな見た目・雰囲気だったかを楽しそうに実況して。",
    "【イベント】デモ演出「{kind}」が再生された。初めて見る視聴者向けに、これは視聴者自身がチャットの !コマンドで起こせるものだと短く説明して。",
    "【イベント】演出「{kind}」が再生された。「次はどの演出が見たい?」のように視聴者へ軽く問いかけて。",
]


def anti_repeat_block(state: StreamState) -> str:
    """直近の自分の発話を提示して「同じ話をするな」を実弾で伝える。

    ペルソナの抽象的な「繰り返すな」だけではセッション内の言い回し固着を止められなかった
    (既知現象)。具体的な直近発話を見せるのが効く。
    """
    if not state.recent_says:
        return ""
    quoted = " / ".join(f"「{s}」" for s in state.recent_says)
    return ("\n【重要】あなたの直近の発言: " + quoted +
            " — これらと同じ言い回し・同じ話題の蒸し返しは禁止。別の切り口で話すこと。")


def build_filler_prompt(state: StreamState) -> str:
    """場繋ぎのお題を毎回ローテーションで選んで具体的な指示を作る。"""
    topics = ["recruit", "command", "chitchat", "chitchat"]  # 雑談を厚めに (会話感の主成分)
    if state.recap:
        topics.append("recap")
    pool = [t for t in topics if t != state.last_filler_topic] or topics
    topic = random.choice(pool)
    state.last_filler_topic = topic

    if topic == "recruit":
        body = random.choice(RECRUIT_ANGLES)
    elif topic == "command":
        if random.random() < 0.3:
            cmd, how = random.choice(ARG_CMDS)
            body = (f"視聴者コマンド「{cmd}」を紹介する。使い方は「{how}」をチャットに打つ、"
                    f"というところまで必ずセットで説明すること (後ろの指定を省くと何も起きない)。{CMD_RULE}")
        else:
            cmds = random.sample(SIMPLE_CMDS, k=random.choice([1, 2]))
            body = ("視聴者コマンド「" + "」「".join(cmds) + "」を紹介して、"
                    f"チャットに打つだけでゲームに干渉できることを楽しそうに宣伝する。{CMD_RULE}")
    elif topic == "recap":
        recent = "、".join(list(state.recap)[-3:])
        body = f"さっきの出来事 ({recent}) のどれか1つに軽く触れて、感想やこの後への期待を話す。"
    else:
        body = random.choice(CHITCHAT_TOPICS) + "。"

    return ("【場繋ぎ】しばらく何も起きていない。次のお題で、配信を見ている人に向けて1〜2文だけ短く話して。\n"
            f"お題: {body}" + anti_repeat_block(state))


GREETING_PROMPT = "【イベント】配信の実況を開始した。視聴者に向けて短くオープニングの挨拶をして。"
REJOIN_PROMPT = ("【イベント】実況の音声が一時中断から復帰した。配信自体はさっきから続いているので、"
                 "初めましての挨拶やオープニングはやり直さず、「実況再開」程度の短い一言から"
                 "自然に続きへ入って。")


# ---- 実況セッション (Live 音声モード / VoiceVox テキストモード共通の部品) ----

class TurnGate:
    """「いま応答ターンが開いているか」と最終送信時刻。send/receive の両側から触るので束ねる。"""

    def __init__(self) -> None:
        self.open = False
        self.last_send = 0.0


def build_system_instruction(state: StreamState) -> tuple[str, str]:
    """persona + (あれば) recap を合成して (system, 使った recap) を返す。"""
    system = load_persona()
    recap = state.recap_text()
    if recap:
        system = system + "\n\n" + recap
    return system, recap


async def commentary_loop(events: asyncio.Queue, args: argparse.Namespace, state: StreamState,
                          player: AudioPlayer, tts: VoiceVoxTts | None, gate: TurnGate,
                          send_text, first_connect: bool) -> None:
    """イベントのバッチ化→送信、場繋ぎ、発話被り防止の本体 (音声/テキスト両モード共通)。"""
    if first_connect:
        # recap を復元できた新プロセスは「配信の続き」なのでオープニングをやり直さない
        await send_text(REJOIN_PROMPT if state.restored else GREETING_PROMPT)

    def output_busy() -> bool:
        return gate.open or player.is_speaking() or (tts is not None and tts.busy())

    last_activity = time.monotonic()
    while True:
        # イベントを最大 BATCH_WINDOW 秒ぶんまとめる
        raw: list[dict] = []
        try:
            ev = await asyncio.wait_for(events.get(), timeout=1.0)
            deadline = time.monotonic() + BATCH_WINDOW_SEC
            while True:
                raw.append(ev)
                remain = deadline - time.monotonic()
                if remain <= 0:
                    break
                try:
                    ev = await asyncio.wait_for(events.get(), timeout=remain)
                except asyncio.TimeoutError:
                    break
        except asyncio.TimeoutError:
            pass

        batch: list[str] = []
        batch_emotion: str | None = None
        for ev in merge_joins(raw):
            if ev.get("type") == "_joins":
                batch.append(format_merged_joins(ev, state))
            else:
                line = format_event(ev, state, args.quiet_meeting)
                if not line:
                    continue  # 実況しないイベントは表情も動かさない
                batch.append(line)
            batch_emotion = emotion_for_event(ev) or batch_emotion
        if raw:
            state.last_real_event = time.monotonic()  # filler 休眠の解除 (chat/join 含む全実イベント)

        now = time.monotonic()
        if batch:
            # 喋り終わるまで待ってから送る (発話の被り/自己中断防止)
            while output_busy() or (now - gate.last_send) < MIN_SEND_GAP_SEC:
                await asyncio.sleep(0.3)
                now = time.monotonic()
            if batch_emotion:
                state.pending_emotion = batch_emotion  # 発話開始時に avatar_ticker が1回だけ載せる
            await send_text("\n".join(batch[-5:]))  # 溜まりすぎたら新しい5件だけ
            last_activity = now
        elif (now - last_activity) > FILLER_INTERVAL_SEC and not output_busy():
            if args.quiet_meeting and state.phase == "meeting":
                continue  # 会議中は場繋ぎしない
            if (now - state.last_real_event) > args.dormant_after:
                continue  # 誰も居ない・何も起きない時間帯はトークンを焼かない (休眠)
            await send_text(build_filler_prompt(state))
            last_activity = now


# ---- Gemini Live セッション本体 (音声モード) ----

async def run_session(client: genai.Client, args: argparse.Namespace, events: asyncio.Queue,
                      player: AudioPlayer, obs: ObsFiles, state: StreamState,
                      first_connect: bool, conv: ConvLog, tts: VoiceVoxTts | None = None) -> None:
    """Live native-audio セッション。tts 指定時は Gemini の音声を捨て、文字起こしをずん子ボイスで読む。

    (音声を作らせて捨てるのは非効率だが、無料枠が audio dialog 系にしか無いための割り切り。
    文字起こしは「びっくりマーク大地震」のような読み形で届くので VoiceVox の入力に都合が良い。)
    """
    system = load_persona()
    recap_used = ""
    # 再接続だけでなく初回接続でも recap があれば引き継ぐ (recap.txt 永続化により、
    # プロセス再起動後の初回接続 = 「配信の続き」になったため)。読み上げ回避で system 側に載せる。
    recap = state.recap_text()
    if recap:
        system = system + "\n\n" + recap
        recap_used = recap

    config = types.LiveConnectConfig(
        response_modalities=["AUDIO"],
        system_instruction=system,
        output_audio_transcription=types.AudioTranscriptionConfig(),  # 字幕用の文字起こし
    )
    if args.voice:
        config.speech_config = types.SpeechConfig(
            voice_config=types.VoiceConfig(
                prebuilt_voice_config=types.PrebuiltVoiceConfig(voice_name=args.voice)))

    async with client.aio.live.connect(model=args.model, config=config) as session:
        print(f"[live] 接続完了 (model={args.model}"
              + (f", voice={args.voice}" if args.voice else "")
              + (f", 声はVoiceVox styleId={tts.style_id} (Gemini音声は破棄)" if tts is not None else "")
              + ")")
        # 再接続は文脈喪失 (=意味不明発言) の第一容疑なので、引き継いだあらすじごと区切りを記録する
        conv.log("session", first=first_connect, model=args.model, recap=recap_used)
        gate = TurnGate()
        say_buf = ""  # 現在の発話の transcript 蓄積 (turn_complete でログへ吐く)

        def flush_say() -> None:
            nonlocal say_buf
            if say_buf.strip():
                conv.log("say", text=say_buf.strip())
                state.remember_say(say_buf)  # 反復禁止プロンプトの材料
            say_buf = ""

        async def send_text(text: str) -> None:
            print(f"[live] 送信: {text.splitlines()[0][:60]}")
            flush_say()  # turn_complete を取りこぼした場合でも send 前に前の発話を確定させる
            conv.log("send", text=text)
            obs.new_utterance()
            await session.send_client_content(
                turns=types.Content(role="user", parts=[types.Part(text=text)]),
                turn_complete=True,
            )
            gate.open = True
            gate.last_send = time.monotonic()

        tts_buf = ""  # VoiceVox モード: 文の切れ目待ちの文字起こし

        async def receiver() -> None:
            nonlocal say_buf, tts_buf
            while True:
                async for resp in session.receive():
                    if resp.data and tts is None:
                        player.feed(resp.data)  # VoiceVox モードでは Gemini の声は捨てる
                    sc = getattr(resp, "server_content", None)
                    if sc is not None:
                        tr = getattr(sc, "output_transcription", None)
                        if tr is not None and getattr(tr, "text", None):
                            say_buf += tr.text
                            if tts is None:
                                obs.feed_transcript(tr.text)
                            else:
                                # 字幕は VoiceVox 側が PCM 投入と同時に書く (音声とのズレ防止)
                                tts_buf += tr.text
                                done, tts_buf = split_sentences(tts_buf)
                                for s in done:
                                    tts.speak(s)
                        if getattr(sc, "turn_complete", False):
                            if tts is not None and tts_buf.strip():
                                tts.speak(tts_buf)  # 切れ目が来ないまま終わった残りも読む
                            tts_buf = ""
                            gate.open = False
                            flush_say()

        async def obs_ticker() -> None:
            while True:
                obs.tick(player.is_speaking())
                await asyncio.sleep(0.2)

        recv_task = asyncio.create_task(receiver())
        obs_task = asyncio.create_task(obs_ticker())
        try:
            await commentary_loop(events, args, state, player, tts, gate, send_text, first_connect)
        finally:
            recv_task.cancel()
            obs_task.cancel()
            flush_say()


# ---- テキスト実況セッション (--tts voicevox: 通常 Gemini API + ローカル VoiceVox 合成) ----

TEXT_HISTORY_MAX_TURNS = 30  # 会話履歴の保持ターン数 (無制限に伸ばすとトークンを焼き続ける)


async def run_text_session(client: genai.Client, args: argparse.Namespace, events: asyncio.Queue,
                           player: AudioPlayer, obs: ObsFiles, state: StreamState,
                           first_connect: bool, conv: ConvLog, tts: VoiceVoxTts) -> None:
    """Live API を使わないターン制テキスト実況。声は VoiceVox が担う。

    Live と違いセッション時間上限が無いので、正常系では接続し直しが起きない。
    履歴は deque で持ち、長時間配信でも直近 TEXT_HISTORY_MAX_TURNS 往復だけを文脈に使う
    (それ以前の流れは recap が persona 側で補う)。
    """
    system, recap_used = build_system_instruction(state)
    gen_config = types.GenerateContentConfig(system_instruction=system)
    history: deque = deque(maxlen=TEXT_HISTORY_MAX_TURNS * 2)  # user/model の Content ペア
    print(f"[text] テキスト実況開始 (model={args.model}, voice=VoiceVox styleId={tts.style_id})")
    conv.log("session", first=first_connect, model=args.model, recap=recap_used, tts="voicevox")

    gate = TurnGate()

    async def _turn(text: str) -> None:
        """1往復: プロンプト送信→ストリーミング受信→文単位で VoiceVox へ。"""
        say_buf = ""
        tts_buf = ""
        try:
            stream = await client.aio.models.generate_content_stream(
                model=args.model,
                contents=list(history) + [types.Content(role="user", parts=[types.Part(text=text)])],
                config=gen_config,
            )
            async for chunk in stream:
                t = getattr(chunk, "text", None)
                if not t:
                    continue
                say_buf += t
                tts_buf += t
                done, tts_buf = split_sentences(tts_buf)
                for s in done:
                    tts.speak(s)
            if tts_buf.strip():
                tts.speak(tts_buf)  # 文の切れ目が来ないまま終わった残り
            history.append(types.Content(role="user", parts=[types.Part(text=text)]))
            history.append(types.Content(role="model", parts=[types.Part(text=say_buf or "(無言)")]))
            if say_buf.strip():
                conv.log("say", text=say_buf.strip())
                state.remember_say(say_buf)
        except Exception as ex:  # 1ターンの失敗で実況を止めない (次のイベントで再挑戦)
            print(f"[text] 生成エラー: {ex}")
            conv.log("session_end", error=f"turn: {ex}")
        finally:
            gate.open = False

    async def send_text(text: str) -> None:
        print(f"[text] 送信: {text.splitlines()[0][:60]}")
        conv.log("send", text=text)
        obs.new_utterance()
        gate.open = True
        gate.last_send = time.monotonic()
        asyncio.create_task(_turn(text))

    await commentary_loop(events, args, state, player, tts, gate, send_text, first_connect)


# ---- アバター(立ち絵)配信サーバ ----

class AvatarServer:
    """avatar/ を静的配信しつつ /ws で口パク情報 (mouth/speaking/subtitle) を配信する軽量サーバ。

    static も WebSocket も同一ポートで出すので、OBS のブラウザソースは 1 つの URL
    (http://127.0.0.1:<port>/) を指すだけで立ち絵が出る。ローカル専用 (既定 127.0.0.1)。
    websockets 未導入なら黙って無効化し、音声実況側は通常どおり動く。
    """

    def __init__(self, root: Path, host: str, port: int) -> None:
        self.root = root
        self.host = host
        self.port = port
        self.clients: set = set()
        self._server = None

    async def start(self) -> None:
        if not _HAS_WS:
            print("[avatar] websockets(>=13) 未導入のためアバターサーバは起動しません "
                  "(pip install -r requirements.txt でリップシンク配信が有効になります)")
            return
        if not self.root.exists():
            print(f"[avatar] avatar フォルダが見つかりません: {self.root} "
                  "(mod が実体化するまでページは 404 になります)")
        try:
            self._server = await _ws_serve(
                self._ws_handler, self.host, self.port,
                process_request=self._process_request)
            print(f"[avatar] アバターサーバ起動: http://{self.host}:{self.port}/  "
                  "← OBS のブラウザソースにこの URL を設定してください")
        except Exception as ex:
            print(f"[avatar] アバターサーバを起動できませんでした ({self.host}:{self.port}): {ex}")

    async def _ws_handler(self, ws):
        # websockets 13+: ハンドラ引数は接続のみ。パスは ws.request.path で取れる。
        self.clients.add(ws)
        try:
            async for _ in ws:  # クライアントからの受信は使わないが、接続維持のため回す
                pass
        except Exception:
            pass
        finally:
            self.clients.discard(ws)

    def _reply(self, status, reason, body: bytes, ctype: str = "text/plain; charset=utf-8"):
        headers = _WsHeaders()
        headers["Content-Type"] = ctype
        headers["Content-Length"] = str(len(body))
        headers["Cache-Control"] = "no-store"
        return _WsResponse(status, reason, headers, body)

    def _process_request(self, connection, request):
        # /ws は WebSocket ハンドシェイクへ通す (None を返すと websockets 側が処理)
        try:
            p = urlparse(request.path).path
        except Exception:
            return self._reply(400, "Bad Request", b"bad request")
        if p == "/ws":
            return None
        # それ以外は静的ファイル配信 (パストラバーサル防止)
        rel = unquote(p).lstrip("/")
        if rel == "":
            rel = "index.html"
        try:
            root_resolved = self.root.resolve()
            target = (root_resolved / rel).resolve()
            if target != root_resolved and root_resolved not in target.parents:
                return self._reply(403, "Forbidden", b"forbidden")
        except Exception:
            return self._reply(404, "Not Found", b"not found")
        if not target.is_file():
            return self._reply(404, "Not Found", b"not found")
        try:
            body = target.read_bytes()
        except Exception:
            return self._reply(404, "Not Found", b"not found")
        ctype = mimetypes.guess_type(str(target))[0] or "application/octet-stream"
        if target.suffix == ".js":
            ctype = "text/javascript"  # ESM は正しい MIME でないとブラウザが実行を拒否する
        return self._reply(200, "OK", body, ctype)

    async def broadcast(self, payload: dict) -> None:
        if not self.clients:
            return
        msg = json.dumps(payload, ensure_ascii=False)
        dead = []
        for ws in list(self.clients):
            try:
                await ws.send(msg)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.clients.discard(ws)


async def main_async(args: argparse.Namespace) -> None:
    api_key = os.environ.get("GEMINI_API_KEY")
    if not api_key:
        print("環境変数 GEMINI_API_KEY が設定されていません。Google AI Studio で発行して設定してください。")
        sys.exit(1)

    events_path = Path(args.events)
    print(f"[main] events: {events_path}")
    if not events_path.exists():
        print("[main] イベントファイルがまだ無いので、mod 側の起動を待ちます (EnableAICommentary=true を確認)")

    subtitle = None if args.no_obs_files else Path(args.subtitle)
    speaking = None if args.no_obs_files else Path(args.speaking_file)
    if subtitle:
        print(f"[main] 字幕: {subtitle} / 口パク状態: {speaking} (OBS のテキストソース/立ち絵連動に使えます)")

    device: int | str | None = None
    if args.audio_device:
        device = int(args.audio_device) if args.audio_device.isdigit() else args.audio_device
        print(f"[main] 音声出力デバイス: {device}")

    conv = ConvLog(None if args.no_conv_log else Path(args.conv_log))
    if conv.path:
        print(f"[main] 会話ログ: {conv.path} (send/say のペアで後から検証できます)")

    client = genai.Client(api_key=api_key)
    events: asyncio.Queue = asyncio.Queue()
    player = AudioPlayer(device)
    obs = ObsFiles(subtitle, speaking)

    tts: VoiceVoxTts | None = None
    if args.tts in ("voicevox", "voicevox-text"):
        resolved = resolve_voicevox_style(args.voicevox_url, args.voicevox_speaker, args.voicevox_style)
        if resolved is None:
            print("[voicevox] VoiceVox が使えないため Gemini 音声にフォールバックします "
                  "(VOICEVOX を起動して相棒を再起動すればずん子ボイスになります)")
            args.tts = "gemini"
            if not args.model_explicit:
                args.model = DEFAULT_MODEL  # テキスト用モデルのままだと AUDIO 応答が返らない
        else:
            style_id, desc = resolved
            tts = VoiceVoxTts(args.voicevox_url, style_id, player, obs, args.voicevox_speed)
            print(f"[voicevox] 実況ボイス: {desc} @ {args.voicevox_url}")
            print("[voicevox] 配信概要欄などにクレジット「VOICEVOX:東北ずん子」の表記をお忘れなく")
    recap_file = None if args.no_recap_file else Path(args.recap_file)
    state = StreamState(recap_file)
    if state.restored:
        print(f"[recap] 前プロセスのあらすじを復元しました ({len(state.recap)}行): {recap_file}")
    asyncio.create_task(tail_events(events_path, events))

    # アバター(立ち絵)配信サーバ + 口パク情報のブロードキャスト (~30Hz)
    avatar = AvatarServer(Path(__file__).with_name("avatar"), args.avatar_host, args.avatar_port)
    if not args.no_avatar:
        await avatar.start()

        async def avatar_ticker() -> None:
            while True:
                try:
                    is_speaking = player.is_speaking()
                    level = player.level
                    player.level *= 0.82  # 緩やかに減衰: 連続発話中は口が開いた状態を保つ (0.55 だと谷が深すぎて口パクが疎らに見えた)
                    mouth = min(1.0, level * 4.0) if is_speaking else 0.0
                    payload = {
                        "mouth": round(mouth, 3),
                        "speaking": is_speaking,
                        "subtitle": obs.current_subtitle(),
                    }
                    # 表情タグは発話開始の最初の tick で1回だけ載せる (avatar.js は受信のたびに
                    # emotionValue をリセットするので、毎 tick 載せると表情が固まったままになる)
                    if is_speaking and state.pending_emotion:
                        payload["emotion"] = state.pending_emotion
                        state.pending_emotion = None
                    await avatar.broadcast(payload)
                except Exception:
                    pass
                await asyncio.sleep(0.033)

        asyncio.create_task(avatar_ticker())

    first = True
    backoff = 2.0
    while True:
        started = time.monotonic()
        try:
            if tts is not None and args.tts == "voicevox-text":
                await run_text_session(client, args, events, player, obs, state, first, conv, tts)
            else:
                await run_session(client, args, events, player, obs, state, first, conv, tts)
        except Exception as ex:
            print(f"[live] セッション終了/エラー: {ex}")
            conv.log("session_end", error=str(ex))
        first = False
        # 長く生きていたら backoff リセット (Live API のセッション時間上限による切断は正常系)
        backoff = 2.0 if time.monotonic() - started > 60 else min(backoff * 2, 60.0)
        print(f"[live] {backoff:.0f} 秒後に再接続します")
        await asyncio.sleep(backoff)


def _start_parent_watchdog() -> None:
    """親 (Among Us) が死んだら自分も即終了する見張り。

    相棒は ShellExecute (cmd) 起動でジョブオブジェクトの外に出てしまうため、ホスト (AU) が
    ウォッチドッグの強制 kill / auto-rehost で落ちても道連れにならず孤児として残り続ける。
    その状態で AU が再起動すると新しい相棒が別プロセスで立ち上がるので、再起動のたびに増殖し、
    複数インスタンスが同じイベントファイルを読んで実況が被り合う (配信終盤に壊れる原因)。
    起動時に渡された親 PID (EK_COMPANION_PARENT_PID) を監視し、消えたら os._exit で確実に落ちる。
    """
    raw = os.environ.get("EK_COMPANION_PARENT_PID", "").strip()
    if not raw:
        return
    try:
        parent_pid = int(raw)
    except ValueError:
        return
    if parent_pid <= 0:
        return

    def _pid_alive(pid: int) -> bool:
        if os.name == "nt":
            import ctypes
            PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
            STILL_ACTIVE = 259
            k = ctypes.windll.kernel32
            h = k.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
            if not h:
                return False  # 開けない = ほぼ消滅
            try:
                code = ctypes.c_ulong()
                ok = k.GetExitCodeProcess(h, ctypes.byref(code))
                return bool(ok) and code.value == STILL_ACTIVE
            finally:
                k.CloseHandle(h)
        try:
            os.kill(pid, 0)  # 非 Windows (開発用フォールバック)
            return True
        except OSError:
            return False

    def _watch() -> None:
        seen_alive = False  # 一度でも生存を確認してから監視 (起動直後の取りこぼし・誤爆防止)
        while True:
            if _pid_alive(parent_pid):
                seen_alive = True
            elif seen_alive:
                print(f"[watchdog] 親プロセス (PID {parent_pid}) が終了したので相棒も終了します")
                os._exit(0)
            time.sleep(3.0)

    threading.Thread(target=_watch, name="parent-watchdog", daemon=True).start()


def main() -> None:
    # --list-audio-devices は --events 必須チェックより先に処理する (単体で使えるように)
    if "--list-audio-devices" in sys.argv:
        print(sd.query_devices())
        print("\n出力デバイスの「名前の一部」か番号を --audio-device に渡してください。")
        return

    here = Path(__file__).parent
    parser = argparse.ArgumentParser(description="EndKnot AI実況相棒アプリ")
    parser.add_argument("--events", required=True,
                        help="companion-events.jsonl のパス (Among Us フォルダ/EndKnot_DATA/ 配下)")
    parser.add_argument("--model", default="",
                        help=f"モデル名 (default: {DEFAULT_MODEL}、--tts voicevox-text 時は {DEFAULT_TEXT_MODEL}。"
                             "環境変数 GEMINI_LIVE_MODEL / GEMINI_TEXT_MODEL でも上書き可)")
    parser.add_argument("--tts", choices=["gemini", "voicevox", "voicevox-text"],
                        default=os.environ.get("EK_COMPANION_TTS", "gemini"),
                        help="実況の声の作り方。gemini = Live API のネイティブ音声。"
                             "voicevox = Live native-audio セッションはそのまま (無料枠が太い)、"
                             "Gemini の音声は捨てて文字起こしをローカル VOICEVOX のずん子ボイスで読む "
                             "(要: VOICEVOX 0.19+ 起動中。アバターの見た目と声が一致する)。"
                             "voicevox-text = 声は同じくずん子だが、テキスト生成を通常 Gemini API のターン制で行う "
                             "(テキスト系の利用枠が確保できたとき/他社 LLM へ移行するとき用)")
    parser.add_argument("--voicevox-url", default=VOICEVOX_DEFAULT_URL,
                        help=f"VOICEVOX エンジンの URL (default: {VOICEVOX_DEFAULT_URL}、環境変数 EK_VOICEVOX_URL でも可)")
    parser.add_argument("--voicevox-speaker", default=VOICEVOX_DEFAULT_SPEAKER,
                        help=f"VOICEVOX の話者名 (部分一致、default: {VOICEVOX_DEFAULT_SPEAKER})")
    parser.add_argument("--voicevox-style", type=int, default=-1,
                        help="styleId を直接指定 (指定時は --voicevox-speaker の名前解決を飛ばす)")
    parser.add_argument("--voicevox-speed", type=float, default=1.0,
                        help="読み上げ速度 (speedScale, default: 1.0)")
    parser.add_argument("--voice", default=DEFAULT_VOICE,
                        help="実況ボイス名 (Live API の prebuilt voice。例: Puck, Charon, Kore, Fenrir, Aoede。"
                             "環境変数 GEMINI_VOICE でも指定可。空ならモデル既定)")
    parser.add_argument("--quiet-meeting", action="store_true",
                        help="会議中は場繋ぎ・チャット反応を止めてプレイヤーの議論を邪魔しない")
    parser.add_argument("--dormant-after", type=float, default=300.0,
                        help="実イベントがこの秒数無いとき場繋ぎを休眠してトークン消費を止める (default: 300)")
    parser.add_argument("--subtitle", default=str(here / "subtitle.txt"),
                        help="字幕ファイルの出力先 (OBS のテキストソースで読む。default: スクリプトと同じフォルダ)")
    parser.add_argument("--speaking-file", default=str(here / "speaking.txt"),
                        help='発話中フラグ "1"/"0" の出力先 (立ち絵の口パク連動用)')
    parser.add_argument("--no-obs-files", action="store_true",
                        help="字幕・口パク状態ファイルを出力しない")
    parser.add_argument("--conv-log", default=str(here / "companion-log.jsonl"),
                        help="会話ログ (送信全文/発話全文/セッション区切り) の追記先 JSONL "
                             "(default: スクリプトと同じフォルダの companion-log.jsonl)")
    parser.add_argument("--no-conv-log", action="store_true",
                        help="会話ログを残さない")
    parser.add_argument("--recap-file", default=str(here / "recap.txt"),
                        help="あらすじの永続化先。プロセス再起動 (auto-rehost 等) をまたいで記憶を引き継ぎ、"
                             "オープニング挨拶のやり直しを防ぐ (default: スクリプトと同じフォルダの recap.txt)")
    parser.add_argument("--no-recap-file", action="store_true",
                        help="あらすじをディスクに残さない (再起動ごとに記憶リセット)")
    parser.add_argument("--audio-device", default=os.environ.get("COMPANION_AUDIO_DEVICE", ""),
                        help="実況音声の出力先デバイス (名前の一部か番号)。仮想ケーブル (VB-Audio 等) を指定すると"
                             "アバターアプリのリップシンクに直結できる。--list-audio-devices で一覧表示。"
                             "空なら既定デバイス")
    parser.add_argument("--list-audio-devices", action="store_true",
                        help="音声デバイスの一覧を表示して終了")
    parser.add_argument("--avatar-port", type=int, default=int(os.environ.get("EK_AVATAR_PORT", "8777")),
                        help="アバター(立ち絵)配信サーバのポート (default: 8777)。"
                             "OBS のブラウザソースに http://127.0.0.1:<port>/ を設定する")
    parser.add_argument("--avatar-host", default=os.environ.get("EK_AVATAR_HOST", "127.0.0.1"),
                        help="アバターサーバの待受ホスト (default: 127.0.0.1 = ローカルのみ)")
    parser.add_argument("--no-avatar", action="store_true",
                        help="アバター配信サーバを起動しない (音声実況のみ)")
    args = parser.parse_args()
    args.model_explicit = bool(args.model)  # VoiceVox フォールバック時のモデル巻き戻し判定に使う
    if not args.model:
        args.model = DEFAULT_TEXT_MODEL if args.tts == "voicevox-text" else DEFAULT_MODEL
    _start_parent_watchdog()  # 親 AU が死んだら道連れで終了 (孤児プロセスの増殖防止)
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n[main] 終了します")


if __name__ == "__main__":
    main()
