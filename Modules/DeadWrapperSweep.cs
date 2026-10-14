using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace EndKnot.Modules;

// mod 側の参照が Destroy 済みの Unity オブジェクトのラッパーを握り続けると、ラッパーの GCHandle が
// il2cpp 側の本体 (TMP なら文字情報の配列、ボタンなら押下イベントの先のメニュー一式) を生かし続ける。
// il2cpp の GC は incremental を切っているので、毎回の停止はポインタを含む生存量にほぼ比例する
// (実測: 1MB ≒ 0.44ms)。画面が切り替わった直後にだけ見回り、死んだ参照を外す。
//
// 1. ラムダの捕捉変数: ボタンの押下イベント等へ渡したラムダは il2cpp 側のデリゲートから GCHandle で
//    固定され、ラムダが捕まえたラッパーが il2cpp 側のボタン一式を固定する輪になる (どちらの GC からも
//    相手側が根に見えるので永久に残る)。変換の瞬間に捕捉オブジェクトを弱参照で覚えておき、破棄済みの
//    オブジェクトを捕まえている捕捉変数を null にして輪を切る。
// 2. EK の static フィールド: 直接の参照は null、List は全要素が破棄済みの時だけ空に、Dictionary /
//    HashSet は破棄済みのキーか値を持つ項目を外す (次の参照で作り直される)。配列と、生きた要素が
//    混ざった List は触らない (件数・添字や null を想定しない走査を変えないため)。
//    反射で static を読むと型の静的コンストラクタが走るので、静的コンストラクタが無いか、コレクションの
//    生成と代入しかしない型だけを対象にする。null を後始末の合図に使うフィールドは
//    [KeepDestroyedReference] で外す。
// 外したラッパーの GCHandle は CoreCLR のファイナライザで解放されるので、外した時だけ背景 GC を頼む。
internal static class DeadWrapperSweep
{
    private const long DelaySeconds = 2;

    private static volatile FieldInfo[] _fields;
    private static volatile bool _building;
    private static long _dueTs;
    private static int _skippedTypes;
    private static List<string> _skippedNames;

    // Android では null 参照がフリーズになりうる (エミュの翻訳層で例外にならない) ので、実機で確かめるまで PC だけ
    internal static bool Enabled => !OperatingSystem.IsAndroid() && (Main.SweepDestroyedReferences?.Value ?? false);

    internal static void OnStateChange(string from, string to)
    {
        if (Enabled && _fields == null && !_building)
        {
            _building = true;
            Task.Run(Build);
        }

        if (to is "Menu" or "Lobby" or "InTask") _dueTs = Utils.TimeStamp + DelaySeconds;
    }

    // HealthLog.Tick から (秒単位で判定)
    internal static void Tick(long now)
    {
        if (_dueTs == 0 || now < _dueTs) return;
        if (Enabled && _fields == null && _building) return; // 列挙が終わるまで持ち越す
        _dueTs = 0;

        // 機構の切り替えとは関係なく、持ち主の消えた層は手放す
        try { LobbyCodeRainbow.ReleaseIfOrphaned(); }
        catch (Exception e) { Utils.ThrowException(e); }

        if (!Enabled || _fields == null) return;

        try { Sweep(); }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void Build()
    {
        try
        {
            Type uo = typeof(UnityEngine.Object);
            Type[] types;
            try { types = typeof(DeadWrapperSweep).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }

            var list = new List<FieldInfo>(512);

            foreach (Type t in types)
            {
                try
                {
                    if (t == null || t.ContainsGenericParameters) continue;

                    FieldInfo[] fs = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    var mine = new List<FieldInfo>();

                    foreach (FieldInfo f in fs)
                    {
                        try
                        {
                            if (f.IsLiteral || f.IsDefined(typeof(KeepDestroyedReferenceAttribute), false)) continue;
                            Type ft = f.FieldType;

                            if (uo.IsAssignableFrom(ft))
                            {
                                if (!f.IsInitOnly) mine.Add(f);
                                continue;
                            }

                            if (!ft.IsGenericType) continue;

                            Type def = ft.GetGenericTypeDefinition();
                            if (def != typeof(List<>) && def != typeof(Dictionary<,>) && def != typeof(HashSet<>)) continue;

                            foreach (Type a in ft.GetGenericArguments())
                            {
                                if (!uo.IsAssignableFrom(a)) continue;
                                mine.Add(f);
                                break;
                            }
                        }
                        catch { }
                    }

                    if (mine.Count == 0) continue;

                    if (!HasTrivialTypeInitializer(t))
                    {
                        _skippedTypes++;
                        (_skippedNames ??= new List<string>()).Add(t.Name);
                        continue;
                    }

                    list.AddRange(mine);
                }
                catch { }
            }

            _fields = list.ToArray();
            if (_skippedNames != null) Logger.Info($"skipped (non-trivial static initializer): {string.Join(", ", _skippedNames)}", "DeadWrapperSweep");
        }
        catch (Exception e) { Logger.Warn($"field scan failed: {e.Message}", "DeadWrapperSweep"); }
        finally { _building = false; }
    }

    // 静的コンストラクタが無いか、定数・文字列の読み込み・BCL の基本型とコレクションの生成と操作・
    // Unity の値型 (Vector3 / Color 等) の生成・自分の static への代入・デリゲートの生成しかしない型だけ true。
    // 他の型の static を読む・mod や Unity のメソッドを呼ぶ・デリゲートを実行しうる (Linq や
    // デリゲート型の引数を取るメソッド) 初期化は、見回りから走らせると準備前の状態で評価されうるので対象外にする。
    private static readonly HashSet<string> SafeNamespaces =
    [
        "System", "System.Collections.Generic", "System.Collections.Concurrent", "System.Collections.ObjectModel",
        "System.Text", "System.Text.RegularExpressions", "System.Runtime.CompilerServices", "System.Globalization"
    ];

    private static bool IsSafeCall(MethodBase m)
    {
        Type d = m.DeclaringType;
        if (d == null) return false;

        if (typeof(Delegate).IsAssignableFrom(d)) return m.IsConstructor; // 生成だけ (Invoke は不可)

        if (d.Namespace == "UnityEngine" && d.IsValueType && m.IsConstructor) return true;

        if (!SafeNamespaces.Contains(d.Namespace ?? "")) return false;
        if (d == typeof(Activator) || d == typeof(Type) || d == typeof(AppDomain) || d == typeof(GC) || d == typeof(Environment)) return false;
        if (d == typeof(RuntimeHelpers) && m.Name != nameof(RuntimeHelpers.InitializeArray)) return false;

        // デリゲートを受け取るメソッド (List.ForEach / Array.ConvertAll / Lazy 等) はその場で実行しうる。
        // Dictionary.Add の TValue のようにジェネリック引数として渡るだけのものは、宣言側で判定する。
        MethodBase declared = m;
        if (d.IsGenericType && !d.IsGenericTypeDefinition)
        {
            try { declared = MethodBase.GetMethodFromHandle(m.MethodHandle, d.GetGenericTypeDefinition().TypeHandle) ?? m; }
            catch { declared = m; }
        }

        foreach (ParameterInfo p in declared.GetParameters())
        {
            if (!p.ParameterType.IsGenericParameter && typeof(Delegate).IsAssignableFrom(p.ParameterType)) return false;
        }

        return true;
    }
    private static Dictionary<short, OpCode> _opcodes;

    internal static bool HasTrivialTypeInitializer(Type t)
    {
        ConstructorInfo cctor = t.TypeInitializer;
        if (cctor == null) return true;

        byte[] il;
        try { il = cctor.GetMethodBody()?.GetILAsByteArray(); }
        catch { return false; }
        if (il == null) return false;

        if (_opcodes == null)
        {
            var map = new Dictionary<short, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is OpCode op) map[op.Value] = op;
            }

            _opcodes = map;
        }

        Module module = t.Module;
        int i = 0;

        while (i < il.Length)
        {
            short code = il[i] == 0xFE && i + 1 < il.Length ? (short)(0xFE00 | il[i + 1]) : il[i];
            if (!_opcodes.TryGetValue(code, out OpCode op)) return false;
            i += op.Size;

            int operand = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => -1,
                _ => 4
            };

            if (operand < 0) return false;

            int token = operand == 4 && i + 4 <= il.Length ? BitConverter.ToInt32(il, i) : 0;
            i += operand;

            if (op.OperandType == OperandType.InlineMethod)
            {
                MethodBase m;
                try { m = module.ResolveMethod(token, t.IsGenericType ? t.GetGenericArguments() : null, null); }
                catch { return false; }

                if (op == OpCodes.Jmp) return false;
                if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn) continue; // デリゲートの生成だけ (呼ばない)
                if (!IsSafeCall(m)) return false;
                continue;
            }

            if (op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld)
            {
                FieldInfo f;
                try { f = module.ResolveField(token, t.IsGenericType ? t.GetGenericArguments() : null, null); }
                catch { return false; }

                Type d = f.DeclaringType;
                if (d == t || (d != null && d.DeclaringType == t && d.IsDefined(typeof(CompilerGeneratedAttribute), false))) continue;
                if (op != OpCodes.Stsfld && SafeNamespaces.Contains(d?.Namespace ?? "")) continue;
                return false;
            }

            if (op == OpCodes.Calli) return false;
        }

        return true;
    }

    private static void Sweep()
    {
        var sw = Stopwatch.StartNew();
        int cleared = 0;

        foreach (FieldInfo f in _fields)
        {
            try
            {
                int n = f.GetValue(null) switch
                {
                    null => 0,
                    UnityEngine.Object o => Il2Direct.Alive(o) ? 0 : ClearField(f),
                    IList l => SweepList(l),
                    IDictionary d => SweepDictionary(d),
                    { } v => SweepSet(v)
                };

                if (n == 0) continue;
                cleared += n;

                // どの static に手が入ったかは初回だけ残す (実卓で初めて踏むものを後から追えるように)
                if (Touched.Add(f)) Logger.Info($"cleared {f.DeclaringType?.Name}.{f.Name} ({n})", "DeadWrapperSweep");
            }
            catch (Exception e) { Logger.Warn($"{f.DeclaringType?.Name}.{f.Name}: {e.Message}", "DeadWrapperSweep"); }
        }

        int captures = 0;
        try { captures = SweepCaptures(); }
        catch (Exception e) { Utils.ThrowException(e); }

        if (cleared + captures > 0) GC.Collect(2, GCCollectionMode.Forced, false);

        HealthLog.Note($"DEADSWEEP cleared={cleared} captures={captures} tracked={TrackedCount} fields={_fields.Length} skippedTypes={_skippedTypes} ms={sw.ElapsedMilliseconds}");
    }

    private static readonly HashSet<FieldInfo> Touched = new();

    private static int ClearField(FieldInfo f)
    {
        f.SetValue(null, null);
        return 1;
    }

    private const int MaxTracked = 20000;
    private static readonly List<WeakReference<object>> Captures = new(1024);
    private static readonly Dictionary<Type, FieldInfo[]> CaptureFields = new();

    // DelegateCaptureTrackPatch から
    internal static void TrackDelegate(Delegate d)
    {
        if (d == null) return;

        foreach (Delegate x in d.GetInvocationList())
        {
            object target = x.Target;
            if (target == null || CaptureFieldsOf(target.GetType()).Length == 0) continue;

            lock (Captures)
            {
                if (Captures.Count >= MaxTracked) PruneCaptures();
                if (Captures.Count >= MaxTracked) Captures.RemoveRange(0, MaxTracked / 4);

                Captures.Add(new WeakReference<object>(target));
            }
        }
    }

    // 捕捉クラス (コンパイラ生成) のうち、Unity オブジェクトか入れ子の捕捉クラスを持つフィールド。
    // `this` の捕捉は外す: 破棄済みの自クラスでも Unity を触らないメソッドは動き続けるので、null にすると
    // そこで落ちるようになる。
    private static FieldInfo[] CaptureFieldsOf(Type t)
    {
        lock (CaptureFields) return CaptureFieldsOfLocked(t);
    }

    private static FieldInfo[] CaptureFieldsOfLocked(Type t)
    {
        if (CaptureFields.TryGetValue(t, out FieldInfo[] fs)) return fs;

        fs = [];

        if (t.IsDefined(typeof(CompilerGeneratedAttribute), false))
        {
            var list = new List<FieldInfo>();

            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.Name == "<>4__this") continue;

                if (typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType) || (f.FieldType.IsClass && f.FieldType.IsDefined(typeof(CompilerGeneratedAttribute), false)))
                    list.Add(f);
            }

            fs = list.ToArray();
        }

        CaptureFields[t] = fs;
        return fs;
    }

    private static int TrackedCount { get { lock (Captures) return Captures.Count; } }

    private static void PruneCaptures() => Captures.RemoveAll(w => !w.TryGetTarget(out _));

    private static int SweepCaptures()
    {
        List<object> live = new();

        lock (Captures)
        {
            PruneCaptures();

            foreach (WeakReference<object> w in Captures)
            {
                if (w.TryGetTarget(out object target)) live.Add(target);
            }
        }

        int n = 0;
        foreach (object target in live) n += SweepCapture(target, 0);
        return n;
    }

    private static int SweepCapture(object target, int depth)
    {
        int n = 0;

        foreach (FieldInfo f in CaptureFieldsOf(target.GetType()))
        {
            try
            {
                object v = f.GetValue(target);

                if (v is UnityEngine.Object o)
                {
                    if (Il2Direct.Alive(o)) continue;
                    f.SetValue(target, null);
                    n++;
                }
                else if (v != null && depth < 3)
                    n += SweepCapture(v, depth + 1);
            }
            catch { }
        }

        return n;
    }

    private static bool Dead(object e) => e is UnityEngine.Object o && !Il2Direct.Alive(o);

    private static int SweepList(IList l)
    {
        int n = l.Count;
        if (n == 0) return 0;

        for (int i = 0; i < n; i++)
        {
            if (!Dead(l[i])) return 0;
        }

        l.Clear();
        return n;
    }

    private static int SweepDictionary(IDictionary d)
    {
        List<object> dead = null;

        foreach (DictionaryEntry e in d)
        {
            if (Dead(e.Key) || Dead(e.Value))
                (dead ??= new List<object>()).Add(e.Key);
        }

        if (dead == null) return 0;

        foreach (object k in dead) d.Remove(k);
        return dead.Count;
    }

    private static readonly Dictionary<Type, MethodInfo> SetRemove = new();

    private static int SweepSet(object v)
    {
        Type t = v.GetType();
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(HashSet<>)) return 0;

        List<object> dead = null;

        foreach (object e in (IEnumerable)v)
        {
            if (Dead(e))
                (dead ??= new List<object>()).Add(e);
        }

        if (dead == null) return 0;

        if (!SetRemove.TryGetValue(t, out MethodInfo remove))
            SetRemove[t] = remove = t.GetMethod("Remove");

        var arg = new object[1];

        foreach (object e in dead)
        {
            arg[0] = e;
            remove.Invoke(v, arg);
        }

        return dead.Count;
    }
}

// 破棄済みでも null にしてはいけない static (null かどうかを後始末の合図に使っている等) に付ける。
[AttributeUsage(AttributeTargets.Field)]
internal sealed class KeepDestroyedReferenceAttribute : Attribute;

// ラムダが il2cpp のデリゲートへ変換される時 (UnityEvent.AddListener や il2cpp フィールドへの代入の
// キャスト) に必ず通る、変換先の参照オブジェクトの生成口。
[HarmonyPatch]
internal static class DelegateCaptureTrackPatch
{
    private static MethodBase _target;

    internal static bool Prepare()
    {
        if (!Main.SweepDestroyedReferences.Value) return false;
        _target ??= ResolveTarget();
        if (_target == null) Logger.Warn("Il2CppToMonoDelegateReference(Delegate, IntPtr) not found", "DeadWrapperSweep");
        return _target != null;
    }

    internal static MethodBase TargetMethod() => _target ??= ResolveTarget();

    private static MethodBase ResolveTarget()
    {
        Type t = typeof(DelegateSupport).GetNestedType("Il2CppToMonoDelegateReference", BindingFlags.NonPublic | BindingFlags.Public);
        return t?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [typeof(Delegate), typeof(IntPtr)], null);
    }

    public static void Postfix(Delegate __0)
    {
        try { DeadWrapperSweep.TrackDelegate(__0); }
        catch { }
    }
}
