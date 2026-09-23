# libdobby.so

`fusionApp/src/main/jniLibs/arm64-v8a/libdobby.so` is built from
[jmpews/Dobby](https://github.com/jmpews/Dobby) at commit `f4643b8d` with
`code-patch-end-page.patch` applied (Apache-2.0).

The patch makes `DobbyCodePatch` compute the last page of a patch from its last
byte (`address + size - 1`). Without it, a patch that ends exactly on a page
boundary also changes the permissions of the following, unrelated page.
`safehook_probe_code_patch()` in `fusion/src/hooking/safehook.cpp` checks this
at startup and logs `CodePatch boundary probe: ... -> PASS`.

## Build (arm64-v8a)

```
git clone https://github.com/jmpews/Dobby.git && cd Dobby
git checkout f4643b8d
git apply ../code-patch-end-page.patch
cmake -S . -B build-arm64 -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-27 -DCMAKE_BUILD_TYPE=Release \
  -DDOBBY_GENERATE_SHARED=ON -DDOBBY_DEBUG=OFF -DNearBranch=ON -DPlugin.SymbolResolver=ON
cmake --build build-arm64 --target dobby
$NDK/toolchains/llvm/prebuilt/*/bin/llvm-strip --strip-all build-arm64/libdobby.so
```

NDK 28.2.13676358 / CMake 3.22.1 were used for the shipped binary.

## Verify the replacement

The exported symbols and the `NEEDED` libraries must match the previous binary
(the build flags of the original were not recorded, so the file size differs;
only the interface is checked):

```
llvm-nm -D --defined-only libdobby.so | grep -i ' T .*dobby'
# DobbyCodePatch DobbyDestroy DobbyGetVersion DobbyHook DobbyInstrument DobbySymbolResolver
# dobby_disable_near_branch_trampoline dobby_enable_near_branch_trampoline
llvm-readelf -d libdobby.so | grep -E 'NEEDED|SONAME'
# liblog.so libm.so libdl.so libc.so / SONAME libdobby.so
```

On the device, a cold start must log `SafeHook: CodePatch boundary probe: ... -> PASS`.
