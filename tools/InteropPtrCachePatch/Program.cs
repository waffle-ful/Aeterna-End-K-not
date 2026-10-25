// Il2CppInterop.Runtime の Il2CppObjectBase.Pointer を、毎回 il2cpp_gchandle_get_target を呼ばずに済むようにする。
// packaging/il2cppinterop/Il2CppInterop.Runtime.dll はこのツールの出力 (LGPL-3.0 の改変版・InteropWbPatch の後に掛ける)。
//   1) Il2CppObjectBase にフィールド cachedPointer を足す
//   2) CreateGCHandle で myGcHandle を作った直後に、作った元のポインタを cachedPointer に入れる
//   3) get_Pointer は cachedPointer が 0 でなければそれを返し、0 の時だけ元の処理 (ハンドルから引く) を通る
// 成り立つ理由: myGcHandle は CreateGCHandle で 1 回だけ作る通常 (strong) ハンドルで、解放はファイナライザだけ。
// il2cpp の GC はオブジェクトを動かさないので、ハンドルが生きている間の get_target は常に作った時のポインタを返す。
// ファイナライザが走る時はラッパーに誰も触れないので、キャッシュが古くなる時点は無い。
//
// 使い方: dotnet run --project tools/InteropPtrCachePatch -- <InteropWbPatch 済みの Il2CppInterop.Runtime.dll> <出力先>
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) { Console.Error.WriteLine("usage: InteropPtrCachePatch <in Il2CppInterop.Runtime.dll> <out>"); return 2; }
var rp = new ReaderParameters { ReadWrite = false, InMemory = true };
using var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
var mod = asm.MainModule;
var objBase = mod.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase");
if (objBase.Fields.Any(f => f.Name == "cachedPointer")) { Console.Error.WriteLine("already patched"); return 3; }
if (!mod.GetType("Il2CppInterop.Runtime.IL2CPP").Methods.Any(m => m.Name == "WbStore")) { Console.Error.WriteLine("input is not InteropWbPatch output"); return 5; }

var handle = objBase.Fields.Single(f => f.Name == "myGcHandle");
var cached = new FieldDefinition("cachedPointer", FieldAttributes.Private, mod.TypeSystem.IntPtr);
objBase.Fields.Add(cached);

// 2)
var create = objBase.Methods.Single(m => m.Name == "CreateGCHandle");
var stores = create.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == handle.Name).ToList();
if (stores.Count != 1) { Console.Error.WriteLine($"CreateGCHandle: expected 1 store to myGcHandle, found {stores.Count}"); return 4; }
var cp = create.Body.GetILProcessor();
var after = stores[0];
var i1 = cp.Create(OpCodes.Ldarg_0); var i2 = cp.Create(OpCodes.Ldarg_1); var i3 = cp.Create(OpCodes.Stfld, cached);
cp.InsertAfter(after, i1); cp.InsertAfter(i1, i2); cp.InsertAfter(i2, i3);

// 3)
var getter = objBase.Properties.Single(p => p.Name == "Pointer").GetMethod;
var gp = getter.Body.GetILProcessor();
var first = getter.Body.Instructions[0];
var pop = gp.Create(OpCodes.Pop);
gp.InsertBefore(first, gp.Create(OpCodes.Ldarg_0));
gp.InsertBefore(first, gp.Create(OpCodes.Ldfld, cached));
gp.InsertBefore(first, gp.Create(OpCodes.Dup));
gp.InsertBefore(first, gp.Create(OpCodes.Brfalse, pop));
gp.InsertBefore(first, gp.Create(OpCodes.Ret));
gp.InsertBefore(first, pop);
getter.Body.MaxStackSize = Math.Max(getter.Body.MaxStackSize, 2);

asm.Write(args[1]);
Console.WriteLine($"patched: cachedPointer field + CreateGCHandle store + get_Pointer fast path -> {args[1]}");
return 0;
