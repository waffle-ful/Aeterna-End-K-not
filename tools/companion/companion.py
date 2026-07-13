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

# ---- 設定 (CLI/環境変数で上書き可) ----

DEFAULT_MODEL = os.environ.get("GEMINI_LIVE_MODEL", "gemini-2.5-flash-native-audio-preview-09-2025")
DEFAULT_VOICE = os.environ.get("GEMINI_VOICE", "")  # 空 = モデル既定ボイス
AUDIO_SAMPLE_RATE = 24000  # Live API の出力 PCM は 24kHz 16bit mono
FILLER_INTERVAL_SEC = 27.0  # 無音がこれだけ続いたら場繋ぎを一言 (コスト対策で控えめ)
MIN_SEND_GAP_SEC = 3.0      # 連続送信の最小間隔
BATCH_WINDOW_SEC = 1.0      # イベントをまとめる窓
CHAT_EVENT_MAX_AGE_SEC = 30.0  # 古すぎるチャットは実況しない
SUBTITLE_CLEAR_AFTER_SEC = 8.0  # 発話終了からこれだけ経ったら字幕を消す
SUMMARY_MAX_LINES = 12  # 再接続時に引き継ぐ「あらすじ」の行数

PERSONA_FILE = Path(__file__).with_name("persona.txt")

DEFAULT_PERSONA = """\
あなたは Among Us の視聴者参加型ライブ配信の AI 実況者です。
- 明るく元気な日本語で、1回の発話は1〜3文の短さで話す。
- ロビーに参加者が来たら名前を呼んで歓迎する。
- 視聴者がチャットの !コマンド (「!大地震」「!隕石」など) で
  ゲームに干渉できることを、折に触れて楽しそうに宣伝する。
- イベント情報は【イベント】などのタグ付きテキストで届く。タグや記号は読み上げず、
  内容に自然にリアクションだけする。
- 同じ挨拶や同じ宣伝文句を繰り返さない。言い回しを毎回変える。
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
    def __init__(self) -> None:
        self.phase = "lobby"           # 最後に受けた phase イベント
        self.recap: deque[str] = deque(maxlen=SUMMARY_MAX_LINES)  # 再接続用あらすじ
        self.last_real_event = time.monotonic()  # filler 休眠判定 (実イベントのみ更新)

    def note(self, line: str) -> None:
        """あらすじ用の1行メモ (【】タグは付けない素の日本語)。"""
        self.recap.append(line)

    def recap_text(self) -> str:
        if not self.recap:
            return ""
        return "直前までの配信の流れ (参考情報。読み上げず、文脈として把握だけすること):\n- " + "\n- ".join(self.recap)


# ---- 音声再生 (専用スレッド。async ループをブロックしない) ----

class AudioPlayer:
    def __init__(self, device: int | str | None = None) -> None:
        self._q: queue.Queue[bytes | None] = queue.Queue()
        self._device = device  # None = 既定デバイス。仮想ケーブル (VB-Audio 等) に出すと立ち絵アプリへ直結できる
        self.last_audio_time = 0.0  # monotonic。実況が「喋っている」判定に使う
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
            except Exception as ex:  # デバイス断などで実況全体を殺さない
                print(f"[audio] 再生エラー: {ex}")
                time.sleep(0.5)


# ---- OBS 連携ファイル (字幕 / 口パク状態) ----

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
        self._write(self.subtitle_path, self._subtitle_text.strip())

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
        return (f"【イベント】自動デモで「{ev.get('kindName', ev.get('kind', '?'))}」の演出が再生された。"
                f"視聴者もチャットに !コマンドを打てば同じことを起こせると宣伝して。")
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


FILLER_PROMPT = ("【場繋ぎ】しばらく何も起きていない。配信を見ている人に向けて、"
                 "参加募集か !コマンドの紹介か雑談を、1文だけ短く。"
                 "実在する !コマンドは「!大地震」「!隕石」「!停電」「!リアクター」「!通信」"
                 "「!ドア」「!呪い」「!祝福」「!天の声」「!偽死体」だけ。"
                 "この中から紹介し、存在しないコマンド名を作らないこと。")
GREETING_PROMPT = "【イベント】配信の実況を開始した。視聴者に向けて短くオープニングの挨拶をして。"


# ---- Gemini Live セッション本体 ----

async def run_session(client: genai.Client, args: argparse.Namespace, events: asyncio.Queue,
                      player: AudioPlayer, obs: ObsFiles, state: StreamState,
                      first_connect: bool, conv: ConvLog) -> None:
    system = load_persona()
    recap_used = ""
    if not first_connect:
        recap = state.recap_text()
        if recap:
            system = system + "\n\n" + recap  # 再接続の記憶喪失対策 (読み上げさせない為 system 側に載せる)
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
        print(f"[live] 接続完了 (model={args.model}" + (f", voice={args.voice}" if args.voice else "") + ")")
        # 再接続は文脈喪失 (=意味不明発言) の第一容疑なので、引き継いだあらすじごと区切りを記録する
        conv.log("session", first=first_connect, model=args.model, recap=recap_used)
        turn_open = False
        last_send = 0.0
        say_buf = ""  # 現在の発話の transcript 蓄積 (turn_complete でログへ吐く)

        def flush_say() -> None:
            nonlocal say_buf
            if say_buf.strip():
                conv.log("say", text=say_buf.strip())
            say_buf = ""

        async def send_text(text: str) -> None:
            nonlocal turn_open, last_send
            print(f"[live] 送信: {text.splitlines()[0][:60]}")
            flush_say()  # turn_complete を取りこぼした場合でも send 前に前の発話を確定させる
            conv.log("send", text=text)
            obs.new_utterance()
            await session.send_client_content(
                turns=types.Content(role="user", parts=[types.Part(text=text)]),
                turn_complete=True,
            )
            turn_open = True
            last_send = time.monotonic()

        async def receiver() -> None:
            nonlocal turn_open, say_buf
            while True:
                async for resp in session.receive():
                    if resp.data:
                        player.feed(resp.data)
                    sc = getattr(resp, "server_content", None)
                    if sc is not None:
                        tr = getattr(sc, "output_transcription", None)
                        if tr is not None and getattr(tr, "text", None):
                            obs.feed_transcript(tr.text)
                            say_buf += tr.text
                        if getattr(sc, "turn_complete", False):
                            turn_open = False
                            flush_say()

        async def obs_ticker() -> None:
            while True:
                obs.tick(player.is_speaking())
                await asyncio.sleep(0.2)

        recv_task = asyncio.create_task(receiver())
        obs_task = asyncio.create_task(obs_ticker())
        try:
            if first_connect:
                await send_text(GREETING_PROMPT)

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
                for ev in merge_joins(raw):
                    if ev.get("type") == "_joins":
                        batch.append(format_merged_joins(ev, state))
                        continue
                    line = format_event(ev, state, args.quiet_meeting)
                    if line:
                        batch.append(line)
                if raw:
                    state.last_real_event = time.monotonic()  # filler 休眠の解除 (chat/join 含む全実イベント)

                now = time.monotonic()
                if batch:
                    # 喋り終わるまで待ってから送る (発話の被り/自己中断防止)
                    while turn_open or player.is_speaking() or (now - last_send) < MIN_SEND_GAP_SEC:
                        await asyncio.sleep(0.3)
                        now = time.monotonic()
                    await send_text("\n".join(batch[-5:]))  # 溜まりすぎたら新しい5件だけ
                    last_activity = now
                elif (now - last_activity) > FILLER_INTERVAL_SEC and not turn_open and not player.is_speaking():
                    if args.quiet_meeting and state.phase == "meeting":
                        continue  # 会議中は場繋ぎしない
                    if (now - state.last_real_event) > args.dormant_after:
                        continue  # 誰も居ない・何も起きない時間帯はトークンを焼かない (休眠)
                    await send_text(FILLER_PROMPT)
                    last_activity = now
        finally:
            recv_task.cancel()
            obs_task.cancel()
            flush_say()


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
    state = StreamState()
    asyncio.create_task(tail_events(events_path, events))

    first = True
    backoff = 2.0
    while True:
        started = time.monotonic()
        try:
            await run_session(client, args, events, player, obs, state, first, conv)
        except Exception as ex:
            print(f"[live] セッション終了/エラー: {ex}")
            conv.log("session_end", error=str(ex))
        first = False
        # 長く生きていたら backoff リセット (Live API のセッション時間上限による切断は正常系)
        backoff = 2.0 if time.monotonic() - started > 60 else min(backoff * 2, 60.0)
        print(f"[live] {backoff:.0f} 秒後に再接続します")
        await asyncio.sleep(backoff)


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
    parser.add_argument("--model", default=DEFAULT_MODEL,
                        help=f"Gemini Live モデル名 (default: {DEFAULT_MODEL}、環境変数 GEMINI_LIVE_MODEL でも上書き可)")
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
    parser.add_argument("--audio-device", default=os.environ.get("COMPANION_AUDIO_DEVICE", ""),
                        help="実況音声の出力先デバイス (名前の一部か番号)。仮想ケーブル (VB-Audio 等) を指定すると"
                             "アバターアプリのリップシンクに直結できる。--list-audio-devices で一覧表示。"
                             "空なら既定デバイス")
    parser.add_argument("--list-audio-devices", action="store_true",
                        help="音声デバイスの一覧を表示して終了")
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n[main] 終了します")


if __name__ == "__main__":
    main()
