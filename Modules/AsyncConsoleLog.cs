using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BepInEx;
using BepInEx.Logging;

namespace EndKnot.Modules;

// BepInEx の ConsoleLogListener をノンブロッキング版に差し替える。
//
// 素の ConsoleLogListener はメインスレッドから conhost へ同期書き込みする。conhost はテキストが
// 選択されている間 WriteFile を無期限ブロックするため、選択中に1行吐いた瞬間メインスレッドごと
// 停止し「ハング」として観測される。ConsoleGuard は選択そのものを潰す対策
// (予防+自動復帰) だが、こちらは「コンソールが何で詰まってもメインスレッドは巻き込まれない」
// という構造側の根治にあたる。両方入れて二重化する。
//
// 設計上の要点:
//   - LogEvent はキューに積むだけで絶対に待たない。キューが満杯なら**捨てる**
//     (ログ1行を守るためにゲームを止めるのは本末転倒)。
//   - 書き込みは専用スレッド1本。ここが conhost にブロックされても被害はこのスレッドだけ。
//   - ConsoleManager.ConsoleStream は DetachConsole/CreateConsole で差し替わるので毎回読み直す。
public sealed class AsyncConsoleLog : ILogListener
{
    // 数千行たまる時点でコンソールは死んでいる。上限を超えた分は捨てて前に進む。
    private const int Capacity = 4096;

    private static AsyncConsoleLog _installed;
    private static int _dropped;

    private readonly BlockingCollection<(string Line, LogLevel Level)> _queue = new(new ConcurrentQueue<(string, LogLevel)>(), Capacity);

    private readonly LogLevel _filter;

    private AsyncConsoleLog(LogLevel filter)
    {
        _filter = filter;
        var t = new Thread(WriteLoop) { IsBackground = true, Name = "EndKnot.AsyncConsoleLog" };
        t.Start();
    }

    public LogLevel LogLevelFilter => _filter;

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            // TryAdd(タイムアウト無し) なので満杯でも即座に false が返る = メインスレッドは待たない。
            if (!_queue.TryAdd((eventArgs.ToStringLine(), eventArgs.Level)))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // ログ経路の例外でゲームを巻き込まない。
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); }
        catch
        {
            // 破棄時の例外は無視。
        }
    }

    /// <summary>
    /// 既存の ConsoleLogListener を外して非同期版に差し替え、結果を人間可読の1行で返す
    /// (呼び出し元がログする)。失敗した場合は何も変更しない。
    /// </summary>
    public static string Install()
    {
        if (_installed != null) return "already installed";

        try
        {
            List<ILogListener> old = BepInEx.Logging.Logger.Listeners.Where(l => l is ConsoleLogListener).ToList();
            if (old.Count == 0) return "skipped (no ConsoleLogListener — console logging is off)";

            // 元のリスナーのフィルタを引き継ぐ (BepInEx.cfg の Logging.Console.LogLevels を尊重する)。
            LogLevel filter = old[0].LogLevelFilter;

            var async = new AsyncConsoleLog(filter);
            BepInEx.Logging.Logger.Listeners.Add(async);

            foreach (ILogListener l in old)
            {
                BepInEx.Logging.Logger.Listeners.Remove(l);
                try { l.Dispose(); }
                catch
                {
                    // 元リスナーの破棄失敗は致命ではない。
                }
            }

            _installed = async;
            return $"console logging is now async (filter={filter}, capacity={Capacity})";
        }
        catch (Exception e) { return $"install failed, keeping stock listener: {e.Message}"; }
    }

    /// <summary>捨てた行数 (計器用)。0 でなければコンソールが詰まっていた証拠になる。</summary>
    public static int DroppedLines => _dropped;

    // ⚠️ このループは BepInEx の Logger を呼んではならない (自分自身を再入させることになる)。
    // 記録が要るときは HealthLog.Note (File.AppendAllText・コンソール非経由) を使う。
    private void WriteLoop()
    {
        int reportedDrops = 0;

        foreach ((string line, LogLevel level) in _queue.GetConsumingEnumerable())
        {
            try
            {
                var stream = ConsoleManager.ConsoleStream;
                if (stream == null) continue;

                ConsoleManager.SetConsoleColor(ColorFor(level));
                stream.Write(line);
                ConsoleManager.SetConsoleColor(ConsoleColor.Gray);
            }
            catch (ObjectDisposedException)
            {
                // ⚠️ 想定内の競合: ConsoleStream を読んだ直後にメインスレッドが DetachConsole() を
                // 呼ぶと (Main.cs の AmDebugger 分岐 / ShipStatusPatch のゲーム開始ごとの
                // AllowConsole 経路)、BepInEx 側が ConsoleOut.Close() するため書き込み先が消える。
                // BepInEx は Listeners 側にロックを提供しないので、この窓は catch で受けるしかない。
                // 失うのは行き先を失った1行だけで、メインスレッドには影響しない。
            }
            catch (IOException)
            {
                // 同上 (ハンドルが閉じられた/コンソールが消えた)。
            }
            catch
            {
                // コンソールが詰まった等 — 捨てて次へ。メインスレッドには一切影響しない。
            }

            // 取りこぼしが発生していたら Health ログ側にだけ残す (コンソールには書かない)。
            int dropped = Volatile.Read(ref _dropped);
            if (dropped <= reportedDrops || dropped - reportedDrops < 100) continue;

            reportedDrops = dropped;

            // HealthLog がまだ初期化されていなければ書かない — 背景スレッドが EnsureInit() の
            // 最初の呼び出し者になると .prev への File.Move がメインスレッドと競合し、その catch が
            // EndKnot.Logger (ロック無しの共有 Dictionary) へ落ちて別種のハングを生む。
            if (!HealthLog.IsInitialized) continue;

            try { HealthLog.Note($"ASYNCCONSOLE dropped={dropped} (console was blocked or too slow)"); }
            catch
            {
                // 計器の失敗は無視。
            }
        }
    }

    private static ConsoleColor ColorFor(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0) return ConsoleColor.Red;
        if ((level & LogLevel.Error) != 0) return ConsoleColor.Red;
        if ((level & LogLevel.Warning) != 0) return ConsoleColor.Yellow;
        if ((level & LogLevel.Message) != 0) return ConsoleColor.White;
        if ((level & LogLevel.Info) != 0) return ConsoleColor.Gray;
        return ConsoleColor.DarkGray;
    }
}
