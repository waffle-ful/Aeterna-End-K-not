using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace EndKnot.Modules;

// マネージドヒープの帰属計器 (BUG-20260706-01)。2026-08-01 実測で gcMB 107→211 が Gen2 GC 21回を
// 跨いで単調増 = マネージド側の保持リークが確定したが、既存の CENSUS (Unity オブジェクト型カウント) は
// マネージドコレクションの成長に原理的に盲目だった。この計器は2つの穴を塞ぐ:
//  1. gcTrue: 強制フル GC 直後の GetTotalMemory = 未回収ゴミを含まない「真の保持量」。
//     停止を伴うため Lobby/Menu/Ended 限定 + 低頻度。pause 実測 (gcPauseMs) を添えて framestall 帰属を汚さない。
//  2. MHEAPTOP/MHEAPGROW: EndKnot アセンブリ全 static フィールドのうち Count を持つコレクション /
//     StringBuilder / delegate (イベント購読) を定期棚卸しし、上位とセッション基準の増分を Health.log に残す。
//     players=1 の放置でも増えるリークなので、単調増する「器」の名前がそのまま犯人生成元になる。
// ログのみ・送信ゼロ。走査はキャッシュ済みリフレクション読みだけなので計器自体の膨張寄与は無視できる (§7 対策)。
public static class ManagedCensus
{
    private const long SweepIntervalSeconds = 120; // static コレクション棚卸しの間隔
    private const long ForcedGcIntervalSeconds = 300; // 強制フル GC (gcTrue 測定) の間隔
    private const int MaxTrackedFields = 4000; // リフレクション列挙の暴走上限
    private const int TopCount = 25; // MHEAPTOP に載せる件数
    private const int GrowCount = 15; // MHEAPGROW に載せる件数

    private static long _lastSweepTs;
    private static long _lastForcedGcTs;

    private struct TrackedField
    {
        public string Name; // "Type.Field" (名前空間なし)
        public FieldInfo Field;
        public long Baseline; // セッション初観測時の count (-1 = 未観測)
    }

    private static TrackedField[] _fields; // 初回 sweep で一度だけ構築
    private static readonly Dictionary<Type, PropertyInfo> CountProps = []; // 実行時型 → Count getter

    // HealthLog の HB (5秒 grid) から毎回呼ばれ、間隔を満たしたときだけ実走する。
    public static void MaybeTick(long now, string state)
    {
        if (now - _lastSweepTs < SweepIntervalSeconds) return;
        _lastSweepTs = now;

        // 強制フル GC はメインスレッドを数十〜数百 ms 止めるので、ゲーム中 (InTask/Meeting) は踏まない。
        bool forceGc = now - _lastForcedGcTs >= ForcedGcIntervalSeconds && state is "Lobby" or "Menu" or "Ended";
        if (forceGc) _lastForcedGcTs = now;

        Run("timer", forceGc);
    }

    // /census コマンド用 (1-bit A/B の手動スナップショット)。手動時は状態を問わず gcTrue も測る。
    public static void RunNow(string reason)
    {
        Run(reason, forceGc: true);
    }

    private static void Run(string src, bool forceGc)
    {
        try
        {
            long now = Utils.TimeStamp;
            _fields ??= BuildFieldList();

            var sb = new StringBuilder("MHEAP t=").Append(now).Append(" src=").Append(src);
            sb.Append(" fields=").Append(_fields.Length);
            sb.Append(" gcMB=").Append(GC.GetTotalMemory(false) / (1024 * 1024));

            if (forceGc)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                    sw.Stop();
                    sb.Append(" gcTrue=").Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" gcPauseMs=").Append(sw.ElapsedMilliseconds);
                }
                catch { sb.Append(" gcTrue=?"); }
            }

            HealthLog.Note(sb.ToString());

            // 棚卸し: 全 tracked フィールドの現在 count を読み、上位と成長分を別行で残す
            var samples = new List<(string Name, long Count, long Delta)>(_fields.Length);

            for (int i = 0; i < _fields.Length; i++)
            {
                long count = ReadCount(ref _fields[i]);
                if (count < 0) continue;

                if (_fields[i].Baseline < 0) _fields[i].Baseline = count;
                samples.Add((_fields[i].Name, count, count - _fields[i].Baseline));
            }

            var top = new StringBuilder("MHEAPTOP t=").Append(now).Append(" src=").Append(src).Append(' ');
            foreach ((string name, long count, _) in samples.OrderByDescending(x => x.Count).Take(TopCount))
                top.Append(name).Append('=').Append(count).Append(' ');
            HealthLog.Note(top.ToString().TrimEnd());

            // 成長行が本命: セッション基準で単調に増え続ける器 = 保持リークの犯人候補。
            var grow = new StringBuilder("MHEAPGROW t=").Append(now).Append(" src=").Append(src).Append(' ');
            int growN = 0;

            foreach ((string name, long count, long delta) in samples.Where(x => x.Delta > 0).OrderByDescending(x => x.Delta).Take(GrowCount))
            {
                grow.Append(name).Append("=+").Append(delta).Append('/').Append(count).Append(' ');
                growN++;
            }

            HealthLog.Note(growN > 0 ? grow.ToString().TrimEnd() : $"MHEAPGROW t={now} src={src} none");
        }
        catch (Exception e) { Logger.Warn($"managed census failed: {e.Message}", "ManagedCensus"); }
    }

    private static long ReadCount(ref TrackedField tf)
    {
        try
        {
            object value = tf.Field.GetValue(null);
            if (value == null) return -1;

            switch (value)
            {
                case StringBuilder s:
                    return s.Length;
                case Delegate d:
                    return d.GetInvocationList().Length;
            }

            // フィールド宣言型でなく実行時型から Count を引く (IList/interface 宣言のフィールド対応)
            Type rt = value.GetType();

            if (!CountProps.TryGetValue(rt, out PropertyInfo prop))
            {
                prop = rt.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.PropertyType != typeof(int)) prop = null;
                CountProps[rt] = prop;
            }

            return prop != null ? (int)prop.GetValue(value) : -1;
        }
        catch { return -1; } // 並行変更・未初期化 cctor 等はこの回スキップ (次回に取れれば十分)
    }

    private static TrackedField[] BuildFieldList()
    {
        var list = new List<TrackedField>(1024);

        try
        {
            Type[] types;
            try { types = typeof(ManagedCensus).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

            foreach (Type t in types)
            {
                // open generic の static フィールドは GetValue 不可。自分自身は自己観測ノイズなので除外。
                if (t.ContainsGenericParameters || t == typeof(ManagedCensus)) continue;

                FieldInfo[] fields;
                try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }

                foreach (FieldInfo f in fields)
                {
                    if (f.IsLiteral || !Qualifies(f.FieldType)) continue;

                    // 自動プロパティの backing field 名 "<Prop>k__BackingField" を Prop に整形
                    string fname = f.Name;
                    if (fname.Length > 2 && fname[0] == '<')
                    {
                        int close = fname.IndexOf('>');
                        if (close > 1) fname = fname[1..close];
                    }

                    list.Add(new TrackedField { Name = $"{t.Name}.{fname}", Field = f, Baseline = -1 });
                    if (list.Count >= MaxTrackedFields) return list.ToArray();
                }
            }
        }
        catch (Exception e) { Logger.Warn($"field list build failed: {e.Message}", "ManagedCensus"); }

        Logger.Info($"tracking {list.Count} static collection/delegate fields", "ManagedCensus");
        return list.ToArray();
    }

    // 追跡対象の型か: 成長しうるマネージド容器だけ拾う。配列は固定長なので対象外。
    // Il2Cpp コレクション proxy は managed の Count パターンに合致しないため自然に外れる (interop 呼びを踏まない)。
    private static bool Qualifies(Type ft)
    {
        try
        {
            if (ft == typeof(StringBuilder)) return true;
            if (typeof(MulticastDelegate).IsAssignableFrom(ft)) return true;
            if (ft.IsArray || ft.IsPointer || ft.IsPrimitive || ft == typeof(string)) return false;
            if (ft.Namespace != null && ft.Namespace.StartsWith("Il2Cpp", StringComparison.Ordinal)) return false;

            PropertyInfo count = ft.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            return count != null && count.PropertyType == typeof(int);
        }
        catch { return false; }
    }
}
