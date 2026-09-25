# Third-Party Notices / 同梱している第三者ソフトウェア

End K not itself is licensed under the GNU General Public License v3.0 (see [`LICENSE`](./LICENSE)).
The components listed below are the work of other authors and keep their own licenses.
They are either embedded into `EndKnot.dll` or shipped inside the release packages.

End K not 本体は GPL-3.0 です（[`LICENSE`](./LICENSE)）。以下は別の作者による著作物で、
それぞれ独自のライセンスに従います。`EndKnot.dll` に埋め込まれているもの、
配布パッケージに同梱されているものの両方を含みます。

End K not is a derivative of [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)
(Gurge44 et al., GPL-3.0), and many of its roles are ported from
[TownOfHost-K](https://github.com/KYMario/TownOfHost-K) (KYMario et al., GPL-3.0).
Both are under the same license as End K not; see [`LICENSE`](./LICENSE) and the
[README credits](./README.md#クレジット).

End K not は [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)（Gurge44 他、GPL-3.0）の
派生プロジェクトで、多くの役職は [TownOfHost-K](https://github.com/KYMario/TownOfHost-K)（KYMario 他、GPL-3.0）
から移植しています。どちらも本体と同じライセンスです（[`LICENSE`](./LICENSE) と
[README のクレジット](./README.md#クレジット)を参照）。

| Component | Author / Project | License | Where |
|---|---|---|---|
| NVorbis 0.10.5 | Andrew Ward — [NVorbis](https://github.com/NVorbis/NVorbis) | MIT | embedded in `EndKnot.dll` (Ogg Vorbis decoding) |
| NLayer 1.16.0 | Mark Heath, Andrew Ward & Contributors (port of JavaLayer) — [NLayer](https://github.com/naudio/NLayer) | MIT | embedded in `EndKnot.dll` (MP3 decoding) |
| Yusei Magic (font) | The Yusei Magic Project Authors — [YuseiMagic](https://github.com/tanukifont/YuseiMagic) | SIL OFL 1.1 | embedded in `EndKnot.dll` (`Resources/Fonts/YuseiMagic-Regular.ttf`, main menu text) |
| Unity Doorstop 4.5.1 (`winhttp.dll`, `doorstop_config.ini`) | NeighTools — [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) | LGPL-2.1 | release packages |
| BepInEx (IL2CPP) | BepInEx team — [BepInEx](https://github.com/BepInEx/BepInEx) | LGPL-2.1 | release packages |
| Il2CppInterop | BepInEx team, knah et al. — [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | LGPL-3.0 | release packages (`BepInEx/core`; modified build — see [LGPL components](#lgpl-components-bepinex-unity-doorstop-miraapi-il2cppinterop)) |
| HarmonyX (`0Harmony.dll`) | BepInEx, based on Harmony by Andreas Pardeike — [HarmonyX](https://github.com/BepInEx/HarmonyX) | MIT | release packages (`BepInEx/core`) |
| MonoMod (`MonoMod.RuntimeDetour`, `MonoMod.Utils`) | 0x0ade — [MonoMod](https://github.com/MonoMod/MonoMod) | MIT | release packages (`BepInEx/core`) |
| Mono.Cecil (`Mono.Cecil*.dll`) | Jb Evain, Novell — [Cecil](https://github.com/jbevain/cecil) | MIT | release packages (`BepInEx/core`) |
| AsmResolver (`AsmResolver*.dll`) | Washi — [AsmResolver](https://github.com/Washi1337/AsmResolver) | MIT | release packages (`BepInEx/core`) |
| Cpp2IL (`Cpp2IL.Core`, `LibCpp2IL`, `StableNameDotNet`, `WasmDisassembler`) | Sam Byass (Samboy063) — [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) | MIT | release packages (`BepInEx/core`) |
| Disarm | Sam Byass (Samboy063) — [Disarm](https://github.com/SamboyCoding/Disarm) | MIT | release packages (`BepInEx/core`) |
| AssetRipper.CIL / AssetRipper.Primitives | ds5678 — [AssetRipper.CIL](https://github.com/AssetRipper/AssetRipper.CIL), [AssetRipper.Primitives](https://github.com/AssetRipper/AssetRipper.Primitives) | MIT | release packages (`BepInEx/core`) |
| Iced | iced project and contributors — [iced](https://github.com/icedland/iced) | MIT | release packages (`BepInEx/core`) |
| Capstone.NET (`Gee.External.Capstone.dll`) | Ahmed Garhy — [Capstone.NET](https://github.com/9ee1/Capstone.NET) | MIT | release packages (`BepInEx/core`) |
| SemanticVersioning | Adam Reeve — [semver.net](https://github.com/adamreeve/semver.net) | MIT | release packages (`BepInEx/core`) |
| Dobby (`dobby.dll`) | jmpews — [Dobby](https://github.com/jmpews/Dobby) | Apache-2.0 | release packages (`BepInEx/core`) |
| .NET runtime | Microsoft & .NET Foundation — [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | release packages (`dotnet/`) |
| Python (embeddable) and its packages | Python Software Foundation — [CPython](https://github.com/python/cpython); packages listed in [Python](#python-ai-commentary-companion) | PSF-2.0 and others (see below) | release packages (`EndKnot_DATA/companion/python/`) |
| three.js / three-vrm / fflate | three.js authors — [three.js](https://github.com/mrdoob/three.js); pixiv Inc. — [three-vrm](https://github.com/pixiv/three-vrm); Arjun Barrett — [fflate](https://github.com/101arrowz/fflate) | MIT | embedded in `EndKnot.dll` (companion avatar page) |
| Mini.RegionInstall | miniduikboot — [Mini.RegionInstall](https://github.com/miniduikboot/Mini.RegionInstall) | GPL-3.0 | source, adapted in `Modules/RegionInstaller.cs` |
| CrowdedMod | andry08 & CrowdedMods — [CrowdedMod](https://github.com/CrowdedMods/CrowdedMod) | MIT | source, adapted in `Patches/Crowded.cs` |
| MiraAPI (UI sprites) | All-Of-Us-Mods — [MiraAPI](https://github.com/All-Of-Us-Mods/MiraAPI) | LGPL-2.1 | `Resources/Images/`: `ActiveNextButton.png`, `InactiveNextButton.png`, `Checkmark.png`, `CheckMarkBox.png` |
| TownOfHost-Pko | satokazoku et al. — [TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko) | GPL-3.0 | source, adapted in `Patches/NumericOptionInputPatch.cs` and `Modules/ConsecutiveJoinKick.cs` |
| Lotus (LotusContinued) | Lotus-AU — [LotusContinued](https://github.com/Lotus-AU/LotusContinued) | GPL-3.0 | source, adapted in `Modules/WhitelistManager.cs` |
| FusionCore | All-Of-Us-Mods — [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) | GPL-3.0 | source, `Android/Launcher/` (git subtree, rebranded Android launcher) |
| BepInEx Fusion | All-Of-Us-Mods — [BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion) | LGPL-2.1 | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`) |
| Il2CppInterop Fusion | All-Of-Us-Mods — [Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) | LGPL-3.0 | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`) |
| .NET runtime | Microsoft & .NET Foundation — [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/dotnet-arm64.zip`, `jniLibs/arm64-v8a/`) |
| Dobby (modified) | jmpews — [Dobby](https://github.com/jmpews/Dobby) | Apache-2.0 | bundled in the launcher APK (`jniLibs/arm64-v8a/libdobby.so`); changes in [`Android/Launcher/third_party/dobby/`](./Android/Launcher/third_party/dobby/) |
| OpenSSL | The OpenSSL Project Authors — [OpenSSL](https://github.com/openssl/openssl) | Apache-2.0 | bundled in the launcher APK (`jniLibs/arm64-v8a/libcrypto.so`, `libssl.so`) |
| AndroidX (annotation, appcompat, browser, coordinatorlayout, core) / Material Components | The Android Open Source Project / Google — [AndroidX](https://github.com/androidx/androidx), [material-components-android](https://github.com/material-components/material-components-android) | Apache-2.0 | bundled in the launcher APK |
| Kotlin standard library / kotlinx.serialization | JetBrains — [Kotlin](https://github.com/JetBrains/kotlin), [kotlinx.serialization](https://github.com/Kotlin/kotlinx.serialization) | Apache-2.0 | bundled in the launcher APK |
| Protocol Buffers (protobuf-javalite) | Google — [protobuf](https://github.com/protocolbuffers/protobuf) | BSD-3-Clause | bundled in the launcher APK |
| AndroidUtilities / AuthFix | All-Of-Us-Mods — [AndroidUtilities](https://github.com/All-Of-Us-Mods/AndroidUtilities) | (no license file published) | technique reference only; no code copied |

The GPL-3.0 entries above are ported source rather than bundled binaries. End K not is itself
GPL-3.0, so those files stay under the same license; the full text is in [`LICENSE`](./LICENSE).
Features that were rewritten from scratch after reading another mod, rather than adapted from its
source, are credited in the README credit list instead of this table.

上表の GPL-3.0 の項目は、同梱バイナリではなくソースコードの移植です。本体も GPL-3.0 なので
ライセンスはそのまま引き継がれます（全文は [`LICENSE`](./LICENSE)）。他 Mod を参考にしつつ
書き起こした機能は、この表ではなく README のクレジット一覧に記載しています。

Bundled audio, sound-effect and video assets are credited in [`README.md`](./README.md#クレジット) /
[`README-EN.md`](./README-EN.md#credits).

---

## NVorbis

```
MIT License

Copyright (c) 2020 Andrew Ward

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Yusei Magic

```
Copyright 2020 The Yusei Magic Project Authors (https://github.com/tanukifont/YuseiMagic)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## NLayer

```
MIT License

Copyright (c) 2018 Mark Heath, Andrew Ward & Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## CrowdedMod

```
MIT License

Copyright (c) 2020-2022 andry08 (github.com/andry08) & CrowdedMods (github.com/CrowdedMods)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## MIT components (BepInEx/core, .NET runtime, three.js)

The following are distributed under the MIT License below, with these copyright notices:

```
HarmonyX            Copyright (c) 2020 BepInEx
                    Copyright (c) 2017 Andreas Pardeike (Harmony)
MonoMod             Copyright (c) 2015 - 2020 0x0ade
Mono.Cecil          Copyright (c) 2008 - 2015 Jb Evain
                    Copyright (c) 2008 - 2011 Novell, Inc.
AsmResolver         Copyright (c) 2016-2026 Washi
Cpp2IL / LibCpp2IL / StableNameDotNet / WasmDisassembler
                    Copyright (c) 2020 Sam Byass
Disarm              Copyright (c) 2025 Sam Byass
AssetRipper.CIL     Copyright (c) 2024 ds5678
AssetRipper.Primitives
                    Copyright (c) 2022 ds5678
Iced                Copyright (C) 2018-present iced project and contributors
Capstone.NET        Copyright (c) Ahmed Garhy
SemanticVersioning  Copyright (c) Adam Reeve
.NET runtime        Copyright (c) .NET Foundation and Contributors
three.js            Copyright (c) 2010-2026 three.js authors
three-vrm           Copyright (c) 2019-2026 pixiv Inc.
fflate              Copyright (c) 2026 Arjun Barrett
```

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The three.js license file is also shipped next to the avatar page files as
`tools/companion/avatar/vendor/LICENSE-three.txt`.

## LGPL components (BepInEx, Unity Doorstop, MiraAPI, Il2CppInterop)

BepInEx, Unity Doorstop and the MiraAPI sprites are licensed under LGPL-2.1 and are redistributed
unmodified (except `doorstop_config.ini`, which is a plain configuration file).
Il2CppInterop is licensed under LGPL-3.0. The release packages ship a patched build of
Il2CppInterop 1.5.3 (`Il2CppInterop.Runtime.dll` / `Il2CppInterop.Common.dll`) taken from the packaging of
[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles/commit/c2e0b297e).
The full license texts are included in the release packages as `licenses/LGPL-2.1.txt` and
`licenses/LGPL-3.0.txt` (and in this repository under [`licenses/`](./licenses/)).

BepInEx・Unity Doorstop・MiraAPI のスプライトは LGPL-2.1 で、無改変で再配布しています
（`doorstop_config.ini` は設定ファイルのため当 fork 向けに調整）。Il2CppInterop は LGPL-3.0 で、
配布物には Endless Host Roles の配布構成に含まれる修正版ビルドを同梱しています。ライセンス全文は配布物の
`licenses/` フォルダ（本リポジトリでは [`licenses/`](./licenses/)）にあります。

## Apache-2.0 components (Dobby, OpenSSL, Android launcher libraries)

`dobby.dll` (release packages), and `libdobby.so`, `libcrypto.so`, `libssl.so`, AndroidX,
Material Components, the Kotlin standard library and kotlinx.serialization (Android launcher APK)
are licensed under the Apache License 2.0. The full text is in [`licenses/Apache-2.0.txt`](./licenses/Apache-2.0.txt).

- Dobby — Copyright jmpews. `libdobby.so` is built from commit `f4643b8d` with
  [`code-patch-end-page.patch`](./Android/Launcher/third_party/dobby/code-patch-end-page.patch) applied;
  build steps are in [`Android/Launcher/third_party/dobby/README.md`](./Android/Launcher/third_party/dobby/README.md).
  `dobby.dll` is redistributed unmodified.
- OpenSSL — Copyright (c) 1998-2025 The OpenSSL Project Authors.
- AndroidX / Material Components — Copyright The Android Open Source Project / Google LLC.
- Kotlin / kotlinx.serialization — Copyright JetBrains s.r.o. and Kotlin Programming Language contributors.

Dobby・OpenSSL・Android ランチャーのライブラリは Apache License 2.0 です。`libdobby.so` は上記パッチを
当てたビルドで、変更内容とビルド手順は `Android/Launcher/third_party/dobby/` にあります。

## Protocol Buffers (BSD-3-Clause)

```
Copyright 2008 Google Inc.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
    * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Python (AI commentary companion)

The release packages include the Windows embeddable distribution of Python 3.12
(Python Software Foundation License; the full text is `EndKnot_DATA/companion/python/LICENSE.txt`,
which also covers the libraries bundled with CPython such as OpenSSL, libffi and SQLite)
and the following packages, redistributed unmodified. Each package's license file is shipped in its
`*.dist-info` folder under `EndKnot_DATA/companion/python/Lib/site-packages/`.

リリースパッケージには AI 実況相棒用に Python 3.12（組み込み版）と以下のパッケージを無改変で同梱しています。
各パッケージのライセンス全文は `Lib/site-packages/` 内の `*.dist-info` に入っています。

| Package | License |
|---|---|
| annotated-types, pydantic, pydantic-core, typing-inspection | MIT |
| anyio, charset-normalizer, h11, sounddevice, urllib3 | MIT |
| cffi | MIT-0 |
| certifi | MPL-2.0 |
| cryptography | Apache-2.0 or BSD-3-Clause |
| distro, google-auth, google-genai, requests, tenacity | Apache-2.0 |
| sniffio | MIT or Apache-2.0 |
| httpcore, httpx, idna, pycparser, pyasn1, pyasn1-modules, websockets | BSD-2-Clause / BSD-3-Clause |
| typing-extensions | PSF-2.0 |
| pip | MIT (its vendored packages keep their own licenses in `pip/_vendor/`) |
| PortAudio (bundled with sounddevice) | MIT-style, Copyright (c) 1999-2006 Ross Bencina and Phil Burk. ASIO is a trademark and software of Steinberg Media Technologies GmbH (`*-asio.dll` builds) |

## FusionCore (Android launcher)

The Android launcher in `Android/Launcher/` is a `git subtree` of
[FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) by All-Of-Us-Mods (GPL-3.0), with
branding, storage root and target selection changed for End K not. It stays under GPL-3.0, the
same license as End K not; the full text is in [`Android/Launcher/LICENSE`](./Android/Launcher/LICENSE)
and the tracked upstream commit is recorded in [`Android/Launcher/UPSTREAM.md`](./Android/Launcher/UPSTREAM.md).

`Android/Launcher/` の Android ランチャーは All-Of-Us-Mods の
[FusionCore](https://github.com/All-Of-Us-Mods/FusionCore)（GPL-3.0）を git subtree として取り込み、
ブランド表記・保存先フォルダ・起動対象を End K not 向けに変更したものです。ライセンスは本体と同じ GPL-3.0 です。

## BepInEx Fusion (LGPL-2.1)

[BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion) (All-Of-Us-Mods) is bundled
unmodified inside the Android launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`)
and extracted on the device at first start. The full text of the GNU Lesser General Public License v2.1
is available at <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>, and the project's own
repository carries its license file.

Android ランチャーの APK に無改変で同梱し、初回起動時に端末へ展開しています。LGPL-2.1 の全文は
<https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html> を参照してください。

## Il2CppInterop Fusion (LGPL-3.0)

[Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) (All-Of-Us-Mods) is bundled
unmodified inside the same `BepInEx-arm64.zip` in the Android launcher APK. The full text of the GNU
Lesser General Public License v3.0 is available at <https://www.gnu.org/licenses/lgpl-3.0.html>, and the
project's own repository carries its license file.

同じ `BepInEx-arm64.zip` に無改変で同梱しています。LGPL-3.0 の全文は <https://www.gnu.org/licenses/lgpl-3.0.html> を参照してください。

## .NET runtime

The .NET runtime (Microsoft & .NET Foundation, [dotnet/runtime](https://github.com/dotnet/runtime)) is
bundled unmodified inside the Android launcher APK (`Android/Launcher/fusionApp/src/main/assets/dotnet-arm64.zip`)
under the MIT License. The full text is available at <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>.

## AndroidUtilities / AuthFix

[AndroidUtilities](https://github.com/All-Of-Us-Mods/AndroidUtilities) (All-Of-Us-Mods, including
the `AuthFix` plugin) was read for reference on how the Android build signs in. No code from it is
copied into End K not; the corresponding logic here was written independently. The repository
published no license file at the time of writing.

[AndroidUtilities](https://github.com/All-Of-Us-Mods/AndroidUtilities)（All-Of-Us-Mods、`AuthFix` を含む）は
Android 版のログイン手法の参考としてのみ参照しました。コードの複製はありません。
