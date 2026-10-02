using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace EndKnot.Modules;

// ゲーム本体 (il2cpp) のメソッドを、生成済みラッパーを通さずコンパイル済み関数のアドレスで直接呼ぶ。
// ラッパーは il2cpp_runtime_invoke を経由し、値型の戻り値 (bool / float / Vector3 …) を 1 回ごとに
// ゲーム側ヒープへ箱詰めして返す (1 呼び 32B)。毎フレーム経路ではこの箱が GC の主な燃料になる。
// il2cpp が吐く関数は C の呼出規約で (this, 引数…, MethodInfo*) を取るので、関数ポインタで呼べば箱は作られない。
//
// 直接呼びは runtime_invoke がやっていた保護を外す:
//   ・ゲーム側で例外が投げられると managed 側で捕まえられずプロセスが落ちる → 破棄済みオブジェクトに対して呼ばない
//   ・仮想呼び出しの解決をしない → 解決した型の実装がそのまま走る (override を持つメソッドには使わない)
// だから対象は「生存確認済みのオブジェクトに対する、例外を投げない非仮想の読み取り」に限る。
public static unsafe class Il2Direct
{
    private const string Tag = "Il2Direct";

    public readonly struct Method
    {
        public readonly IntPtr Info; // MethodInfo* (末尾の隠し引数として渡す)
        public readonly IntPtr Ptr;  // コンパイル済み関数の先頭

        public Method(IntPtr info, IntPtr ptr)
        {
            Info = info;
            Ptr = ptr;
        }

        public bool Ok => Ptr != IntPtr.Zero;
    }

    private static int _cachedPtrOffset = -1;
    private const int AliveCheckCount = 64;
    private static int _aliveChecks;

    // false の間は全部ラッパー経路へ落とす (同じ実行ファイルのまま効果を比べるための切り替え)
    public static bool Enabled = true;

    /// <summary>
    /// T (または親クラス) の name メソッドを解決する。見つからなければ Ok=false (呼び出し側はラッパー経路へ)。
    /// 同名・同引数数のオーバーロードがある時は firstParamType (例 "UnityEngine.KeyCode") で 1 本に絞る。
    /// </summary>
    public static Method Resolve<T>(string name, int argc, string firstParamType = null)
    {
        try
        {
            IntPtr klass = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (klass == IntPtr.Zero) return default;

            IntPtr info = firstParamType == null ? IL2CPP.il2cpp_class_get_method_from_name(klass, name, argc) : FindOverload(klass, name, argc, firstParamType);
            if (info == IntPtr.Zero) return default;

            // 静的メソッドは型初期化子が済んでいる前提で吐かれている (runtime_invoke はここを肩代わりしていた)
            IL2CPP.il2cpp_runtime_class_init(klass);

            // MethodInfo の先頭フィールドが関数アドレス
            IntPtr ptr = *(IntPtr*)info;
            return new Method(info, ptr);
        }
        catch (Exception e)
        {
            Logger.Warn($"resolve {typeof(T).Name}.{name} failed: {e.Message}", Tag);
            return default;
        }
    }

    private static IntPtr FindOverload(IntPtr klass, string name, int argc, string firstParamType)
    {
        IntPtr iter = IntPtr.Zero;

        while (true)
        {
            IntPtr m = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (m == IntPtr.Zero) return IntPtr.Zero;
            if (IL2CPP.il2cpp_method_get_param_count(m) != argc) continue;
            if (Marshal.PtrToStringUTF8(IL2CPP.il2cpp_method_get_name(m)) != name) continue;
            if (Marshal.PtrToStringUTF8(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_param(m, 0))) == firstParamType) return m;
        }
    }

    public static bool Bool(in Method m, IntPtr self) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte>)m.Ptr)(self, m.Info) != 0;
    public static int Int(in Method m, IntPtr self) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)m.Ptr)(self, m.Info);
    public static float Float(in Method m, IntPtr self) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float>)m.Ptr)(self, m.Info);
    public static Vector2 V2(in Method m, IntPtr self) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, Vector2>)m.Ptr)(self, m.Info);
    public static Vector3 V3(in Method m, IntPtr self) => ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, Vector3>)m.Ptr)(self, m.Info);

    /// <summary>
    /// UnityEngine.Object が生きているか (Destroy 済みでないか) を、フィールド 1 個の読みだけで答える。
    /// `if (obj)` / `obj != null` はどちらもゲーム側の比較メソッドを呼ぶので 1 回ごとに箱が出る。
    /// </summary>
    public static bool Alive(UnityEngine.Object o)
    {
        if (o is null) return false;
        if (!Enabled) return o;
        IntPtr p = o.Pointer;
        if (p == IntPtr.Zero) return false;

        int off = _cachedPtrOffset;
        if (off < 0) off = ResolveCachedPtrOffset();
        if (off == 0) return o; // オフセットが取れない環境では従来の判定

        bool alive = *(IntPtr*)((byte*)p + off) != IntPtr.Zero;

        // 最初の数十回だけ従来の判定と突き合わせ、食い違う環境では以後ずっと従来の判定を使う
        if (_aliveChecks < AliveCheckCount)
        {
            _aliveChecks++;
            bool wrapped = o;

            if (alive != wrapped)
            {
                _cachedPtrOffset = 0;
                Logger.Warn($"direct alive check disabled (offset {off} disagrees with the engine)", Tag);
                return wrapped;
            }
        }

        return alive;
    }

    // ---- 作り置きのゲーム側文字列を TMP へ渡す ----
    // ラッパーの `tmp.text = s` は呼ぶたびに s をゲーム側ヒープへ複製する (同じ managed 文字列でも毎回新しい)。
    // 決まった文字列を繰り返し差し替える表示では、ゲーム側の文字列を 1 回だけ作って握り、その参照を渡す。
    // 呼び出しは runtime_invoke 経由のまま (仮想呼び出しの解決と例外の捕捉はラッパーと同じ) で、引数の複製だけを省く。

    public readonly struct PinnedString
    {
        public readonly IntPtr Ptr;
        public readonly IntPtr Handle;
        public readonly string Managed;

        public PinnedString(string s)
        {
            Managed = s;
            Ptr = IL2CPP.ManagedStringToIl2Cpp(s);
            // ゲーム側の GC に回収させないため強参照のハンドルで握る (Boehm は動かさないのでポインタはそのまま使える)
            Handle = Ptr == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_gchandle_new(Ptr, false);
        }

        public void Free()
        {
            if (Handle != IntPtr.Zero) IL2CPP.il2cpp_gchandle_free(Handle);
        }
    }

    private static IntPtr _setTextInfo;
    private static int _setTextState; // 0 = 未解決 / 1 = 使える / 2 = 使えない (ラッパー経路のまま)

    /// <summary>tmp.text = s.Managed と同じ。作り置きの文字列を渡すのでゲーム側ヒープに新しい文字列を作らない。</summary>
    public static void SetText(TMPro.TMP_Text tmp, in PinnedString s)
    {
        if (!Enabled || s.Handle == IntPtr.Zero || !SetTextReady())
        {
            tmp.text = s.Managed;
            return;
        }

        IntPtr obj = tmp.Pointer;
        IntPtr method = IL2CPP.il2cpp_object_get_virtual_method(obj, _setTextInfo);
        void** args = stackalloc void*[1];
        args[0] = (void*)s.Ptr;
        IntPtr exc = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(method, obj, args, ref exc);
        Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exc);
    }

    private static bool SetTextReady()
    {
        if (_setTextState != 0) return _setTextState == 1;

        try
        {
            IntPtr klass = Il2CppClassPointerStore<TMPro.TMP_Text>.NativeClassPtr;
            _setTextInfo = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, "set_text", 1);
        }
        catch (Exception e) { Logger.Warn($"resolve TMP_Text.set_text failed: {e.Message}", Tag); }

        _setTextState = _setTextInfo != IntPtr.Zero ? 1 : 2;
        return _setTextState == 1;
    }

    // ---- 毎フレーム何十回も読まれる静的な読み取り ----

    private static Method _screenWidth, _screenHeight, _getKey, _getKeyDown;
    private static int _staticsState; // 0 = 未解決 / 1 = 使える / 2 = 使えない (ラッパー経路のまま)

    public static int ScreenWidth => Enabled && StaticsReady() ? ((delegate* unmanaged[Cdecl]<IntPtr, int>)_screenWidth.Ptr)(_screenWidth.Info) : Screen.width;
    public static int ScreenHeight => Enabled && StaticsReady() ? ((delegate* unmanaged[Cdecl]<IntPtr, int>)_screenHeight.Ptr)(_screenHeight.Info) : Screen.height;
    public static bool GetKey(KeyCode key)
    {
        if (!Enabled || !StaticsReady()) return Input.GetKey(key);
        bool v = ((delegate* unmanaged[Cdecl]<int, IntPtr, byte>)_getKey.Ptr)((int)key, _getKey.Info) != 0;
        if (_shadowLeft > 0) Shadow(v, Input.GetKey(key));
        return v;
    }

    public static bool GetKeyDown(KeyCode key)
    {
        if (!Enabled || !StaticsReady()) return Input.GetKeyDown(key);
        bool v = ((delegate* unmanaged[Cdecl]<int, IntPtr, byte>)_getKeyDown.Ptr)((int)key, _getKeyDown.Info) != 0;
        if (_shadowLeft > 0) Shadow(v, Input.GetKeyDown(key));
        return v;
    }

    // キー読みの突き合わせ窓: 指定回数ぶん、直接呼びの結果をラッパー経路と同じフレーム内で比べて数える。
    // 起動時の確認は「押されていない」側しか通らないので、押された側は実際に押して確かめる。
    private static int _shadowLeft, _shadowTrue, _shadowMismatch;

    public static void ShadowStart(int calls)
    {
        _shadowTrue = 0;
        _shadowMismatch = 0;
        _shadowLeft = calls;
    }

    public static string ShadowReport() => $"OK il2direct shadow left={_shadowLeft} true={_shadowTrue} mismatch={_shadowMismatch} ready={_staticsState} aliveOffset={_cachedPtrOffset} aliveChecks={_aliveChecks}";

    // 突き合わせ窓の間だけ、代表的なキーを毎 tick 自分で読む (キー読みの呼び出し元が動いていない画面・環境でも窓を進めるため)
    public static void ShadowPoll()
    {
        if (_shadowLeft <= 0) return;

        // 直接呼びが使われていない間は比べる相手が無く、窓が減らない
        if (!Enabled || !StaticsReady())
        {
            _shadowLeft = 0;
            return;
        }

        GetKey(KeyCode.Mouse0);
        GetKeyDown(KeyCode.Mouse0);
        GetKey(KeyCode.Escape);
        GetKeyDown(KeyCode.Escape);
        GetKey(KeyCode.Tab);
        GetKeyDown(KeyCode.Delete);
        if (ScreenWidth != Screen.width || ScreenHeight != Screen.height) _shadowMismatch++;
    }

    private static void Shadow(bool direct, bool wrapped)
    {
        _shadowLeft--;
        if (wrapped) _shadowTrue++;
        if (direct != wrapped) _shadowMismatch++;
    }

    private static bool StaticsReady()
    {
        if (_staticsState != 0) return _staticsState == 1;

        _staticsState = 2;
        _screenWidth = Resolve<Screen>("get_width", 0);
        _screenHeight = Resolve<Screen>("get_height", 0);
        _getKey = Resolve<Input>("GetKey", 1, "UnityEngine.KeyCode");
        _getKeyDown = Resolve<Input>("GetKeyDown", 1, "UnityEngine.KeyCode");
        if (!_screenWidth.Ok || !_screenHeight.Ok || !_getKey.Ok || !_getKeyDown.Ok) return Fail("resolve");

        // 引数の渡し方・戻り値の受け方がこの環境で合っているかを、ラッパー経路と同じフレーム内で突き合わせる
        // ラッパー経路を先に通す: エンジン側に実体が無い環境なら、ここで捕まえられる例外になって直接呼びまで進まない
        int w = Screen.width, h = Screen.height;
        if (((delegate* unmanaged[Cdecl]<IntPtr, int>)_screenWidth.Ptr)(_screenWidth.Info) != w) return Fail("width");
        if (((delegate* unmanaged[Cdecl]<IntPtr, int>)_screenHeight.Ptr)(_screenHeight.Info) != h) return Fail("height");

        ReadOnlySpan<KeyCode> probe = stackalloc KeyCode[] { KeyCode.None, KeyCode.Space, KeyCode.LeftControl, KeyCode.LeftShift, KeyCode.Mouse0, KeyCode.A };

        foreach (KeyCode k in probe)
        {
            bool held = Input.GetKey(k), down = Input.GetKeyDown(k);
            if ((((delegate* unmanaged[Cdecl]<int, IntPtr, byte>)_getKey.Ptr)((int)k, _getKey.Info) != 0) != held) return Fail("GetKey");
            if ((((delegate* unmanaged[Cdecl]<int, IntPtr, byte>)_getKeyDown.Ptr)((int)k, _getKeyDown.Info) != 0) != down) return Fail("GetKeyDown");
        }

        _staticsState = 1;
        return true;

        static bool Fail(string what)
        {
            Logger.Warn($"direct static calls disabled ({what})", Tag);
            return false;
        }
    }

    private static int ResolveCachedPtrOffset()
    {
        try
        {
            IntPtr klass = Il2CppClassPointerStore<UnityEngine.Object>.NativeClassPtr;
            IntPtr field = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_field_from_name(klass, "m_CachedPtr");
            uint off = field == IntPtr.Zero ? 0 : IL2CPP.il2cpp_field_get_offset(field);
            _cachedPtrOffset = off is 0 or uint.MaxValue ? 0 : (int)off;
        }
        catch
        {
            _cachedPtrOffset = 0;
        }

        return _cachedPtrOffset;
    }

    // ラッパー経路と直接呼びを同じ対象・同じ回数で回し、ゲーム側ヒープの増分と所要時間を並べる。
    // 値が一致しなければ mismatch に数える (呼出規約や構造体の返し方がこの環境で合っているかの確認を兼ねる)。
    public static string Bench(int n)
    {
        Camera cam = Camera.main;
        if (!cam) return "ERR il2direct no camera";

        Transform tr = cam.transform;
        IntPtr camPtr = cam.Pointer;
        IntPtr trPtr = tr.Pointer;

        Method mEnabled = Resolve<Behaviour>("get_enabled", 0);
        Method mOrtho = Resolve<Camera>("get_orthographicSize", 0);
        Method mPos = Resolve<Transform>("get_position", 0);
        if (!mEnabled.Ok || !mOrtho.Ok || !mPos.Ok) return $"ERR il2direct resolve enabled={mEnabled.Ok} ortho={mOrtho.Ok} pos={mPos.Ok}";

        int mismatch = 0;
        if (cam.enabled != Bool(mEnabled, camPtr)) mismatch++;
        if (cam.orthographicSize != Float(mOrtho, camPtr)) mismatch++;
        Vector3 pw = tr.position, pd = V3(mPos, trPtr);
        if (pw.x != pd.x || pw.y != pd.y || pw.z != pd.z) mismatch++;
        if ((bool)cam != Alive(cam)) mismatch++;

        // 破棄済みの側: 生きている対象だけでは、読む位置がずれていても一致してしまう
        var dead = new GameObject("Il2DirectProbe");
        UnityEngine.Object.DestroyImmediate(dead);
        if (Alive(dead) || dead) mismatch++;

        var sb = new System.Text.StringBuilder(256);
        sb.Append("OK il2direct n=").Append(n).Append(" mismatch=").Append(mismatch);
        if (mismatch != 0) return sb.ToString();

        int sink = 0;
        float fsink = 0f;

        Run(sb, "bool.wrap", n, () => { for (int i = 0; i < n; i++) if (cam.enabled) sink++; });
        Run(sb, "bool.direct", n, () => { for (int i = 0; i < n; i++) if (Bool(mEnabled, camPtr)) sink++; });
        Run(sb, "float.wrap", n, () => { for (int i = 0; i < n; i++) fsink += cam.orthographicSize; });
        Run(sb, "float.direct", n, () => { for (int i = 0; i < n; i++) fsink += Float(mOrtho, camPtr); });
        Run(sb, "v3.wrap", n, () => { for (int i = 0; i < n; i++) fsink += tr.position.x; });
        Run(sb, "v3.direct", n, () => { for (int i = 0; i < n; i++) fsink += V3(mPos, trPtr).x; });
        Run(sb, "alive.wrap", n, () => { for (int i = 0; i < n; i++) if (cam) sink++; });
        Run(sb, "alive.direct", n, () => { for (int i = 0; i < n; i++) if (Alive(cam)) sink++; });

        sb.Append(" sink=").Append(sink + (int)fsink);
        return sb.ToString();
    }

    private static void Run(System.Text.StringBuilder sb, string name, int n, Action body)
    {
        int gc0 = GcPrepass.BoehmCollectionCount();
        long il0 = GcPrepass.BoehmUsedBytes();
        long m0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();
        body();
        long t1 = Stopwatch.GetTimestamp();
        long m1 = GC.GetAllocatedBytesForCurrentThread();
        long il1 = GcPrepass.BoehmUsedBytes();
        int gc1 = GcPrepass.BoehmCollectionCount();

        double ns = (t1 - t0) * 1e9 / Stopwatch.Frequency / n;
        // 区間内でゲーム側 GC が走ると使用量の差は意味を失う → 回数を併記して読む側で捨てる
        sb.Append(" | ").Append(name)
            .Append(" il2B/call=").Append(((il1 - il0) / (double)n).ToString("F1"))
            .Append(" mgdB/call=").Append(((m1 - m0) / (double)n).ToString("F1"))
            .Append(" ns/call=").Append(ns.ToString("F0"))
            .Append(" bgc=").Append(gc1 - gc0);
    }
}
