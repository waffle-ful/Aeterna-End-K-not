using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// メニュー構築で初めて呼ばれるメソッドを、スプラッシュ中に裏スレッドで先にコンパイルしておく。
// 対象は Resources/BootPreJit/menu.txt (「型::メソッド」を 1 行ずつ・重い順)。
//
// JIT はメソッド本体が触る静的フィールドの持ち主の型初期化子を、その場 (= このスレッド) で走らせる。
// Unity / il2cpp に触る初期化子を主スレッド以外で走らせないよう、IL を読んで
// 「初期化子が無い型」「標準ライブラリの型」「標準ライブラリしか呼ばない初期化子を持つ自前の型」
// 「主スレッドで初期化を済ませた型」だけを触るメソッドに限って先行コンパイルする。
// ループを持つメソッドは最適化コンパイルになり呼び先がインライン展開されるので、呼び先の本体にも
// 同じ検査をかける。
//
// 検査で止まったメソッドは、止めた型を主スレッド (MainTick) で先に初期化してからもう一度試す
// (1 回の検査で分かるのは最初に引っかかった型だけなので、これを数周繰り返す)。
// 主スレッドで先に初期化してよいのは、il2cpp 側の型 (初期化子はポインタの解決だけ) と、
// EarlyInit に挙げた自前の型だけ。それでも通らないメソッドは何もしない
// (従来どおり初回呼び出し時に主スレッドでコンパイルされる)。
//
// 全体はスプラッシュの最初の 1 秒ほど (ログイン待ちで主スレッドがほぼ何もしていない間) に終わる。
// メニュー構築やオプション構築と同時には走らせない。
public static class BootPreJit
{
    // ENDKNOT_BOOT_PREJIT=0 で無効 (比較計測用)。
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("ENDKNOT_BOOT_PREJIT") != "0";

    // ENDKNOT_BOOT_PREJIT=verbose で、見送ったメソッドと理由を全部ログへ出す。
    private static readonly bool Verbose = Environment.GetEnvironmentVariable("ENDKNOT_BOOT_PREJIT") == "verbose";

    // 初期化子が「プラグイン読み込み後ならいつ走っても同じ結果になる」自前の型。
    // ゲームのシングルトンやシーン上のオブジェクトを初期化子で読む型 (例: 静的フィールドの初期値に
    // GameOptionsManager.Instance を使う型) をここへ足してはいけない。スプラッシュ中はまだ存在しない。
    private static readonly HashSet<string> EarlyInit = new(StringComparer.Ordinal)
    {
        "EndKnot.Utils",
        "EndKnot.Patches.CalamityMenu.CalamityDusk",
        "EndKnot.Patches.CalamityMenu.CalamityButtons",
        "EndKnot.Patches.CalamityMenu.EndKnotFeatureBridge",
        "EndKnot.Modules.CustomSoundsManager",
        "EndKnot.Modules.DataFlagRateLimiter",
        "EndKnot.Modules.HitboxDebug",
        "EndKnot.Modules.Audience.AudienceInterventions",
        "EndKnot.Modules.Audience.AudienceEconomy",
        "EndKnot.GameSettingMenuPatch",
    };

    // 初期化子が必ず済んでいる自前の型 (プラグインの読み込み自体がこの型の上で走っている)。
    private const string MainType = "EndKnot.Main";

    private const string ListResource = "EndKnot.Resources.BootPreJit.menu.txt";
    private const int InlineIlLimit = 100;
    private const int MaxInlineDepth = 12;
    private const int MaxRounds = 16;
    private const double MainInitBudgetMs = 3.0;
    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    // 段階コンパイルが切られていると、ループの無いメソッドも最初から最適化コンパイル (インライン展開あり) になる。
    private static readonly bool AlwaysOptimized = TieringDisabled();

    private const int Idle = 0, Compiling = 1, WaitingMain = 2, Finished = 3;

    private static volatile string _summary = "off";
    private static volatile bool _stop;
    private static volatile int _phase = Idle;
    private static string _detailBox;

    // 以下は裏スレッドと主スレッドが交代で触る (_phase が Compiling の間は裏、WaitingMain の間は主)。
    private static List<(MethodBase Method, string Line)> _todo;
    private static readonly HashSet<Type> MainInited = [];
    private static readonly HashSet<Type> InitFailed = [];
    private static readonly Queue<Type> InitQueue = new();
    private static readonly Dictionary<string, int> Blockers = [];
    private static readonly List<string> SkippedList = [];
    private static int _prepared, _missing, _failed, _round, _inited;
    private static long _ticks;

    // BOOT 行に載せる要約 (コンパイル済み / 見送り / リストに無い / 失敗 / 裏スレッド ms / 主スレッドで初期化した型 / 周回)。
    public static string Summary => _summary;

    public static void Start()
    {
        if (_phase != Idle || !Enabled || OperatingSystem.IsAndroid()) return;
        _summary = "running";
        EmbeddedDeps.Preload();
        StartThread();
    }

    // メニューが操作可能になったら残りは捨てる (もう主スレッドが自分で踏んでいる)。
    public static void Stop() => _stop = true;

    // 主スレッドから毎フレーム呼ぶ。裏スレッドが止まった原因の型を、1 フレームあたり数 ms ずつ初期化する。
    public static void MainTick()
    {
        if (_phase != WaitingMain) return;
        if (_stop) { Finish(); return; }

        long t0 = Stopwatch.GetTimestamp();

        while (InitQueue.Count > 0)
        {
            Type type = InitQueue.Dequeue();

            try
            {
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                MainInited.Add(type);
                _inited++;
            }
            catch { InitFailed.Add(type); }

            if ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency >= MainInitBudgetMs) return;
        }

        BootTimeline.Mark("prejit.round");
        StartThread();
    }

    // 主スレッドから呼ぶ: 裏スレッドが残した内訳を 1 回だけログへ。
    public static void FlushLog()
    {
        string detail = Interlocked.Exchange(ref _detailBox, null);
        if (detail != null) Logger.Info(detail, "BootPreJit");
    }

    private static void StartThread()
    {
        _phase = Compiling;

        try
        {
            var thread = new Thread(ThreadMain) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "EndKnot.PreJit" };
            thread.Start();
        }
        catch (Exception ex)
        {
            _summary = "err:" + ex.GetType().Name;
            _phase = Finished;
        }
    }

    private static void ThreadMain()
    {
        // 検査をすり抜けた型初期化子が il2cpp を呼んでも GC がこのスレッドを知っているように登録しておく。
        IntPtr il2cppThread = IntPtr.Zero;
        try { il2cppThread = IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get()); }
        catch { }

        try { Run(); }
        catch (Exception ex)
        {
            _summary = "err:" + ex.GetType().Name;
            _phase = Finished;
        }
        finally
        {
            if (il2cppThread != IntPtr.Zero)
            {
                try { IL2CPP.il2cpp_thread_detach(il2cppThread); }
                catch { }
            }
        }
    }

    private static void Run()
    {
        long t0 = Stopwatch.GetTimestamp();
        Assembly asm = typeof(BootPreJit).Assembly;
        _todo ??= ReadList(asm);
        if (_todo == null) { _summary = "nolist"; _phase = Finished; return; }

        _round++;
        var scan = new IlScan(asm, MainInited);
        var next = new List<(MethodBase Method, string Line)>();
        var wanted = new HashSet<Type>();
        Blockers.Clear();
        SkippedList.Clear();

        foreach ((MethodBase method, string line) in _todo)
        {
            if (_stop)
            {
                next.Add((method, line));
                continue;
            }

            try
            {
                string blocker = scan.RootBlocker(method, out Type blockerType);

                if (blocker != null)
                {
                    next.Add((method, line));
                    Blockers[blocker] = Blockers.GetValueOrDefault(blocker) + 1;
                    if (Verbose) SkippedList.Add(line + "<-" + blocker);

                    if (blockerType != null && !MainInited.Contains(blockerType) && !InitFailed.Contains(blockerType))
                    {
                        if (blockerType.FullName == MainType || scan.MayInitOnMain(blockerType, EarlyInit)) wanted.Add(blockerType);
                    }

                    continue;
                }

                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                _prepared++;
            }
            catch { _failed++; }
        }

        _todo = next;
        _ticks += Stopwatch.GetTimestamp() - t0;

        if (!_stop && _round < MaxRounds && wanted.Count > 0)
        {
            foreach (Type type in wanted) InitQueue.Enqueue(type);
            _phase = WaitingMain;
            return;
        }

        Finish();
    }

    private static void Finish()
    {
        long ms = _ticks * 1000 / Stopwatch.Frequency;
        int skipped = _todo?.Count ?? 0;
        _summary = $"{_prepared}/s{skipped}/m{_missing}/f{_failed}/{ms}ms/i{_inited}/r{_round}/@{BootTimeline.ElapsedMs}{(_stop ? "/cut" : "")}";
        string top = string.Join(", ", Blockers.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"{kv.Key}={kv.Value}"));
        Interlocked.Exchange(ref _detailBox, $"prepared={_prepared} skipped={skipped} missing={_missing} failed={_failed} ms={ms} inited={_inited} rounds={_round} blockers=[{top}]{(InitFailed.Count > 0 ? " initFailed=[" + string.Join(", ", InitFailed.Select(t => t.FullName)) + "]" : "")}{(Verbose ? " skippedList=[" + string.Join(" | ", SkippedList) + "]" : "")}");
        _phase = Finished;
    }

    private static bool TieringDisabled()
    {
        try
        {
            if (AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool tiered) && !tiered) return true;
            if (AppContext.TryGetSwitch("System.Runtime.TieredCompilation.QuickJit", out bool quick) && !quick) return true;

            foreach (string prefix in new[] { "DOTNET_", "COMPlus_" })
            {
                if (Environment.GetEnvironmentVariable(prefix + "TieredCompilation") == "0") return true;
                if (Environment.GetEnvironmentVariable(prefix + "TC_QuickJit") == "0") return true;
            }

            return false;
        }
        catch { return true; }
    }

    private static List<(MethodBase Method, string Line)> ReadList(Assembly asm)
    {
        string[] lines;

        using (Stream stream = asm.GetManifestResourceStream(ListResource))
        {
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n');
        }

        var result = new List<(MethodBase Method, string Line)>();
        var methodsByType = new Dictionary<string, MethodBase[]>();

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            int sep = line.IndexOf("::", StringComparison.Ordinal);
            if (sep <= 0) continue;
            string typeName = line[..sep], methodName = line[(sep + 2)..];

            if (!methodsByType.TryGetValue(typeName, out MethodBase[] all))
            {
                Type type = null;
                try { type = asm.GetType(typeName, false); }
                catch { }

                all = type == null || type.ContainsGenericParameters
                    ? Array.Empty<MethodBase>()
                    : type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToArray();
                methodsByType[typeName] = all;
            }

            var found = false;

            foreach (MethodBase method in all)
            {
                if (method.Name != methodName) continue;
                found = true;
                result.Add((method, line));
            }

            if (!found) _missing++;
        }

        return result;
    }

    private sealed class IlScan
    {
        private readonly Assembly _self;
        private readonly OpCode?[] _one = new OpCode?[256];
        private readonly OpCode?[] _two = new OpCode?[256];
        private readonly Dictionary<Type, bool> _typeOk = [];
        private readonly Dictionary<MethodBase, string> _inlineeBlocker = [];
        private readonly HashSet<Type> _preInited;
        private readonly List<Type> _provisional = [];
        private int _typeDepth;
        private Type _blockerType;

        public IlScan(Assembly self, HashSet<Type> preInited)
        {
            _self = self;
            _preInited = preInited;

            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is not OpCode op) continue;
                ushort value = (ushort)op.Value;
                if (value < 0x100) _one[value] = op;
                else if ((value & 0xFF00) == 0xFE00) _two[value & 0xFF] = op;
            }
        }

        // null = 先行コンパイルしてよい。非 null = 止めた理由 (型名など)。
        public string RootBlocker(MethodBase method, out Type blockerType)
        {
            _blockerType = null;
            string result = RootBlockerCore(method);
            blockerType = result == null ? null : _blockerType;
            return result;
        }

        // 主スレッドで先に初期化してよい型か。自前の型は allow に挙がっていて、かつ初期化子が
        // il2cpp 側の参照型 (シーン上のオブジェクトやシングルトン) を直接呼んでいないこと。
        public bool MayInitOnMain(Type type, HashSet<string> allow)
        {
            if (type.ContainsGenericParameters) return false;
            if (type.Assembly != _self) return true;
            if (type.IsGenericType || type.FullName == null || !allow.Contains(type.FullName)) return false;

            ConstructorInfo cctor = type.TypeInitializer;
            return cctor == null || InitializerCallsOnly(cctor, earlyOnMain: true);
        }

        private string RootBlockerCore(MethodBase method)
        {
            if (method.IsAbstract || method.ContainsGenericParameters) return "#generic";
            if (method is ConstructorInfo { IsStatic: true }) return "#cctor";
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0) return "#pinvoke";

            Type owner = method.DeclaringType;
            if (owner == null || owner.ContainsGenericParameters) return "#generic";

            // 明示的な静的コンストラクタを持つ型は、静的メソッド/コンストラクタの準備時に初期化子が走る。
            if ((owner.Attributes & TypeAttributes.BeforeFieldInit) == 0 && !TypeOk(owner))
            {
                _blockerType = owner;
                return owner.FullName;
            }

            string blocker = BodyBlocker(method, 0, false, out bool hasLoop);
            if (blocker != null || !(hasLoop || AlwaysOptimized || Optimized(method))) return blocker;
            return BodyBlocker(method, 0, true, out _);
        }

        private static bool Optimized(MethodBase method) => (method.MethodImplementationFlags & MethodImplAttributes.AggressiveOptimization) != 0;

        // followCalls=false: このメソッド自身の静的フィールド参照だけを見る。
        // followCalls=true: インライン展開され得る呼び先の本体も再帰的に見る。
        private string BodyBlocker(MethodBase method, int depth, bool followCalls, out bool hasLoop)
        {
            hasLoop = false;
            MethodBody body;
            try { body = method.GetMethodBody(); }
            catch { return "#nobody"; }

            byte[] il = body?.GetILAsByteArray();
            if (il == null) return depth == 0 ? "#nobody" : null;

            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType is { IsGenericType: true } dt ? dt.GetGenericArguments() : null;
            Type[] methodArgs = method is MethodInfo { IsGenericMethod: true } mi ? mi.GetGenericArguments() : null;

            var pos = 0;

            while (pos < il.Length)
            {
                OpCode? decoded = il[pos] == 0xFE && pos + 1 < il.Length ? _two[il[pos + 1]] : _one[il[pos]];
                if (decoded is not { } op) return "#il";
                pos += op.Size;

                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                        if ((sbyte)il[pos] < 0) hasLoop = true;
                        pos += 1;
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        pos += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        pos += 8;
                        break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, pos);
                        for (var i = 0; i < count; i++)
                            if (BitConverter.ToInt32(il, pos + 4 + (i * 4)) < 0) hasLoop = true;
                        pos += 4 + (count * 4);
                        break;
                    case OperandType.InlineBrTarget:
                        if (BitConverter.ToInt32(il, pos) < 0) hasLoop = true;
                        pos += 4;
                        break;
                    case OperandType.InlineField:
                        if (op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld)
                        {
                            FieldInfo field;
                            try { field = module.ResolveField(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); }
                            catch { return "#resolve"; }

                            Type owner = field?.DeclaringType;
                            if (owner == null) return "#resolve";
                            if (!TypeOk(owner))
                            {
                                _blockerType = owner;
                                return owner.IsGenericType ? owner.GetGenericTypeDefinition().FullName : owner.FullName;
                            }
                        }

                        pos += 4;
                        break;
                    case OperandType.InlineMethod:
                        if (followCalls)
                        {
                            MethodBase callee;
                            try { callee = module.ResolveMethod(BitConverter.ToInt32(il, pos), typeArgs, methodArgs); }
                            catch { return "#resolve"; }

                            string blocker = InlineeBlocker(callee, depth + 1);
                            if (blocker != null) return blocker;
                        }

                        pos += 4;
                        break;
                    default:
                        pos += 4;
                        break;
                }
            }

            return null;
        }

        private string InlineeBlocker(MethodBase callee, int depth)
        {
            if (callee == null) return "#resolve";
            if (IsBcl(callee.Module.Assembly)) return null;
            if (_inlineeBlocker.TryGetValue(callee, out string cached)) return cached;

            string result;
            MethodImplAttributes impl = callee.MethodImplementationFlags;

            if (callee.IsAbstract || (impl & MethodImplAttributes.NoInlining) != 0 || (callee.Attributes & MethodAttributes.PinvokeImpl) != 0)
                result = null;
            else
            {
                byte[] il = null;
                try { il = callee.GetMethodBody()?.GetILAsByteArray(); }
                catch { }

                if (il == null) result = null;
                else if (il.Length > InlineIlLimit && (impl & MethodImplAttributes.AggressiveInlining) == 0) result = null;
                else if (depth >= MaxInlineDepth) result = "#depth";
                else
                {
                    _inlineeBlocker[callee] = null; // 再帰呼び出しの打ち切り
                    result = BodyBlocker(callee, depth, true, out _);
                }
            }

            _inlineeBlocker[callee] = result;
            return result;
        }

        private static bool IsBcl(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return name != null && (name == "System.Private.CoreLib" || name == "netstandard" || name == "mscorlib" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal));
        }

        // この型の初期化子を裏スレッドで走らせてよいか。
        private bool TypeOk(Type type)
        {
            if (_typeOk.TryGetValue(type, out bool ok)) return ok;
            if (_preInited.Contains(type)) return _typeOk[type] = true;

            ConstructorInfo cctor;
            try { cctor = type.TypeInitializer; }
            catch { return _typeOk[type] = false; }

            if (cctor == null) return _typeOk[type] = true;
            if (IsBcl(type.Assembly)) return _typeOk[type] = true;
            if (type.Assembly != _self || type.IsGenericType) return _typeOk[type] = false;

            // 互いを参照し合う型の打ち切りのため、調べている間は仮に通しておく。仮の値を見て通した型は、
            // 一番外側の判定が不可で終わったら取り消す (次に聞かれた時に調べ直す)。
            if (_typeDepth == 0) _provisional.Clear();
            _typeOk[type] = true;
            _typeDepth++;
            bool result;
            try { result = InitializerCallsOnly(cctor, earlyOnMain: false); }
            finally { _typeDepth--; }

            if (result) _provisional.Add(type);

            if (_typeDepth == 0 && !result)
            {
                foreach (Type leaned in _provisional) _typeOk.Remove(leaned);
                _provisional.Clear();
            }

            return _typeOk[type] = result;
        }

        // earlyOnMain=false: 裏スレッドで走らせてよい初期化子か。標準ライブラリ (スレッド系を除く) と、
        //   同じ条件を満たす自前の型の静的フィールドしか触らないこと。
        // earlyOnMain=true: 主スレッドで前倒しに走らせてよい初期化子か。il2cpp 側は値型 (Color / Vector など)
        //   のメンバーしか呼ばないこと。自前のメソッド呼び出しは許す。
        private bool InitializerCallsOnly(ConstructorInfo cctor, bool earlyOnMain)
        {
            byte[] il;
            try { il = cctor.GetMethodBody()?.GetILAsByteArray(); }
            catch { return false; }

            if (il == null) return false;
            Module module = cctor.Module;
            var pos = 0;

            while (pos < il.Length)
            {
                OpCode? decoded = il[pos] == 0xFE && pos + 1 < il.Length ? _two[il[pos + 1]] : _one[il[pos]];
                if (decoded is not { } op) return false;
                pos += op.Size;

                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        pos += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        pos += 8;
                        break;
                    case OperandType.InlineSwitch:
                        pos += 4 + (BitConverter.ToInt32(il, pos) * 4);
                        break;
                    case OperandType.InlineField:
                        try
                        {
                            FieldInfo field = module.ResolveField(BitConverter.ToInt32(il, pos));
                            if (field?.DeclaringType == null) return false;
                            if (!earlyOnMain && field.IsStatic && field.DeclaringType != cctor.DeclaringType && !TypeOk(field.DeclaringType)) return false;
                        }
                        catch { return false; }

                        pos += 4;
                        break;
                    case OperandType.InlineMethod:
                        if (op == OpCodes.Ldftn)
                        {
                            pos += 4;
                            break;
                        }

                        try
                        {
                            MethodBase callee = module.ResolveMethod(BitConverter.ToInt32(il, pos));
                            Type owner = callee?.DeclaringType;
                            if (owner == null) return false;

                            if (earlyOnMain)
                            {
                                if (!IsBcl(owner.Assembly) && owner.Assembly != _self && !owner.IsValueType) return false;
                            }
                            else if (IsBcl(owner.Assembly))
                            {
                                string ns = owner.Namespace ?? "";
                                if (ns.StartsWith("System.Threading", StringComparison.Ordinal) || callee.Name.Contains("Thread")) return false;
                            }
                            else if (owner.Assembly != _self || !callee.IsConstructor || !owner.Name.StartsWith("<", StringComparison.Ordinal))
                                return false;
                        }
                        catch { return false; }

                        pos += 4;
                        break;
                    default:
                        pos += 4;
                        break;
                }
            }

            return true;
        }
    }
}
