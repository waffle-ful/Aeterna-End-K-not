using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;

namespace EndKnot.Modules;

// TestBridge の deadrefs: mod 側 static から辿れる破棄済み Unity オブジェクトのラッパーを、持ち主の static ごとに数える
internal static class DeadRefWalker
{
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags InstFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }

    private static HashSet<object> _visited;
    private static Dictionary<string, (int dead, int alive, string sample)> _byRoot;
    private static string _root;
    private static int _budget;

    public static void Run()
    {
        _visited = new HashSet<object>(new RefEq());
        _byRoot = new Dictionary<string, (int, int, string)>();
        _budget = 2_000_000;
        Assembly asm = typeof(DeadRefWalker).Assembly;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

        foreach (Type t in types)
        {
            // static の反射読みは型の静的コンストラクタを走らせるので、副作用の無い型だけ見る
            if (t.ContainsGenericParameters || !DeadWrapperSweep.HasTrivialTypeInitializer(t)) continue;
            FieldInfo[] fields;
            try { fields = t.GetFields(StaticFlags); } catch { continue; }
            foreach (FieldInfo f in fields)
            {
                if (f.IsLiteral) continue;
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                if (v == null) continue;
                _root = t.FullName + "." + f.Name;
                Visit(v, 0);
            }
        }

        var sb = new StringBuilder($"DEADREFS visited={_visited.Count} budgetLeft={_budget} ");
        foreach (var kv in _byRoot.Where(x => x.Value.dead > 0).OrderByDescending(x => x.Value.dead).Take(60))
            sb.Append(kv.Key).Append('=').Append(kv.Value.dead).Append('/').Append(kv.Value.dead + kv.Value.alive).Append('(').Append(kv.Value.sample).Append(") ");
        HealthLog.Note(sb.ToString().TrimEnd());
        _visited = null;
        _byRoot = null;
    }

    private static void Visit(object v, int depth)
    {
        if (v == null || --_budget < 0) return;
        Type t = v.GetType();
        if (t.IsPrimitive || t.IsEnum || v is string || v is Type || v is MemberInfo || v is Assembly) return;
        if (!t.IsValueType && !_visited.Add(v)) return;

        if (v is Il2CppObjectBase ob)
        {
            if (v is UnityEngine.Object uo)
            {
                bool dead;
                try { dead = uo == null; } catch { dead = true; }
                _byRoot.TryGetValue(_root, out var r);
                _byRoot[_root] = dead ? (r.dead + 1, r.alive, r.sample ?? t.Name) : (r.dead, r.alive + 1, r.sample);
            }
            return;
        }

        if (depth > 8) return;

        if (v is Delegate d)
        {
            foreach (Delegate x in d.GetInvocationList())
                if (x.Target != null) Visit(x.Target, depth + 1);
            return;
        }

        if (v is IEnumerable en && !(v is Il2CppObjectBase))
        {
            int n = 0;
            try
            {
                foreach (object e in en)
                {
                    if (++n > 200_000) break;
                    if (e == null) continue;
                    Type et = e.GetType();
                    if (et.IsGenericType && et.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        Visit(et.GetProperty("Key").GetValue(e), depth + 1);
                        Visit(et.GetProperty("Value").GetValue(e), depth + 1);
                    }
                    else Visit(e, depth + 1);
                }
            }
            catch { }
            return;
        }

        string ns = t.Namespace ?? "";
        if (ns.StartsWith("System", StringComparison.Ordinal) && !t.IsGenericType && !ns.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal)) return;

        for (Type c = t; c != null && c != typeof(object); c = c.BaseType)
        {
            FieldInfo[] fs;
            try { fs = c.GetFields(InstFlags | BindingFlags.DeclaredOnly); } catch { continue; }
            foreach (FieldInfo f in fs)
            {
                Type ft = f.FieldType;
                if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft.IsPointer || ft == typeof(IntPtr)) continue;
                object fv;
                try { fv = f.GetValue(v); } catch { continue; }
                if (fv != null) Visit(fv, depth + 1);
            }
        }
    }
}
