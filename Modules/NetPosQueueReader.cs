using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace EndKnot.Modules;

// CustomNetworkTransform.incomingPosQueue (Il2CppSystem Queue<Vector2>) の末尾要素を、
// 配列 wrapper を生成せずに il2cpp オブジェクトから直接読む。
// 参照型フィールド (_array) の interop getter は呼ぶたびに新しい Il2CppStructArray wrapper を
// 作るため、Pos() 1 呼びで ≈695B の managed 確保が発生していた (2026-09-07 実測)。
// フィールドオフセットは最初の実インスタンスから 1 回だけ解決し、
// 最初の数回は従来経路と値を突き合わせて一致しなければ恒久的に無効化する。
public static class NetPosQueueReader
{
    private const int ValidateCount = 16;
    private const string Tag = "NetPosQueueReader";

    private static int _arrayOffset = -1;
    private static int _tailOffset = -1;
    private static bool _disabled;
    private static int _validated;

    public static bool Enabled => !_disabled;

    /// <summary>末尾 (最後に Enqueue された) 要素を確保無しで読む。読めない時は false (呼び出し側が従来経路へ)。</summary>
    public static unsafe bool TryReadLast(Il2CppSystem.Collections.Generic.Queue<Vector2> queue, out Vector2 value)
    {
        value = default;
        if (_disabled || queue == null) return false;

        IntPtr q = queue.Pointer;
        if (q == IntPtr.Zero) return false;

        if (_arrayOffset < 0 && !ResolveOffsets(q)) return false;

        IntPtr arr = *(IntPtr*)((byte*)q + _arrayOffset);
        if (arr == IntPtr.Zero) return false;

        long len = IL2CPP.il2cpp_array_length(arr);
        if (len <= 0) return false;

        int tail = *(int*)((byte*)q + _tailOffset);
        long index = (tail - 1 + len) % len;
        if ((ulong)index >= (ulong)len) return false;

        // il2cpp 配列ヘッダ = klass / monitor / bounds / max_length の 4 ポインタ分 (x64 で 32B)。
        // この前提は CustomSounds / EkmapLoader の Marshal.Copy 経路と同じ。
        value = *(Vector2*)((byte*)arr + IntPtr.Size * 4 + index * sizeof(Vector2));

        if (_validated < ValidateCount && !Validate(queue, index, value)) return false;

        return true;
    }

    private static bool ResolveOffsets(IntPtr queuePtr)
    {
        try
        {
            IntPtr klass = IL2CPP.il2cpp_object_get_class(queuePtr);
            if (klass == IntPtr.Zero) return Disable("class ptr is null");

            IntPtr arrayField = IL2CPP.il2cpp_class_get_field_from_name(klass, "_array");
            IntPtr tailField = IL2CPP.il2cpp_class_get_field_from_name(klass, "_tail");
            if (arrayField == IntPtr.Zero || tailField == IntPtr.Zero) return Disable("field not found");

            uint arrayOff = IL2CPP.il2cpp_field_get_offset(arrayField);
            uint tailOff = IL2CPP.il2cpp_field_get_offset(tailField);
            if (arrayOff == unchecked((uint)-1) || tailOff == unchecked((uint)-1) || arrayOff == 0 || tailOff == 0) return Disable($"bad offsets array={arrayOff} tail={tailOff}");

            _arrayOffset = (int)arrayOff;
            _tailOffset = (int)tailOff;
            return true;
        }
        catch (Exception e)
        {
            return Disable(e.Message);
        }
    }

    private static bool Validate(Il2CppSystem.Collections.Generic.Queue<Vector2> queue, long index, Vector2 fast)
    {
        try
        {
            var array = queue._array;
            int tail = queue._tail;
            int expectedIndex = (tail - 1 + array.Length) % array.Length;
            Vector2 expected = array[expectedIndex];

            // Vector2 の != はイプシロン許容なのでビット厳密で比べる
            if (expectedIndex != index || !expected.x.Equals(fast.x) || !expected.y.Equals(fast.y))
                return Disable($"mismatch fast={fast}@{index} managed={expected}@{expectedIndex}");

            if (++_validated == ValidateCount)
                Logger.Info($"validated {ValidateCount} reads (array@{_arrayOffset} tail@{_tailOffset})", Tag);

            return true;
        }
        catch (Exception e)
        {
            return Disable(e.Message);
        }
    }

    private static bool Disable(string reason)
    {
        _disabled = true;
        Logger.Warn($"fast path disabled: {reason}", Tag);
        return false;
    }
}
