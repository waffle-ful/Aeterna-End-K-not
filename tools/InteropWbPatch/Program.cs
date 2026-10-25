// Il2CppInterop.Runtime 1.5.3 の barrier 無しの参照書き込み 3 か所を il2cpp_gc_wbarrier_set_field 経由に書き換える。
// packaging/il2cppinterop/Il2CppInterop.Runtime.dll はこのツールの出力に tools/InteropPtrCachePatch を掛けた物 (LGPL-3.0 の改変版)。
//   1) IL2CPP.FieldWriteWbarrierStub(obj, target, value) の本体 = il2cpp_gc_wbarrier_set_field(obj, target, value)
//      (ScanMethodRefs=false で生成した interop の参照型フィールド setter はここを呼ぶ)
//   2) Il2CppReferenceArray<T>.StoreValue の参照型分岐の stind.i
//   3) Il2CppStringArray の indexer setter の stind.i
// 2) 3) は (target, value) がスタックに積まれているので、新設の IL2CPP.WbStore(target, value) の呼び出しへ置換する。
// ゲーム本体の il2cpp_gc_wbarrier_set_field は第 1 引数を使わない (*target = value; GC_dirty(target)) ので obj = 0 でよい
// (GameAssembly 2026.9.29 の逆アセンブルで確認)。
// incremental GC が ON の間、barrier の無い書き込みは書いた先の参照が回収されうる (生きているのに解放される)。
// 3 か所を直すと interop を作り直さずに incremental GC を ON にできる。mod 側は IL2CPP.WbStore の有無で
// 書き換え済みかどうかを判定する (Modules/IncrementalGcInvalidator.cs)。
//
// 使い方: dotnet run --project tools/InteropWbPatch -- <元の Il2CppInterop.Runtime.dll> <出力先>
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) { Console.Error.WriteLine("usage: InteropWbPatch <in Il2CppInterop.Runtime.dll> <out>"); return 2; }
var rp = new ReaderParameters { ReadWrite = false, InMemory = true };
using var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
var mod = asm.MainModule;
var il2cpp = mod.GetType("Il2CppInterop.Runtime.IL2CPP");
var setField = il2cpp.Methods.Single(m => m.Name == "il2cpp_gc_wbarrier_set_field");
var stub = il2cpp.Methods.Single(m => m.Name == "FieldWriteWbarrierStub");
if (il2cpp.Methods.Any(m => m.Name == "WbStore")) { Console.Error.WriteLine("already patched"); return 3; }

// 1)
stub.Body.Instructions.Clear();
stub.Body.Variables.Clear();
stub.Body.ExceptionHandlers.Clear();
var p = stub.Body.GetILProcessor();
p.Emit(OpCodes.Ldarg_0); p.Emit(OpCodes.Ldarg_1); p.Emit(OpCodes.Ldarg_2);
p.Emit(OpCodes.Call, setField); p.Emit(OpCodes.Ret);

// WbStore(target, value)
var intPtr = mod.TypeSystem.IntPtr;
var wb = new MethodDefinition("WbStore", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, mod.TypeSystem.Void);
wb.Parameters.Add(new ParameterDefinition("targetAddress", ParameterAttributes.None, intPtr));
wb.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, intPtr));
var w = wb.Body.GetILProcessor();
w.Emit(OpCodes.Ldc_I4_0); w.Emit(OpCodes.Conv_I); w.Emit(OpCodes.Ldarg_0); w.Emit(OpCodes.Ldarg_1);
w.Emit(OpCodes.Call, setField); w.Emit(OpCodes.Ret);
il2cpp.Methods.Add(wb);

int Replace(MethodDefinition m)
{
    var stores = m.Body.Instructions.Where(i => i.OpCode == OpCodes.Stind_I).ToList();
    foreach (var s in stores) { s.OpCode = OpCodes.Call; s.Operand = wb; }
    return stores.Count;
}
// 2)
var refArr = mod.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1");
int n2 = Replace(refArr.Methods.Single(m => m.Name == "StoreValue"));
// 3)
var strArr = mod.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray");
int n3 = Replace(strArr.Methods.Single(m => m.Name == "set_Item"));

// 残りの stind.i / stind.ref を一覧に出す (型登録・メタデータ構造体への書き込みは GC 管理外なので対象外)
foreach (var t in mod.GetTypes())
    foreach (var m in t.Methods.Where(m => m.HasBody))
        foreach (var i in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Stind_I || i.OpCode == OpCodes.Stind_Ref))
            Console.WriteLine($"remaining {i.OpCode} in {t.FullName}::{m.Name}");

asm.Write(args[1]);
Console.WriteLine($"patched: stub=1 refArray={n2} stringArray={n3} -> {args[1]}");
return (n2 == 1 && n3 == 1) ? 0 : 4;
