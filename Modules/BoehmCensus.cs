using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// il2cpp (Boehm) 側の生存オブジェクトを型別に帰属する計器。GcPrepass.BoehmUsedBytes が
// 40MB→229MB→352MB とセッション中単調増していて Ended 状態の 500-700ms ストールがこれに比例する一方、
// CENSUS (Unity オブジェクト型カウント) と MHEAP (マネージド static コレクション棚卸し) はどちらも
// il2cpp 生存オブジェクトの型別内訳に原理的に盲目 — Unity のメモリプロファイラ / UnloadUnusedAssets と
// 同じ liveness 計算 (il2cpp_unity_liveness_*) を直接叩いて型別ヒストグラムを取る。
//
// GameAssembly.dll には begin/end 版 (il2cpp_unity_liveness_calculation_begin/end) が無く、
// allocate_struct/from_root/finalize/free_struct の新 API のみが実在する。
//
// il2cpp vm/Liveness.cpp の実装契約:
//  - allocate_struct 冒頭の Class::SetupTypeHierarchy(filter) が filter を無条件に逆参照するため、
//    filter に IntPtr.Zero は渡せない (NULL+0xc8 の AV になる) — 全型を対象にしたい場合でも
//    System.Object の Il2CppClass を渡す (全型が HasParent(Object) を満たす)。
//  - calculation_from_statics は使わない。代わりに calculation_from_root(root, state) をルート単位で
//    繰り返し呼ぶ。FromRoot は呼ぶ度に内部の process_array だけをリセットし、到達済みオブジェクト集合
//    (all_objects) とマークは呼び出しを跨いで累積するため、同じ state に対して何度呼んでもよく、
//    finalize/free_struct は全ルート走査が終わった後に1回で足りる。
//  - 走査は各オブジェクトを `obj->klass |= 1` でマークする。マーク解除は il2cpp_unity_liveness_finalize
//    が全 収集済みオブジェクトへ CLEAR_OBJ を適用するまで起きない。register_object_callback は
//    マーク解除 "前" に呼ばれるため、コールバック内で il2cpp_object_get_class/get_size 等クラス
//    ポインタを読む API を呼ぶと、下位ビットが立った klass をそのまま逆参照して AV する。
//    → コールバックはポインタを保存するだけに留め、クラス解決/サイズ取得は finalize 完了後に行う。
//    同じ理由で、ルート走査ループ中にオブジェクトの実行時型名を出す場合 (BCENSUSROOT の obj=) は、
//    そのオブジェクトが直前までのルート走査で既にマーク済みの可能性があるため、klass の下位ビットを
//    落としてから名前解決する。
//  - この API はワールド停止も GC 停止もしない (呼び出し側の責務)。マークは走査中ずっと対象オブジェクトの
//    klass (vtable 参照元) を汚染するため、ワールドを止めずに他スレッド (入力処理等) がその間に汚染された
//    klass 経由で仮想呼び出しをすると無音でプロセスが落ちる (非決定的・犠牲オブジェクトはランダムに見える
//    — 実機で複数回確認)。Unity 本体はワールド停止つきでこの API を呼んでいるため、allocate_struct〜
//    finalize の区間は il2cpp_stop_gc_world/il2cpp_start_gc_world で確実に挟む。CLR 側も
//    GC.TryStartNoGCRegion で同じ窓の間だけ GC を止め、ワールド停止中に不要な一時停止・割当・ファイル
//    I/O が発生しないようにする (per-root のログ行は StringBuilder に溜めてワールド再開後にまとめて
//    書き出す — File.AppendAllText を毎行呼ぶと停止中の I/O レイテンシがそのまま停止時間に乗る)。
//  - reallocate callback は「新規確保 (ptr=null)」と「解放 (size=0)」の2パターンしか呼ばれず、内部バッファは
//    8KB (LivenessBlockSize) 固定ブロック単位でしか確保されない。ワールド停止中にも呼ばれる経路なので
//    Dictionary 等の割当を伴う収支管理は避け、確保回数/解放回数/確保バイト合計の3本のカウンタだけを
//    更新する — 解放済みバイト数は「解放回数×8KB」で正確に近似できる。
//
// ルート源は2系統ある:
//  1. static フィールド (EnumerateRoots) — ドメイン→アセンブリ→イメージ→クラス→フィールドを自前で辿る。
//  2. GCHandle (il2cpp_gchandle_foreach_get_target) — mod 側/Unity ネイティブ側が il2cpp_gchandle_new 等で
//     直接保持しているだけの (どの static からも参照されていない) オブジェクトは (1) に原理的に現れない。
//     実測でヒープの大半 (usedMB=253 のうち statics 到達分は 20MB) がこの経路だったため追加した。
//     停止中はまず static ルートを全部 from_root し、その時点の _bufferCount を境界として記録した上で
//     GCHandle ルートを続けて from_root する — 既に static 経由でマーク済みのオブジェクトは liveness 内部
//     のマークチェックで自動的に再訪問されない (register_object_callback が呼ばれない) ため、境界の前後で
//     単純に「static 到達分」「GCHandle のみ到達分」に分類できる。GCHandle ルートは数万件になりうるため
//     per-root ログは出さず、件数のみ記録する。
//
// ルート列挙: il2cpp のドメイン→アセンブリ→イメージ→クラス→フィールドを自前で辿り、static かつ
// literal でない、thread-static でないフィールドのうち参照型 (CLASS/STRING/OBJECT/ARRAY/SZARRAY、
// GENERICINST は解決先クラスが値型でない場合のみ) で非null の値をルート候補にする。値型 static
// (構造体) は対象外 (件数のみ記録)。corlib (mscorlib) の image は il2cpp 本体の内部状態なのでスキップする。
//
// Stage は「どこまでのルート集合で試すか」の段階ゲート。プロセス無音終了の再現条件を絞り込むための
// ものなので設定ではなくコード定数で切り替える。1=合成ルート1個で機構疎通確認、2=自前列挙の先頭50件、
// 3=全ルート。GCHandle ルートは Stage に関係なく常に全件を対象にする。
//
// 制限: 到達できるのは列挙できたルートから辿れるグラフのみ。
public static class BoehmCensus
{
    private static readonly int Stage = 3;
    private const int Stage2RootLimit = 50;

    private const int TopCount = 25;
    private const int MaxObjectCountHint = 1 << 20; // native 側の内部バッファ確保には使われないヒント値
    private const long InitialBufferCapacity = 1 << 20; // ポインタ保存用バッファの初期容量(8MB on x64)

    private const long LivenessBlockSize = 8192; // liveness 内部バッファの固定確保単位
    private const long NoGCRegionBytes = 64L << 20; // ワールド停止窓の間だけ CLR GC を止める予約量
    private const int BufferCapacityHint = 256 * 1024; // BCENSUSROOT/SUB 行バッファの初期容量

    private const int FieldAttributeStatic = 0x0010;
    private const int FieldAttributeLiteral = 0x0040;

    // Il2CppTypeEnum の参照型側の値 (il2cpp-runtime-metadata.h)。
    private const int TypeString = 14;
    private const int TypeClass = 18;
    private const int TypeArray = 20;
    private const int TypeGenericInst = 21;
    private const int TypeObject = 28;
    private const int TypeSzArray = 29;

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr il2cpp_unity_liveness_allocate_struct(IntPtr filter, int max_object_count, IntPtr callback, IntPtr userdata, IntPtr reallocate);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_unity_liveness_finalize(IntPtr state);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_unity_liveness_free_struct(IntPtr state);

    // IL2CPP ラッパーに rank 取得が無いため直接 DllImport する。elemClass != klass による配列判定は
    // enum (backing type を持つため element_class が非null) を配列と誤表示する罠があった。
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern int il2cpp_class_get_rank(IntPtr klass);

    // Class::Init が未実行 (static 領域未確保) なクラスは static_fields が NULL のまま。
    // il2cpp_field_static_get_value はこのチェックをせず NULL+offset を読むため、フィールド読み出し前に
    // 自前でガードする必要がある。IL2CPP ラッパーに無いため直接 DllImport する。
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr il2cpp_class_get_static_field_data(IntPtr klass);

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_disable();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_enable();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gc_collect(int maxGenerations);

    // マーク走査中は他スレッドの仮想呼び出しが汚染された klass を踏むため、allocate_struct〜finalize の
    // 区間はワールドを止める。Unity 本体の liveness 計算はこの対で呼ばれている。
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_stop_gc_world();

    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_start_gc_world();

    // 第2のルート源: 生存中の全 GCHandle のターゲットを列挙する。static から辿れない (mod/ネイティブ側が
    // ハンドルだけで保持している) オブジェクトはこれでしか拾えない。
    [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
    private static extern void il2cpp_gchandle_foreach_get_target(IntPtr func, IntPtr userData);

    private static bool _disabled; // エクスポート欠落/native 側失敗を確認済みの環境では以後スキップする
    private static bool _loggedMissing;

    // register_object_callback がクラス別に集計する先。finalize 後の分類フェーズでのみ書き込む。
    // ClassCounts = 総計、HandleOnlyClassCounts = GCHandle ルートでしか到達しなかった分のみ。
    private static readonly Dictionary<IntPtr, (long Count, long Bytes)> ClassCounts = new(4096);
    private static readonly Dictionary<IntPtr, (long Count, long Bytes)> HandleOnlyClassCounts = new(4096);

    // コールバックはマーク中で il2cpp API を呼べないため、生ポインタをここへ memcpy するだけに留める。
    // Run 間で使い回す (毎回 free/alloc しない) native メモリ。マネージド割当はゼロ。
    private static IntPtr _buffer = IntPtr.Zero;
    private static long _bufferCapacity; // ポインタ単位の容量
    private static long _bufferCount;
    private static long _droppedCount; // 成長に失敗して記録できなかった件数
    private static bool _bufferGrowFailed;
    private static long _bufferBoundary; // この位置より前 = static 到達分、以降 = GCHandle のみ到達分

    // GCHandle ルート (from_root に渡す対象ポインタ) を溜める専用の native バッファ。_buffer と同じ
    // 成長方式だが別インスタンス — こちらは「訪問済みオブジェクト」でなく「これから訪問するルート」を持つ。
    private static IntPtr _handleBuffer = IntPtr.Zero;
    private static long _handleBufferCapacity;
    private static long _handleBufferCount;
    private static long _handleDroppedCount;
    private static bool _handleBufferGrowFailed;

    // reallocate callback の確保/解放収支。free_struct が内部バッファを全て返却しているかの自己診断に使う
    // (セッション開始からの累積 — 単発の Run では 0 付近で揺れるだけなので、複数回の Run を跨いだ
    // 単調増加だけが「native 側が返却し損ねている」の証拠になる)。ワールド停止中にも呼ばれる経路なので
    // Dictionary は使わず (割当源になる)、カウンタ3本だけを更新する。
    private static long _reallocAllocCount;
    private static long _reallocFreeCount;
    private static long _reallocAllocBytes;

    // RegisterObjectCallback の呼出回数/受信件数。段階マーカー(BCENSUSMARK)の finalize 行に添える
    // だけのカウンタインクリメント (割当なし)。
    private static long _cbCallCount;
    private static long _cbRecvCount;

    // Rewired.ReInput の static (ControllerHelper) を from_root に渡すと無音終了する実機事例があった。
    // 入力ライブラリ内部の生存グラフはこの計器の対象外にする。
    private static readonly string[] ExcludedRootNamespaces = { "Rewired" };

    // AmongUsClient.Instance / ShipStatus.Instance の from_root で無音終了する実機事例があった。原因
    // フィールドを特定するため、これらの型はオブジェクト自体を丸ごとルートにせず、インスタンスフィールドを
    // 1段展開して個別に from_root する。ControllerHelper は Rewired 名前空間除外の対象外 (宣言側が別
    // 名前空間のケースがある) なので、こちらでも明示的に挙げておく。
    private static readonly string[] SplitRootTypes = { "AmongUsClient", "ControllerHelper", "ShipStatus" };

    private readonly record struct RootInfo(IntPtr ObjPtr, string ClsName, string FieldName, string AsmName);

    // /census と同じ発火点 (MemCensus.Run) から呼ばれる。エクスポート欠落は一度だけログして以後無効化する。
    public static void RunNow(string src)
    {
        if (_disabled) return;

        try
        {
            Run(src);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            _disabled = true;
            HealthLog.Note($"BCENSUSMARK t={Utils.TimeStamp} src={src} phase=fail reason=missing_export");
            if (!_loggedMissing)
            {
                _loggedMissing = true;
                Logger.Warn($"BoehmCensus disabled (missing export/module): {e.Message}", "BoehmCensus");
            }
        }
        catch (Exception e)
        {
            HealthLog.Note($"BCENSUSMARK t={Utils.TimeStamp} src={src} phase=fail reason={e.GetType().Name}");
            Logger.Warn($"boehm census failed: {e.Message}", "BoehmCensus");
        }
    }

    private static void Run(string src)
    {
        HealthLog.NoteOp("BoehmCensus");
        long now = Utils.TimeStamp;
        var sw = Stopwatch.StartNew();

        // filter=NULL は allocate_struct 冒頭の SetupTypeHierarchy(filter) が即座に落とすため、
        // 全型を対象にする代わりに System.Object のクラスポインタを渡す。
        IntPtr filterClass = Il2CppClassPointerStore<Il2CppSystem.Object>.NativeClassPtr;
        if (filterClass == IntPtr.Zero)
        {
            Logger.Warn("BoehmCensus: System.Object native class pointer unavailable, skipping", "BoehmCensus");
            return;
        }

        ClassCounts.Clear();
        HandleOnlyClassCounts.Clear();
        ResetBuffer();
        ResetHandleBuffer();
        _bufferBoundary = 0;
        _cbCallCount = 0;
        _cbRecvCount = 0;

        IntPtr state = IntPtr.Zero;
        bool finalized = false;
        long clrGcMs = 0;
        bool nogc = false;

        // 各段階の直前に1行フラッシュする。HealthLog.Note は行毎に flush されるので、この直後で
        // プロセスが無音終了しても最後に書けた行が「どこまで進んだか」の直接証拠になる。
        void Mark(string phase) => HealthLog.Note($"BCENSUSMARK t={now} src={src} phase={phase} ms={sw.ElapsedMilliseconds}");

        // Il2CppInterop ラッパーのファイナライザが GCHandle を解放するのを待ってから Boehm GC 側に
        // 入る。TryStartNoGCRegion (この後、ワールド停止直前) より前なので CLR 側の一時停止と競合しない。
        {
            var clrSw = Stopwatch.StartNew();
            try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } catch { }
            clrSw.Stop();
            clrGcMs = clrSw.ElapsedMilliseconds;
        }
        HealthLog.Note($"BCENSUSMARK t={now} src={src} phase=clrgc ms={clrGcMs}");

        // マーク走査に混ざる進行中の incremental 収集を先に終わらせ、ガベージを対象から外しておく
        // (GcPrepass.Collect と同じ流儀)。この直後に GC を止めるので二重に安全側へ倒す。
        Mark("gccollect");
        try { il2cpp_gc_collect(2); } catch { }
        Mark("gcdisable");
        try { il2cpp_gc_disable(); } catch { }

        try
        {
            IntPtr callback;
            IntPtr reallocate;
            IntPtr handleCallback;

            unsafe
            {
                callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr*, int, IntPtr, void>)&RegisterObjectCallback;
                reallocate = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, IntPtr, IntPtr>)&ReallocateCallback;
                handleCallback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&GcHandleForeachCallback;
            }

            Mark("enum");

            // ルート列挙・分割展開・表示文字列の組み立ては全てワールド停止前に済ませる。停止中に残るのは
            // from_root 呼び出しと StringBuilder への追記だけにする。
            List<RootInfo> roots;
            int enumerated = 0;
            int valueTypeSkipped = 0;
            int noStatics = 0;
            int genericDef = 0;
            int excluded = 0;

            if (Stage == 1)
            {
                var synthetic = new Il2CppSystem.Object();
                IntPtr syntheticPtr = IL2CPP.Il2CppObjectBaseToPtrNotNull(synthetic);
                roots = new List<RootInfo> { new(syntheticPtr, "Il2CppSystem.Object", "<synthetic>", "<synthetic>") };
                enumerated = 1;
            }
            else
            {
                roots = EnumerateRoots(src, now, out enumerated, out valueTypeSkipped, out noStatics, out genericDef, out excluded);
                if (Stage == 2 && roots.Count > Stage2RootLimit) roots = roots.GetRange(0, Stage2RootLimit);
            }

            var workItems = new List<(IntPtr CallPtr, string Line, string Label)>(roots.Count + 64);

            for (int i = 0; i < roots.Count; i++)
            {
                RootInfo r = roots[i];

                IntPtr rootKlassRaw = IL2CPP.il2cpp_object_get_class(r.ObjPtr);
                IntPtr rootKlass = rootKlassRaw == IntPtr.Zero ? IntPtr.Zero : (IntPtr)(rootKlassRaw.ToInt64() & ~1L);
                string objType = rootKlass == IntPtr.Zero ? "<null>" : ResolveClassName(rootKlass);
                bool doSplit = rootKlass != IntPtr.Zero && IsSplitRootTypeByHierarchy(rootKlass);

                string rootLine = $"BCENSUSROOT t={now} src={src} i={i}/{roots.Count} cls={r.ClsName} field={r.FieldName} obj={objType} asm={r.AsmName}{(doSplit ? " split=1" : "")}";
                string rootLabel = $"{r.ClsName}.{r.FieldName}";

                if (doSplit)
                {
                    workItems.Add((IntPtr.Zero, rootLine, rootLabel));
                    CollectSplitWorkItems(src, now, r, rootKlass, workItems, rootLabel);
                }
                else
                {
                    workItems.Add((r.ObjPtr, rootLine, rootLabel));
                }
            }

            // 第2のルート源 (GCHandle) の列挙。gc_disable 後・ワールド停止前に済ませる — 対象ポインタを
            // 専用バッファへ集めるだけで、from_root はまだ呼ばない。
            try { il2cpp_gchandle_foreach_get_target(handleCallback, IntPtr.Zero); } catch { }

            // CLR 側の GC も窓の間だけ止める。失敗しても続行 (native 側のワールド停止だけでも前進の価値がある)。
            try { nogc = GC.TryStartNoGCRegion(NoGCRegionBytes); } catch { nogc = false; }

            // 少数のルートが巨大なグラフを保持している疑いを追うための計器: 各ルートが from_root で
            // 新規に発見したオブジェクト数 (_bufferCount の差分) を記録する。停止中は割当禁止なので、
            // 差分を書き込む配列は件数が確定した時点 (停止前) で確保しておく。
            var workItemDelta = new long[workItems.Count];
            var handleDelta = new long[_handleBufferCount];
            Mark("alloc");
            var lineBuffer = new StringBuilder(BufferCapacityHint);

            HealthLog.Note($"BCENSUSMARK t={now} src={src} phase=roots ms={sw.ElapsedMilliseconds} stage={Stage} enumerated={enumerated} valueTypeSkipped={valueTypeSkipped} noStatics={noStatics} genericDef={genericDef} excluded={excluded} using={roots.Count} handles={_handleBufferCount} nogc={nogc}");

            Mark("stopworld");

            try
            {
                bool worldStopped = false;
                try { il2cpp_stop_gc_world(); worldStopped = true; } catch { worldStopped = false; }

                try
                {
                    state = il2cpp_unity_liveness_allocate_struct(filterClass, MaxObjectCountHint, callback, IntPtr.Zero, reallocate);
                    if (state == IntPtr.Zero) throw new InvalidOperationException("liveness allocate_struct returned null state");

                    for (int i = 0; i < workItems.Count; i++)
                    {
                        (IntPtr callPtr, string line, string label) = workItems[i];
                        lineBuffer.Append(line).Append('\n');
                        if (callPtr != IntPtr.Zero)
                        {
                            long before = _bufferCount;
                            IL2CPP.il2cpp_unity_liveness_calculation_from_root(callPtr, state);
                            workItemDelta[i] = _bufferCount - before;
                        }
                    }

                    // ここまでが static 到達分。以降 GCHandle ルートで新規に発見される分だけを
                    // 「GCHandle のみ到達分」として区別する (既に static 側でマーク済みのオブジェクトは
                    // liveness 内部のマークチェックで再訪問されず register_object_callback も呼ばれない)。
                    _bufferBoundary = _bufferCount;

                    if (_handleBuffer != IntPtr.Zero && _handleBufferCount > 0)
                    {
                        unsafe
                        {
                            IntPtr* hp = (IntPtr*)_handleBuffer;
                            for (long h = 0; h < _handleBufferCount; h++)
                            {
                                IntPtr target = hp[h];
                                if (target != IntPtr.Zero)
                                {
                                    long before = _bufferCount;
                                    IL2CPP.il2cpp_unity_liveness_calculation_from_root(target, state);
                                    handleDelta[h] = _bufferCount - before;
                                }
                            }
                        }
                    }

                    // finalize が失敗すると次回走査が未完了のマーク状態から始まりうる。空 catch で握り潰さず
                    // 以後の Run を止める (壊れた計器で汚染された数値を出し続けるより計測停止のほうが安全)。
                    try
                    {
                        il2cpp_unity_liveness_finalize(state);
                        finalized = true;
                    }
                    catch (Exception e)
                    {
                        _disabled = true;
                        Logger.Warn($"BoehmCensus disabled: liveness_finalize failed ({e.Message})", "BoehmCensus");
                    }
                }
                finally
                {
                    if (worldStopped) { try { il2cpp_start_gc_world(); } catch { } }
                    Mark("startworld");
                }
            }
            finally
            {
                if (nogc) { try { GC.EndNoGCRegion(); } catch { } }
            }

            // ここから先はワールド再開後 — 溜めておいた行をまとめて書き出す。
            if (lineBuffer != null) FlushLineBuffer(lineBuffer);

            // マーク解除が終わって初めて klass/size を安全に読める。
            if (finalized)
            {
                Mark("classify");
                ClassifyBuffer(_bufferBoundary);
                ReportTopRoots(src, now, workItems, workItemDelta, handleDelta);
            }
        }
        finally
        {
            if (state != IntPtr.Zero)
            {
                Mark("free");
                try { il2cpp_unity_liveness_free_struct(state); } catch { }
            }

            Mark("gcenable");
            try { il2cpp_gc_enable(); } catch { }
        }

        sw.Stop();

        long totalObjs = 0;
        long totalBytes = 0;
        foreach ((long count, long bytes) in ClassCounts.Values) { totalObjs += count; totalBytes += bytes; }

        long handleObjs = 0;
        long handleBytes = 0;
        foreach ((long count, long bytes) in HandleOnlyClassCounts.Values) { handleObjs += count; handleBytes += bytes; }

        long staticsObjs = totalObjs - handleObjs;
        long staticsBytes = totalBytes - handleBytes;

        HealthLog.Note($"BCENSUSMARK t={now} src={src} phase=done ms={sw.ElapsedMilliseconds} objs={totalObjs}");

        long usedBytes = GcPrepass.BoehmUsedBytes();
        string usedMB = usedBytes < 0 ? "?" : (usedBytes / (1024 * 1024)).ToString();
        long nativeLeakKB = (_reallocAllocBytes - (_reallocFreeCount * LivenessBlockSize)) / 1024;

        string droppedSuffix = _droppedCount > 0 ? $" dropped={_droppedCount}" : "";
        HealthLog.Note($"BCENSUS t={now} src={src} objs={totalObjs} MB={totalBytes / (1024 * 1024)} staticsObjs={staticsObjs} staticsMB={staticsBytes / (1024 * 1024)} handleObjs={handleObjs} handleMB={handleBytes / (1024 * 1024)} handles={_handleBufferCount} usedMB={usedMB} ms={sw.ElapsedMilliseconds} nativeLeakKB={nativeLeakKB} clrGcMs={clrGcMs}{droppedSuffix}");

        var top = new StringBuilder("BCENSUSTOP t=").Append(now).Append(" src=").Append(src).Append(' ');

        foreach (var kv in ClassCounts.OrderByDescending(x => x.Value.Bytes).Take(TopCount))
        {
            string name = ResolveClassName(kv.Key);
            double mb = kv.Value.Bytes / (1024.0 * 1024.0);
            top.Append(name).Append('x').Append(kv.Value.Count).Append('=').Append(mb.ToString("0.0")).Append("MB ");
        }

        HealthLog.Note(top.ToString().TrimEnd());

        var topH = new StringBuilder("BCENSUSTOPH t=").Append(now).Append(" src=").Append(src).Append(' ');

        foreach (var kv in HandleOnlyClassCounts.OrderByDescending(x => x.Value.Bytes).Take(TopCount))
        {
            string name = ResolveClassName(kv.Key);
            double mb = kv.Value.Bytes / (1024.0 * 1024.0);
            topH.Append(name).Append('x').Append(kv.Value.Count).Append('=').Append(mb.ToString("0.0")).Append("MB ");
        }

        HealthLog.Note(topH.ToString().TrimEnd());
    }

    // ドメイン→アセンブリ→イメージ→クラス→フィールドを自前で辿り、参照型 static フィールドの
    // 非null 値をルート候補として集める。il2cpp API 呼び出しのみで、対象オブジェクトは未マークの
    // ままなので (このループ自体はまだ from_root を呼んでいない) 読み取りは安全。
    //
    // Class::Init 未実行 (static 領域未確保) のクラスは il2cpp_class_get_static_field_data が NULL を
    // 返す。il2cpp_field_static_get_value はこのチェックをせず NULL+offset を読んで AV するため、
    // フィールドを読む前にクラス単位で必ず弾く。オープンジェネリック定義 (il2cpp_class_is_generic)
    // も同様に static 領域を持たないので弾く。
    private static unsafe List<RootInfo> EnumerateRoots(string src, long now, out int scannedFields, out int valueTypeSkipped, out int noStatics, out int genericDef, out int excluded)
    {
        var roots = new List<RootInfo>(4096);
        int scanned = 0;
        int skipped = 0;
        int noStaticsCount = 0;
        int genericDefCount = 0;
        int excludedCount = 0;
        int classIndex = 0;

        try
        {
            IntPtr domain = IL2CPP.il2cpp_domain_get();
            if (domain == IntPtr.Zero)
            {
                scannedFields = 0;
                valueTypeSkipped = 0;
                noStatics = 0;
                genericDef = 0;
                excluded = 0;
                return roots;
            }

            uint asmCount = 0;
            IntPtr* assemblies = IL2CPP.il2cpp_domain_get_assemblies(domain, ref asmCount);

            for (uint a = 0; assemblies != null && a < asmCount; a++)
            {
                IntPtr asm = assemblies[a];
                if (asm == IntPtr.Zero) continue;

                IntPtr image = IL2CPP.il2cpp_assembly_get_image(asm);
                if (image == IntPtr.Zero) continue;

                string imgName = SafePtrToStringAnsi(IL2CPP.il2cpp_image_get_name(image));
                if (imgName.IndexOf("mscorlib", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                uint classCount;
                try { classCount = IL2CPP.il2cpp_image_get_class_count(image); }
                catch { continue; }

                for (uint c = 0; c < classCount; c++)
                {
                    classIndex++;
                    if (classIndex % 500 == 0)
                        HealthLog.Note($"BCENSUSENUM t={now} src={src} img={imgName} i={classIndex}");

                    try
                    {
                        IntPtr klass;
                        try { klass = IL2CPP.il2cpp_image_get_class(image, c); }
                        catch { continue; }
                        if (klass == IntPtr.Zero) continue;

                        // オープンジェネリック定義 (List<T> そのもの等) は static 領域を持たない。
                        bool isGenericDef;
                        try { isGenericDef = IL2CPP.il2cpp_class_is_generic(klass); }
                        catch { isGenericDef = false; }
                        if (isGenericDef) { genericDefCount++; continue; }

                        // Class::Init 未実行のクラスは static_fields が NULL のまま。
                        IntPtr staticFieldData;
                        try { staticFieldData = il2cpp_class_get_static_field_data(klass); }
                        catch { staticFieldData = IntPtr.Zero; }
                        if (staticFieldData == IntPtr.Zero) { noStaticsCount++; continue; }

                        // 宣言クラスの名前空間が除外リストに当たるなら、そのクラスの static フィールドは
                        // 丸ごとルート候補にしない (Rewired.ReInput 配下の from_root で無音終了した実例)。
                        string declNs = SafeNamespace(klass);
                        if (IsExcludedNamespace(declNs)) { excludedCount++; continue; }

                        string clsName = SafeClassName(klass);

                        IntPtr iter = IntPtr.Zero;
                        IntPtr field;

                        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
                        {
                            scanned++;

                            int flags;
                            try { flags = IL2CPP.il2cpp_field_get_flags(field); }
                            catch { continue; }
                            if ((flags & FieldAttributeStatic) == 0) continue;
                            if ((flags & FieldAttributeLiteral) != 0) continue;

                            uint offset;
                            try { offset = IL2CPP.il2cpp_field_get_offset(field); }
                            catch { continue; }
                            if (offset == unchecked((uint)-1)) continue; // thread-static sentinel

                            IntPtr fieldType;
                            try { fieldType = IL2CPP.il2cpp_field_get_type(field); }
                            catch { continue; }
                            if (fieldType == IntPtr.Zero) continue;

                            bool isRef;
                            try { isRef = IsReferenceFieldType(fieldType); }
                            catch { isRef = false; }

                            if (!isRef) { skipped++; continue; }

                            IntPtr objPtr;
                            try
                            {
                                IntPtr val = IntPtr.Zero;
                                IL2CPP.il2cpp_field_static_get_value(field, &val);
                                objPtr = val;
                            }
                            catch { continue; }

                            if (objPtr == IntPtr.Zero) continue;

                            // 宣言クラスは除外対象でなくても、参照先オブジェクト自身が除外namespace型な
                            // ケースがある (別クラスの static が Rewired オブジェクトを持つ場合)。
                            if (IsExcludedNamespace(SafeObjNamespace(objPtr))) { excludedCount++; continue; }

                            string fieldName = SafePtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                            roots.Add(new RootInfo(objPtr, clsName, fieldName, imgName));
                        }
                    }
                    catch { } // AV は捕まらないが、通常の管理例外はここでクラス単位に閉じ込めて続行する
                }
            }
        }
        catch (Exception e) { Logger.Warn($"boehm root enumeration failed: {e.Message}", "BoehmCensus"); }

        noStatics = noStaticsCount;
        genericDef = genericDefCount;
        excluded = excludedCount;

        scannedFields = scanned;
        valueTypeSkipped = skipped;
        return roots;
    }

    private static void ResetBuffer()
    {
        if (_buffer == IntPtr.Zero)
        {
            try
            {
                _bufferCapacity = InitialBufferCapacity;
                _buffer = Marshal.AllocHGlobal((nint)(_bufferCapacity * IntPtr.Size));
            }
            catch
            {
                _buffer = IntPtr.Zero;
                _bufferCapacity = 0;
            }
        }

        _bufferCount = 0;
        _droppedCount = 0;
        _bufferGrowFailed = false;
    }

    private static bool GrowBuffer()
    {
        try
        {
            long newCapacity = _bufferCapacity <= 0 ? InitialBufferCapacity : _bufferCapacity * 2;
            IntPtr newBuf = _buffer == IntPtr.Zero
                ? Marshal.AllocHGlobal((nint)(newCapacity * IntPtr.Size))
                : Marshal.ReAllocHGlobal(_buffer, (nint)(newCapacity * IntPtr.Size));

            _buffer = newBuf;
            _bufferCapacity = newCapacity;
            return true;
        }
        catch { return false; }
    }

    private static void ResetHandleBuffer()
    {
        if (_handleBuffer == IntPtr.Zero)
        {
            try
            {
                _handleBufferCapacity = InitialBufferCapacity;
                _handleBuffer = Marshal.AllocHGlobal((nint)(_handleBufferCapacity * IntPtr.Size));
            }
            catch
            {
                _handleBuffer = IntPtr.Zero;
                _handleBufferCapacity = 0;
            }
        }

        _handleBufferCount = 0;
        _handleDroppedCount = 0;
        _handleBufferGrowFailed = false;
    }

    private static bool GrowHandleBuffer()
    {
        try
        {
            long newCapacity = _handleBufferCapacity <= 0 ? InitialBufferCapacity : _handleBufferCapacity * 2;
            IntPtr newBuf = _handleBuffer == IntPtr.Zero
                ? Marshal.AllocHGlobal((nint)(newCapacity * IntPtr.Size))
                : Marshal.ReAllocHGlobal(_handleBuffer, (nint)(newCapacity * IntPtr.Size));

            _handleBuffer = newBuf;
            _handleBufferCapacity = newCapacity;
            return true;
        }
        catch { return false; }
    }

    // Il2Cpp から直接呼ばれる。マーク解除前なので il2cpp API は一切呼ばない — 受け取ったポインタを
    // native バッファへ書き写すだけ (マネージド割当ゼロ)。例外はこの境界を越えると FailFast するため
    // 必ず握り潰す。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe void RegisterObjectCallback(IntPtr* arr, int size, IntPtr userdata)
    {
        try
        {
            _cbCallCount++;
            _cbRecvCount += size;

            if (_buffer == IntPtr.Zero) { _droppedCount += size; return; }

            for (int i = 0; i < size; i++)
            {
                IntPtr obj = arr[i];
                if (obj == IntPtr.Zero) continue;

                if (_bufferCount >= _bufferCapacity)
                {
                    if (_bufferGrowFailed || !GrowBuffer())
                    {
                        _bufferGrowFailed = true;
                        _droppedCount++;
                        continue;
                    }
                }

                ((IntPtr*)_buffer)[_bufferCount] = obj;
                _bufferCount++;
            }
        }
        catch { }
    }

    // il2cpp_gchandle_foreach_get_target から1件ずつ呼ばれる。RegisterObjectCallback と同じ理由で
    // il2cpp API は呼ばず、ハンドルの指す生ポインタを専用バッファへ積むだけに留める。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static unsafe void GcHandleForeachCallback(IntPtr data, IntPtr userData)
    {
        try
        {
            if (data == IntPtr.Zero) return;
            if (_handleBuffer == IntPtr.Zero) { _handleDroppedCount++; return; }

            if (_handleBufferCount >= _handleBufferCapacity)
            {
                if (_handleBufferGrowFailed || !GrowHandleBuffer())
                {
                    _handleBufferGrowFailed = true;
                    _handleDroppedCount++;
                    return;
                }
            }

            ((IntPtr*)_handleBuffer)[_handleBufferCount] = data;
            _handleBufferCount++;
        }
        catch { }
    }

    // finalize (マーク解除) 完了後にのみ呼ばれる。ここで初めて klass/size を安全に読める。boundary より
    // 前のインデックス = static ルートで訪問された分、以降 = GCHandle ルートでのみ新規発見された分。
    private static unsafe void ClassifyBuffer(long boundary)
    {
        if (_buffer == IntPtr.Zero || _bufferCount == 0) return;

        IntPtr* p = (IntPtr*)_buffer;

        for (long i = 0; i < _bufferCount; i++)
        {
            IntPtr obj = p[i];
            if (obj == IntPtr.Zero) continue;

            IntPtr klass;
            uint objSize;

            try
            {
                klass = IL2CPP.il2cpp_object_get_class(obj);
                if (klass == IntPtr.Zero) continue;
                objSize = IL2CPP.il2cpp_object_get_size(obj);
            }
            catch { continue; }

            if (ClassCounts.TryGetValue(klass, out (long Count, long Bytes) acc))
                ClassCounts[klass] = (acc.Count + 1, acc.Bytes + objSize);
            else
                ClassCounts[klass] = (1, objSize);

            if (i >= boundary)
            {
                if (HandleOnlyClassCounts.TryGetValue(klass, out (long Count, long Bytes) hacc))
                    HandleOnlyClassCounts[klass] = (hacc.Count + 1, hacc.Bytes + objSize);
                else
                    HandleOnlyClassCounts[klass] = (1, objSize);
            }
        }
    }


    // 少数のルートが巨大なグラフを保持している可能性を追う計器。統計ルート (workItems) と GCHandle
    // ルートそれぞれについて、from_root 直後の _bufferCount 差分が大きい上位ランクを報告する。
    // 差分配列は元の (static→GCHandle の順で連続な) index 順のまま渡ってくるので、区間の開始位置は
    // 差分の累積和 (prefix sum) から復元できる — 停止中に区間境界そのものを別途記録する必要はない。
    private static void ReportTopRoots(string src, long now, List<(IntPtr CallPtr, string Line, string Label)> workItems, long[] workItemDelta, long[] handleDelta)
    {
        try
        {
            long acc = 0;
            var workStarts = new long[workItemDelta.Length];
            for (int i = 0; i < workItemDelta.Length; i++) { workStarts[i] = acc; acc += workItemDelta[i]; }

            var workIdx = new int[workItemDelta.Length];
            for (int i = 0; i < workIdx.Length; i++) workIdx[i] = i;
            Array.Sort(workIdx, (a, b) => workItemDelta[b].CompareTo(workItemDelta[a]));

            int sRank = 0;
            for (int k = 0; k < workIdx.Length && sRank < 10; k++)
            {
                int i = workIdx[k];
                long delta = workItemDelta[i];
                if (delta <= 0) break;

                sRank++;
                long bytes = SumBytesInRange(workStarts[i], workStarts[i] + delta);
                HealthLog.Note($"BCENSUSSROOT t={now} src={src} rank={sRank} objs={delta} MB={bytes / (1024 * 1024)} root={workItems[i].Label}");
            }
        }
        catch (Exception e) { Logger.Warn($"boehm sroot report failed: {e.Message}", "BoehmCensus"); }

        try
        {
            long acc = _bufferBoundary;
            var handleStarts = new long[handleDelta.Length];
            for (int h = 0; h < handleDelta.Length; h++) { handleStarts[h] = acc; acc += handleDelta[h]; }

            var handleIdx = new int[handleDelta.Length];
            for (int h = 0; h < handleIdx.Length; h++) handleIdx[h] = h;
            Array.Sort(handleIdx, (a, b) => handleDelta[b].CompareTo(handleDelta[a]));

            int hRank = 0;
            for (int k = 0; k < handleIdx.Length && hRank < 25; k++)
            {
                int h = handleIdx[k];
                long delta = handleDelta[h];
                if (delta <= 0) break;

                hRank++;
                long bytes = SumBytesInRange(handleStarts[h], handleStarts[h] + delta);
                IntPtr rootObj = GetHandleTarget(h);
                string typeName = SafeObjTypeName(rootObj);
                HealthLog.Note($"BCENSUSHROOT t={now} src={src} rank={hRank} objs={delta} MB={bytes / (1024 * 1024)} type={typeName}");

                if (hRank <= 5) ReportRootFieldExpansion(hRank, rootObj);
            }
        }
        catch (Exception e) { Logger.Warn($"boehm hroot report failed: {e.Message}", "BoehmCensus"); }
    }

    // finalize 済みなのでマークの心配なく安全に読める。配列ルートは要素展開ではなく len/elem の1行、
    // それ以外は参照型インスタンスフィールドを1段展開して1行ずつ出す (CollectInstanceRefFields を流用)。
    private static void ReportRootFieldExpansion(int rank, IntPtr rootObj)
    {
        try
        {
            if (rootObj == IntPtr.Zero) return;

            IntPtr klassRaw = IL2CPP.il2cpp_object_get_class(rootObj);
            if (klassRaw == IntPtr.Zero) return;
            IntPtr klass = (IntPtr)(klassRaw.ToInt64() & ~1L);

            if (SafeRank(klass) > 0)
            {
                uint len = 0;
                try { len = IL2CPP.il2cpp_array_length(rootObj); } catch { }

                IntPtr elemClass = IL2CPP.il2cpp_class_get_element_class(klass);
                string elemName = elemClass != IntPtr.Zero ? SafeClassName(elemClass).Replace(' ', '_') : "?";
                HealthLog.Note($"BCENSUSHROOTF rank={rank} len={len} elem={elemName}");
                return;
            }

            List<(string FieldName, IntPtr ObjPtr)> fields = CollectInstanceRefFields(klass, rootObj, out _);

            for (int j = 0; j < fields.Count; j++)
            {
                (string fieldName, IntPtr objPtr) = fields[j];
                string objType = SafeObjTypeName(objPtr);
                HealthLog.Note($"BCENSUSHROOTF rank={rank} field={fieldName} obj={objType}");
            }
        }
        catch (Exception e) { Logger.Warn($"boehm hroot field expansion failed: {e.Message}", "BoehmCensus"); }
    }

    // [start, end) 区間のオブジェクトのサイズ合計。finalize 済み (マーク解除後) の呼び出し専用。
    private static unsafe long SumBytesInRange(long start, long end)
    {
        if (_buffer == IntPtr.Zero) return 0;
        if (start < 0) start = 0;

        long clampedEnd = Math.Min(end, _bufferCount);
        IntPtr* p = (IntPtr*)_buffer;
        long total = 0;

        for (long i = start; i < clampedEnd; i++)
        {
            IntPtr obj = p[i];
            if (obj == IntPtr.Zero) continue;

            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(obj);
                if (klass == IntPtr.Zero) continue;
                total += IL2CPP.il2cpp_object_get_size(obj);
            }
            catch { }
        }

        return total;
    }

    // ハンドルバッファは Run を跨いで使い回すため、ReportTopRoots (ワールド再開後) からの参照も安全。
    private static unsafe IntPtr GetHandleTarget(long index)
    {
        if (_handleBuffer == IntPtr.Zero || index < 0 || index >= _handleBufferCount) return IntPtr.Zero;
        return ((IntPtr*)_handleBuffer)[index];
    }

    // C の realloc 契約: 失敗時は例外を投げず null (IntPtr.Zero) を返す。Marshal.AllocHGlobal/
    // ReAllocHGlobal は失敗時に OutOfMemoryException を投げてしまう (null を返さない) ため、ここで
    // 契約どおりに変換しないと native 側が例外を素通りさせて FailFast する。liveness 側の実装は
    // 「新規確保 (ptr=null)」と「解放 (size=0)」の2パターンしか呼ばない。
    //
    // このコールバックはワールド停止中にも呼ばれるため、Dictionary 更新 (割当を伴う) は避け、確保回数/
    // 解放回数/確保バイト合計の3本の long カウンタだけを更新する。内部バッファは 8KB
    // (LivenessBlockSize) 固定ブロックでしか確保されないため、解放回数×8KB で解放済みバイト数を
    // 正確に近似できる。
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static IntPtr ReallocateCallback(IntPtr ptr, UIntPtr size, IntPtr state)
    {
        try
        {
            long newSize = (long)size;

            if (newSize == 0)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                    _reallocFreeCount++;
                }

                return IntPtr.Zero;
            }

            IntPtr result = ptr == IntPtr.Zero ? Marshal.AllocHGlobal((nint)newSize) : Marshal.ReAllocHGlobal(ptr, (nint)newSize);
            _reallocAllocCount++;
            _reallocAllocBytes += newSize;
            return result;
        }
        catch { return IntPtr.Zero; }
    }

    // finalize 完了後のオブジェクトの実行時型名。まだ finalize していない (今まさにルートを歩いている)
    // 時点で BCENSUSROOT/BCENSUSSUB の obj= 用に呼ばれるため、klass の下位ビット (他ルートで既に付いた
    // マーク) を落としてから名前解決する。
    private static string SafeObjTypeName(IntPtr obj)
    {
        try
        {
            IntPtr klassRaw = IL2CPP.il2cpp_object_get_class(obj);
            if (klassRaw == IntPtr.Zero) return "<null>";
            IntPtr klass = (IntPtr)(klassRaw.ToInt64() & ~1L);
            return ResolveClassName(klass);
        }
        catch { return "<?>"; }
    }

    // 分類フェーズ (通常のマネージドコード) からのみ呼ばれる — 走査終了後 (統計処理が済んでから) に
    // だけクラス名を解決する。コールバック内で文字列化すると走査自体を遅くする。
    private static string ResolveClassName(IntPtr klass)
    {
        try
        {
            string name = SafeClassName(klass);

            // rank>0 だけを配列とみなす。element_class の非null判定は enum の backing type でも
            // 非null になり、"Int32[]" のような誤表示を生む。
            int rank = SafeRank(klass);

            if (rank > 0 && !name.EndsWith("]", StringComparison.Ordinal))
            {
                IntPtr elemClass = IL2CPP.il2cpp_class_get_element_class(klass);
                if (elemClass != IntPtr.Zero && elemClass != klass)
                {
                    string elemName = SafeClassName(elemClass);
                    if (!string.IsNullOrEmpty(elemName)) name = elemName + "[]";
                }
            }

            return name.Replace(' ', '_');
        }
        catch { return "<?>"; }
    }

    private static int SafeRank(IntPtr klass)
    {
        try { return il2cpp_class_get_rank(klass); }
        catch { return 0; }
    }

    private static string SafeClassName(IntPtr klass)
    {
        try
        {
            string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
            string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
            if (string.IsNullOrEmpty(name)) return "<noname>";
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        catch { return "<?>"; }
    }

    private static string SafePtrToStringAnsi(IntPtr p)
    {
        try { return Marshal.PtrToStringAnsi(p) ?? ""; }
        catch { return ""; }
    }

    private static string SafeNamespace(IntPtr klass)
    {
        try { return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass)) ?? ""; }
        catch { return ""; }
    }

    // 参照先オブジェクトの実行時型の名前空間。EnumerateRoots はどの from_root もまだ呼んでいない時点で
    // しか使わない (マーク前提のため、下位ビットの汚染は起こらない) が、念のため SafeObjTypeName と
    // 同様にマスクしてから解決する。
    private static string SafeObjNamespace(IntPtr obj)
    {
        try
        {
            IntPtr klassRaw = IL2CPP.il2cpp_object_get_class(obj);
            if (klassRaw == IntPtr.Zero) return "";
            IntPtr klass = (IntPtr)(klassRaw.ToInt64() & ~1L);
            return SafeNamespace(klass);
        }
        catch { return ""; }
    }

    private static bool IsExcludedNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns)) return false;

        for (int i = 0; i < ExcludedRootNamespaces.Length; i++)
            if (ns.StartsWith(ExcludedRootNamespaces[i], StringComparison.Ordinal)) return true;

        return false;
    }

    // EnumerateRoots (static フィールド) と CollectInstanceRefFields (インスタンスフィールド) の
    // 双方から使う参照型判定。GENERICINST は解決先クラスが値型でない場合のみ参照型とみなす。
    private static bool IsReferenceFieldType(IntPtr fieldType)
    {
        int typeEnum = IL2CPP.il2cpp_type_get_type(fieldType);
        bool isRef = typeEnum is TypeString or TypeClass or TypeArray or TypeSzArray or TypeObject;

        if (!isRef && typeEnum == TypeGenericInst)
        {
            IntPtr genKlass = IL2CPP.il2cpp_class_from_type(fieldType);
            isRef = genKlass != IntPtr.Zero && !IL2CPP.il2cpp_class_is_valuetype(genKlass);
        }

        return isRef;
    }

    private static string SafeBareClassName(IntPtr klass)
    {
        try { return Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? ""; }
        catch { return ""; }
    }

    private static bool IsSplitRootType(string bareName)
    {
        if (string.IsNullOrEmpty(bareName)) return false;

        for (int i = 0; i < SplitRootTypes.Length; i++)
            if (bareName == SplitRootTypes[i]) return true;

        return false;
    }

    // obj の実行時型が SplitRootTypes に直接一致しなくても、派生クラス (例: PolusShipStatus ← ShipStatus)
    // ならその親クラス連鎖のどこかで一致しうる。名前空間なしの型名で親クラスを辿って判定する。
    private static bool IsSplitRootTypeByHierarchy(IntPtr klass)
    {
        IntPtr cur = klass;
        int depth = 0;

        while (cur != IntPtr.Zero && depth < 32)
        {
            string name = SafeBareClassName(cur);
            if (IsSplitRootType(name)) return true;

            IntPtr parent;
            try { parent = IL2CPP.il2cpp_class_get_parent(cur); }
            catch { parent = IntPtr.Zero; }
            if (parent == cur) break; // 循環防止
            cur = parent;
            depth++;
        }

        return false;
    }

    // klass 自身と parent 連鎖の非 static 参照型インスタンスフィールドの値を集める。ワールド停止前
    // (from_root 呼び出し前) にのみ使う — 対象オブジェクトが未マークなら値の読み出しは安全。
    private static unsafe List<(string FieldName, IntPtr ObjPtr)> CollectInstanceRefFields(IntPtr klass, IntPtr obj, out int valueTypeSkipped)
    {
        var result = new List<(string, IntPtr)>(64);
        int skipped = 0;

        IntPtr cur = klass;
        int depth = 0;

        while (cur != IntPtr.Zero && depth < 32)
        {
            IntPtr iter = IntPtr.Zero;
            IntPtr field;

            while ((field = IL2CPP.il2cpp_class_get_fields(cur, ref iter)) != IntPtr.Zero)
            {
                int flags;
                try { flags = IL2CPP.il2cpp_field_get_flags(field); }
                catch { continue; }
                if ((flags & FieldAttributeStatic) != 0) continue; // インスタンスフィールドのみ

                IntPtr fieldType;
                try { fieldType = IL2CPP.il2cpp_field_get_type(field); }
                catch { continue; }
                if (fieldType == IntPtr.Zero) continue;

                bool isRef;
                try { isRef = IsReferenceFieldType(fieldType); }
                catch { isRef = false; }
                if (!isRef) { skipped++; continue; }

                IntPtr objPtr;
                try
                {
                    IntPtr val = IntPtr.Zero;
                    IL2CPP.il2cpp_field_get_value(obj, field, &val);
                    objPtr = val;
                }
                catch { continue; }

                if (objPtr == IntPtr.Zero) continue;

                string fieldName = SafePtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                result.Add((fieldName, objPtr));
            }

            IntPtr parent;
            try { parent = IL2CPP.il2cpp_class_get_parent(cur); }
            catch { parent = IntPtr.Zero; }
            if (parent == cur) break; // 循環防止
            cur = parent;
            depth++;
        }

        valueTypeSkipped = skipped;
        return result;
    }

    // 分割対象ルートのインスタンスフィールドを展開し、from_root 呼び出しと BCENSUSSUB 行を workItems へ
    // 積む (呼び出しと I/O 自体はまだ行わない — ワールド停止中のループで消費される)。
    private static void CollectSplitWorkItems(string src, long now, RootInfo r, IntPtr klass, List<(IntPtr CallPtr, string Line, string Label)> workItems, string parentLabel)
    {
        List<(string FieldName, IntPtr ObjPtr)> subFields = CollectInstanceRefFields(klass, r.ObjPtr, out _);
        string rootLabel = $"{r.ClsName}.{r.FieldName}";

        for (int j = 0; j < subFields.Count; j++)
        {
            (string fieldName, IntPtr objPtr) = subFields[j];
            string objType = SafeObjTypeName(objPtr);
            string line = $"BCENSUSSUB t={now} src={src} root={rootLabel} j={j}/{subFields.Count} field={fieldName} obj={objType}";
            string label = $"{parentLabel}.{fieldName}";
            workItems.Add((objPtr, line, label));
        }
    }

    // ワールド停止中に溜めた行をまとめて Health.log へ書き出す。停止中は StringBuilder への追記だけに
    // 留め、File I/O は世界再開後にここでまとめて行う。
    private static void FlushLineBuffer(StringBuilder buffer)
    {
        if (buffer.Length == 0) return;

        string[] lines = buffer.ToString().Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            HealthLog.Note(lines[i]);
        }
    }
}
