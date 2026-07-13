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
AUDIO_SAMPLE_RATE = 24000  # Live API の出力 PCM は 24kHz 16bit mono
FILLER_INTERVAL_SEC = 27.0  # 無音がこれだけ続いたら場繋ぎを一言 (コスト対策で控えめ)
MIN_SEND_GAP_SEC = 3.0      # 連続送信の最小間隔
BATCH_WINDOW_SEC = 1.0      # イベントをまとめる窓
CHAT_EVENT_MAX_AGE_SEC = 30.0  # 古すぎるチャットは実況しない

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
"""


def load_persona() -> str:
    if PERSONA_FILE.exists():
        return PERSONA_FILE.read_text(encoding="utf-8")
    return DEFAULT_PERSONA


# ---- 音声再生 (専用スレッド。async ループをブロックしない) ----

class AudioPlayer:
    def __init__(self) -> None:
        self._q: queue.Queue[bytes | None] = queue.Queue()
        self.last_audio_time = 0.0  # monotonic。実況が「喋っている」判定に使う
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def feed(self, pcm: bytes) -> None:
        self.last_audio_time = time.monotonic()
        self._q.put(pcm)

    def is_speaking(self) -> bool:
        return not self._q.empty() or (time.monotonic() - self.last_audio_time) < 1.0

    def _run(self) -> None:
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

def format_event(ev: dict) -> str | None:
    t = ev.get("type")
    if t == "join":
        return f"【イベント】プレイヤー「{ev.get('name', '?')}」がロビーに参加した。名前を呼んで歓迎して。"
    if t == "leave":
        return f"【イベント】プレイヤー「{ev.get('name', '?')}」が退出した。軽く見送って。"
    if t == "intervention":
        return (f"【イベント】視聴者「{ev.get('author', '?')}」がコマンドで"
                f"「{ev.get('kindName', ev.get('kind', '?'))}」を発動した！大きくリアクションして、"
                f"発動した視聴者の名前を呼んで褒めて。")
    if t == "demo":
        return (f"【イベント】自動デモで「{ev.get('kindName', ev.get('kind', '?'))}」の演出が再生された。"
                f"視聴者もチャットに !コマンドを打てば同じことを起こせると宣伝して。")
    if t == "chat":
        text = str(ev.get("text", "")).strip()
        # !コマンドは intervention イベント側で実況するので二重反応を避ける
        if not text or text.startswith(("!", "！")):
            return None
        if time.monotonic() - ev.get("_recv", 0) > CHAT_EVENT_MAX_AGE_SEC:
            return None
        return f"【視聴者チャット】{ev.get('author', '?')}: {text[:80]} — 一言だけ軽く反応して。"
    if t == "phase":
        phase = ev.get("phase")
        return {
            "lobby": "【イベント】ロビーに戻った。次の試合の参加者を募集するアナウンスをして。",
            "ingame": "【イベント】試合が始まった！盛り上げて。",
            "meeting": "【イベント】緊急会議が始まった。誰が怪しいかワクワクするコメントをして。",
            "ended": "【イベント】試合が終了した。お疲れさまの一言を。",
        }.get(phase)
    return None


FILLER_PROMPT = ("【場繋ぎ】しばらく何も起きていない。配信を見ている人に向けて、"
                 "参加募集か !コマンドの紹介か雑談を、1文だけ短く。")
GREETING_PROMPT = "【イベント】配信の実況を開始した。視聴者に向けて短くオープニングの挨拶をして。"


# ---- Gemini Live セッション本体 ----

async def run_session(client: genai.Client, model: str, events: asyncio.Queue,
                      player: AudioPlayer, first_connect: bool) -> None:
    config = types.LiveConnectConfig(
        response_modalities=["AUDIO"],
        system_instruction=load_persona(),
    )
    async with client.aio.live.connect(model=model, config=config) as session:
        print(f"[live] 接続完了 (model={model})")
        turn_open = False
        last_send = 0.0

        async def send_text(text: str) -> None:
            nonlocal turn_open, last_send
            print(f"[live] 送信: {text.splitlines()[0][:60]}")
            await session.send_client_content(
                turns=types.Content(role="user", parts=[types.Part(text=text)]),
                turn_complete=True,
            )
            turn_open = True
            last_send = time.monotonic()

        async def receiver() -> None:
            nonlocal turn_open
            while True:
                async for resp in session.receive():
                    if resp.data:
                        player.feed(resp.data)
                    sc = getattr(resp, "server_content", None)
                    if sc is not None and getattr(sc, "turn_complete", False):
                        turn_open = False

        recv_task = asyncio.create_task(receiver())
        try:
            if first_connect:
                await send_text(GREETING_PROMPT)

            last_activity = time.monotonic()
            while True:
                # イベントを最大 BATCH_WINDOW 秒ぶんまとめる
                batch: list[str] = []
                try:
                    ev = await asyncio.wait_for(events.get(), timeout=1.0)
                    deadline = time.monotonic() + BATCH_WINDOW_SEC
                    while True:
                        line = format_event(ev)
                        if line:
                            batch.append(line)
                        remain = deadline - time.monotonic()
                        if remain <= 0:
                            break
                        try:
                            ev = await asyncio.wait_for(events.get(), timeout=remain)
                        except asyncio.TimeoutError:
                            break
                except asyncio.TimeoutError:
                    pass

                now = time.monotonic()
                if batch:
                    # 喋り終わるまで待ってから送る (発話の被り/自己中断防止)
                    while turn_open or player.is_speaking() or (now - last_send) < MIN_SEND_GAP_SEC:
                        await asyncio.sleep(0.3)
                        now = time.monotonic()
                    await send_text("\n".join(batch[-5:]))  # 溜まりすぎたら新しい5件だけ
                    last_activity = now
                elif (now - last_activity) > FILLER_INTERVAL_SEC and not turn_open and not player.is_speaking():
                    await send_text(FILLER_PROMPT)
                    last_activity = now
        finally:
            recv_task.cancel()


async def main_async(args: argparse.Namespace) -> None:
    api_key = os.environ.get("GEMINI_API_KEY")
    if not api_key:
        print("環境変数 GEMINI_API_KEY が設定されていません。Google AI Studio で発行して設定してください。")
        sys.exit(1)

    events_path = Path(args.events)
    print(f"[main] events: {events_path}")
    if not events_path.exists():
        print("[main] イベントファイルがまだ無いので、mod 側の起動を待ちます (EnableAICommentary=true を確認)")

    client = genai.Client(api_key=api_key)
    events: asyncio.Queue = asyncio.Queue()
    player = AudioPlayer()
    asyncio.create_task(tail_events(events_path, events))

    first = True
    backoff = 2.0
    while True:
        started = time.monotonic()
        try:
            await run_session(client, args.model, events, player, first)
        except Exception as ex:
            print(f"[live] セッション終了/エラー: {ex}")
        first = False
        # 長く生きていたら backoff リセット (Live API のセッション時間上限による切断は正常系)
        backoff = 2.0 if time.monotonic() - started > 60 else min(backoff * 2, 60.0)
        print(f"[live] {backoff:.0f} 秒後に再接続します")
        await asyncio.sleep(backoff)


def main() -> None:
    parser = argparse.ArgumentParser(description="EndKnot AI実況相棒アプリ")
    parser.add_argument("--events", required=True,
                        help="companion-events.jsonl のパス (Among Us フォルダ/EndKnot_DATA/ 配下)")
    parser.add_argument("--model", default=DEFAULT_MODEL,
                        help=f"Gemini Live モデル名 (default: {DEFAULT_MODEL}、環境変数 GEMINI_LIVE_MODEL でも上書き可)")
    args = parser.parse_args()
    try:
        asyncio.run(main_async(args))
    except KeyboardInterrupt:
        print("\n[main] 終了します")


if __name__ == "__main__":
    main()
