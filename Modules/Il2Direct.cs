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

    public static string ShadowReport()
    {
        var sb = new System.Text.StringBuilder(512);
        sb.Append($"OK il2direct shadow left={_shadowLeft} true={_shadowTrue} mismatch={_shadowMismatch} ready={_staticsState} aliveOffset={_cachedPtrOffset} aliveChecks={_aliveChecks} clock={_clockState}");

        // 読み取り口ごとの状態 (1 = 直接呼び / 2 = ラッパー経路に落ちた) と突き合わせ済みの回数
        foreach (Reader r in new[] { RPosition, ROrtho, RActiveSelf, RActiveAndEnabled, RColliderBounds, RSpriteBounds, RAmHost, RIsGameStarted, RIsGamePublic, RAmOwner, REventType })
            sb.Append(' ').Append(r.Name).Append('=').Append(r.State).Append('/').Append(r.Checks);

        return sb.ToString();
    }

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

    // ---- 毎フレーム読まれる Time / Input の静的な読み取り ----
    // 上のキー読みと別の組にしておく (片方が使えない環境でも、もう片方は直接呼びのまま使える)

    private static Method _unscaledTime, _realtime, _time, _deltaTime, _unscaledDeltaTime, _fixedDeltaTime, _frameCount;
    private static Method _mousePosition, _mouseScroll, _mouseButton, _mouseButtonDown, _mouseButtonUp;
    private static int _clockState; // 0 = 未解決 / 1 = 使える / 2 = 使えない (ラッパー経路のまま)

    public static float UnscaledTime => Enabled && ClockReady() ? StaticFloat(_unscaledTime) : Time.unscaledTime;
    public static float RealtimeSinceStartup => Enabled && ClockReady() ? StaticFloat(_realtime) : Time.realtimeSinceStartup;
    /// <summary>Time.time と同じ。</summary>
    public static float GameTime => Enabled && ClockReady() ? StaticFloat(_time) : Time.time;
    /// <summary>Time.deltaTime と同じ (FixedUpdate の中では fixedDeltaTime を返すのも同じ)。</summary>
    public static float DeltaTime => Enabled && ClockReady() ? StaticFloat(_deltaTime) : Time.deltaTime;
    public static float UnscaledDeltaTime => Enabled && ClockReady() ? StaticFloat(_unscaledDeltaTime) : Time.unscaledDeltaTime;
    public static float FixedDeltaTime => Enabled && ClockReady() ? StaticFloat(_fixedDeltaTime) : Time.fixedDeltaTime;
    public static int FrameCount => Enabled && ClockReady() ? ((delegate* unmanaged[Cdecl]<IntPtr, int>)_frameCount.Ptr)(_frameCount.Info) : Time.frameCount;
    public static Vector3 MousePosition => Enabled && ClockReady() ? ((delegate* unmanaged[Cdecl]<IntPtr, Vector3>)_mousePosition.Ptr)(_mousePosition.Info) : Input.mousePosition;
    public static Vector2 MouseScrollDelta => Enabled && ClockReady() ? ((delegate* unmanaged[Cdecl]<IntPtr, Vector2>)_mouseScroll.Ptr)(_mouseScroll.Info) : Input.mouseScrollDelta;
    public static bool GetMouseButton(int button) => Enabled && ClockReady() ? StaticIntBool(_mouseButton, button) : Input.GetMouseButton(button);
    public static bool GetMouseButtonDown(int button) => Enabled && ClockReady() ? StaticIntBool(_mouseButtonDown, button) : Input.GetMouseButtonDown(button);
    public static bool GetMouseButtonUp(int button) => Enabled && ClockReady() ? StaticIntBool(_mouseButtonUp, button) : Input.GetMouseButtonUp(button);

    private static float StaticFloat(in Method m) => ((delegate* unmanaged[Cdecl]<IntPtr, float>)m.Ptr)(m.Info);
    private static bool StaticIntBool(in Method m, int arg) => ((delegate* unmanaged[Cdecl]<int, IntPtr, byte>)m.Ptr)(arg, m.Info) != 0;

    private static bool ClockReady()
    {
        if (_clockState != 0) return _clockState == 1;

        _clockState = 2;
        _unscaledTime = Resolve<Time>("get_unscaledTime", 0);
        _realtime = Resolve<Time>("get_realtimeSinceStartup", 0);
        _time = Resolve<Time>("get_time", 0);
        _deltaTime = Resolve<Time>("get_deltaTime", 0);
        _unscaledDeltaTime = Resolve<Time>("get_unscaledDeltaTime", 0);
        _fixedDeltaTime = Resolve<Time>("get_fixedDeltaTime", 0);
        _frameCount = Resolve<Time>("get_frameCount", 0);
        _mousePosition = Resolve<Input>("get_mousePosition", 0);
        _mouseScroll = Resolve<Input>("get_mouseScrollDelta", 0);
        _mouseButton = Resolve<Input>("GetMouseButton", 1);
        _mouseButtonDown = Resolve<Input>("GetMouseButtonDown", 1);
        _mouseButtonUp = Resolve<Input>("GetMouseButtonUp", 1);

        if (!_unscaledTime.Ok || !_realtime.Ok || !_time.Ok || !_deltaTime.Ok || !_unscaledDeltaTime.Ok || !_fixedDeltaTime.Ok || !_frameCount.Ok) return Fail("resolve time");
        if (!_mousePosition.Ok || !_mouseScroll.Ok || !_mouseButton.Ok || !_mouseButtonDown.Ok || !_mouseButtonUp.Ok) return Fail("resolve input");

        // ラッパー経路を先に通し (エンジン側の例外はここで捕まえられる)、同じフレーム内の値と突き合わせる
        // realtimeSinceStartup だけは読むたびに進むので幅を持たせる
        try
        {
            float unscaled = Time.unscaledTime, time = Time.time, dt = Time.deltaTime, udt = Time.unscaledDeltaTime, fdt = Time.fixedDeltaTime;
            int frame = Time.frameCount;
            float rt = Time.realtimeSinceStartup;
            Vector3 mouse = Input.mousePosition;
            Vector2 scroll = Input.mouseScrollDelta;
            Span<bool> held = stackalloc bool[3], down = stackalloc bool[3], up = stackalloc bool[3];

            for (int b = 0; b < 3; b++)
            {
                held[b] = Input.GetMouseButton(b);
                down[b] = Input.GetMouseButtonDown(b);
                up[b] = Input.GetMouseButtonUp(b);
            }

            if (StaticFloat(_unscaledTime) != unscaled) return Fail("unscaledTime");
            if (StaticFloat(_time) != time) return Fail("time");
            if (StaticFloat(_deltaTime) != dt) return Fail("deltaTime");
            if (StaticFloat(_unscaledDeltaTime) != udt) return Fail("unscaledDeltaTime");
            if (StaticFloat(_fixedDeltaTime) != fdt) return Fail("fixedDeltaTime");
            if (((delegate* unmanaged[Cdecl]<IntPtr, int>)_frameCount.Ptr)(_frameCount.Info) != frame) return Fail("frameCount");

            float rtDirect = StaticFloat(_realtime);
            if (rtDirect < rt || rtDirect - rt > 0.5f) return Fail("realtimeSinceStartup");

            Vector3 m = ((delegate* unmanaged[Cdecl]<IntPtr, Vector3>)_mousePosition.Ptr)(_mousePosition.Info);
            if (m.x != mouse.x || m.y != mouse.y || m.z != mouse.z) return Fail("mousePosition");
            Vector2 sc = ((delegate* unmanaged[Cdecl]<IntPtr, Vector2>)_mouseScroll.Ptr)(_mouseScroll.Info);
            if (sc.x != scroll.x || sc.y != scroll.y) return Fail("mouseScrollDelta");

            for (int b = 0; b < 3; b++)
            {
                if (StaticIntBool(_mouseButton, b) != held[b]) return Fail("GetMouseButton");
                if (StaticIntBool(_mouseButtonDown, b) != down[b]) return Fail("GetMouseButtonDown");
                if (StaticIntBool(_mouseButtonUp, b) != up[b]) return Fail("GetMouseButtonUp");
            }
        }
        catch (Exception e) { return Fail(e.GetType().Name); }

        _clockState = 1;
        return true;

        static bool Fail(string what)
        {
            Logger.Warn($"direct clock/input calls disabled ({what})", Tag);
            return false;
        }
    }

    // ---- DestroyableSingleton<T>.InstanceExists ----
    // InstanceExists は静的フィールドを読んで生存確認するだけの静的メソッドで、ラッパー経由だと 1 回ごとに箱が出る。
    // 型ごとに別の MethodInfo (共有コードに型の情報を渡す) なので、T ごとに解決して握る。
    // Instance の方は無い時に空の物を作るので、ここでは扱わない。

    /// <summary>DestroyableSingleton&lt;T&gt;.InstanceExists と同じ。</summary>
    public static bool Exists<T>() where T : MonoBehaviour
    {
        if (!Enabled) return DestroyableSingleton<T>.InstanceExists;

        ref SingletonSlot slot = ref SingletonSlot<T>.Slot;
        if (slot.State == 0) slot.Resolve<T>();
        if (slot.State != 1) return DestroyableSingleton<T>.InstanceExists;

        bool v = ((delegate* unmanaged[Cdecl]<IntPtr, byte>)slot.M.Ptr)(slot.M.Info) != 0;

        if (slot.Checks < InstanceCheckCount)
        {
            slot.Checks++;
            bool wrapped = DestroyableSingleton<T>.InstanceExists;

            if (v != wrapped)
            {
                slot.State = 2;
                Logger.Warn($"direct InstanceExists<{typeof(T).Name}> disabled (disagrees with the wrapper)", Tag);
                return wrapped;
            }
        }

        return v;
    }

    private struct SingletonSlot
    {
        public Method M;
        public int State; // 0 = 未解決 / 1 = 使える / 2 = 使えない
        public int Checks;

        public void Resolve<T>() where T : MonoBehaviour
        {
            M = Il2Direct.Resolve<DestroyableSingleton<T>>("get_InstanceExists", 0);
            State = M.Ok ? 1 : 2;
        }
    }

    private static class SingletonSlot<T> where T : MonoBehaviour
    {
        public static SingletonSlot Slot;
    }

    // ---- 生きている物に対する、非仮想のインスタンスの読み取り ----
    // 呼ぶ直前にその物自体が生きているかを確かめ、破棄済み (や null) ならラッパー経路へ落とす (例外の出方を従来どおりに保つ)。
    // 仮想メソッドは解決の段階で弾く。最初の数十回はラッパー経路と突き合わせ、食い違えばそのメソッドだけ以後ラッパー経路。

    private const int InstanceCheckCount = 32;

    private sealed class Reader
    {
        public readonly string Name;
        private readonly Func<Method> _resolve;
        public Method M;
        public int State; // 0 = 未解決 / 1 = 使える / 2 = 使えない
        public int Checks;

        public Reader(string name, Func<Method> resolve)
        {
            Name = name;
            _resolve = resolve;
        }

        public bool Ready()
        {
            if (State != 0) return State == 1;

            M = _resolve();
            State = M.Ok && !IsVirtual(M.Info) ? 1 : 2;
            if (State == 2) Logger.Warn($"direct {Name} unavailable", Tag);
            return State == 1;
        }

        // 最初の数回だけ呼ばれる。食い違えば以後ずっとラッパー経路
        public bool Agrees(bool same)
        {
            Checks++;
            if (same) return true;

            State = 2;
            Logger.Warn($"direct {Name} disabled (disagrees with the wrapper)", Tag);
            return false;
        }
    }

    private static bool IsVirtual(IntPtr info)
    {
        uint iflags = 0;
        return (IL2CPP.il2cpp_method_get_flags(info, ref iflags) & 0x0040) != 0; // METHOD_ATTRIBUTE_VIRTUAL
    }

    private static readonly Reader RPosition = new("Transform.position", static () => Resolve<Transform>("get_position", 0));
    private static readonly Reader ROrtho = new("Camera.orthographicSize", static () => Resolve<Camera>("get_orthographicSize", 0));
    private static readonly Reader RActiveSelf = new("GameObject.activeSelf", static () => Resolve<GameObject>("get_activeSelf", 0));
    private static readonly Reader RActiveAndEnabled = new("Behaviour.isActiveAndEnabled", static () => Resolve<Behaviour>("get_isActiveAndEnabled", 0));
    private static readonly Reader RColliderBounds = new("Collider2D.bounds", static () => Resolve<Collider2D>("get_bounds", 0));
    private static readonly Reader RSpriteBounds = new("Sprite.bounds", static () => Resolve<Sprite>("get_bounds", 0));
    private static readonly Reader RAmHost = new("InnerNetClient.AmHost", static () => Resolve<InnerNet.InnerNetClient>("get_AmHost", 0));
    private static readonly Reader RIsGameStarted = new("InnerNetClient.IsGameStarted", static () => Resolve<InnerNet.InnerNetClient>("get_IsGameStarted", 0));
    private static readonly Reader RIsGamePublic = new("InnerNetClient.IsGamePublic", static () => Resolve<InnerNet.InnerNetClient>("get_IsGamePublic", 0));
    private static readonly Reader RAmOwner = new("InnerNetObject.AmOwner", static () => Resolve<InnerNet.InnerNetObject>("get_AmOwner", 0));
    private static readonly Reader REventType = new("Event.type", static () => Resolve<Event>("get_type", 0));

    private static bool Usable(UnityEngine.Object o, Reader r) => Enabled && Alive(o) && r.Ready();

    public static Vector3 Position(Transform t)
    {
        if (!Usable(t, RPosition)) return t.position;
        Vector3 v = V3(RPosition.M, t.Pointer);
        return RPosition.Checks >= InstanceCheckCount || RPosition.Agrees(v == t.position) ? v : t.position;
    }

    public static float OrthographicSize(Camera c)
    {
        if (!Usable(c, ROrtho)) return c.orthographicSize;
        float v = Float(ROrtho.M, c.Pointer);
        return ROrtho.Checks >= InstanceCheckCount || ROrtho.Agrees(v == c.orthographicSize) ? v : c.orthographicSize;
    }

    public static bool ActiveSelf(GameObject go) => ReadBool(go, RActiveSelf, static o => o.activeSelf);
    public static bool IsActiveAndEnabled(Behaviour b) => ReadBool(b, RActiveAndEnabled, static o => o.isActiveAndEnabled);
    public static bool AmHost(InnerNet.InnerNetClient c) => ReadBool(c, RAmHost, static o => o.AmHost);
    public static bool IsGameStarted(InnerNet.InnerNetClient c) => ReadBool(c, RIsGameStarted, static o => o.IsGameStarted);
    public static bool IsGamePublic(InnerNet.InnerNetClient c) => ReadBool(c, RIsGamePublic, static o => o.IsGamePublic);
    // AmOwner は中で AmongUsClient.Instance を読むので、それが無い瞬間 (切断の途中など) はラッパー経路で例外を受ける
    public static bool AmOwner(InnerNet.InnerNetObject o) => Alive(AmongUsClient.Instance) ? ReadBool(o, RAmOwner, static x => x.AmOwner) : o.AmOwner;

    public static Bounds ColliderBounds(Collider2D c) => ReadBounds(c, RColliderBounds, static o => o.bounds);
    public static Bounds SpriteBounds(Sprite s) => ReadBounds(s, RSpriteBounds, static o => o.bounds);

    private static bool ReadBool<T>(T o, Reader r, Func<T, bool> wrapped) where T : UnityEngine.Object
    {
        if (!Usable(o, r)) return wrapped(o);
        bool v = Bool(r.M, o.Pointer);
        return r.Checks >= InstanceCheckCount || r.Agrees(v == wrapped(o)) ? v : wrapped(o);
    }

    private static Bounds ReadBounds<T>(T o, Reader r, Func<T, Bounds> wrapped) where T : UnityEngine.Object
    {
        if (!Usable(o, r)) return wrapped(o);
        Bounds v = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, Bounds>)r.M.Ptr)(o.Pointer, r.M.Info);
        if (r.Checks >= InstanceCheckCount) return v;
        Bounds w = wrapped(o);
        return r.Agrees(v.m_Center == w.m_Center && v.m_Extents == w.m_Extents) ? v : w;
    }

    /// <summary>Event.type と同じ。Event は UnityEngine.Object ではないので、ネイティブ側の実体 (m_Ptr) があるかで判定する。</summary>
    public static EventType EventTypeOf(Event e)
    {
        if (!Enabled || e is null || e.m_Ptr == IntPtr.Zero || !REventType.Ready()) return e.type;
        var v = (EventType)Int(REventType.M, e.Pointer);
        return REventType.Checks >= InstanceCheckCount || REventType.Agrees(v == e.type) ? v : e.type;
    }

    // ---- 構造体のメンバ (interop のラッパーは this を箱に詰めてゲーム側へ渡すので、フィールドから自分で計算する) ----

    public static float Width(in Rect r) => r.m_Width;
    public static float Height(in Rect r) => r.m_Height;
    public static float XMin(in Rect r) => r.m_XMin;
    public static float YMin(in Rect r) => r.m_YMin;
    public static float XMax(in Rect r) => r.m_XMin + r.m_Width;
    public static float YMax(in Rect r) => r.m_YMin + r.m_Height;

    /// <summary>Rect.Contains(Vector2) と同じ (幅・高さが負の Rect は何も含まない点も同じ)。</summary>
    public static bool Contains(in Rect r, Vector2 p) => p.x >= r.m_XMin && p.x < r.m_XMin + r.m_Width && p.y >= r.m_YMin && p.y < r.m_YMin + r.m_Height;

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
