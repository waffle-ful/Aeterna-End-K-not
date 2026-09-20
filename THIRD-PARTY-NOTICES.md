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
| Unity Doorstop 4.5.1 (`winhttp.dll`, `doorstop_config.ini`) | NeighTools — [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) | LGPL-2.1 | release packages |
| BepInEx (IL2CPP) | BepInEx team — [BepInEx](https://github.com/BepInEx/BepInEx) | LGPL-2.1 | release packages |
| Il2CppInterop | BepInEx team — [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | LGPL-2.1 | release packages (`BepInEx/core`) |
| Mini.RegionInstall | miniduikboot — [Mini.RegionInstall](https://github.com/miniduikboot/Mini.RegionInstall) | GPL-3.0 | source, adapted in `Modules/RegionInstaller.cs` |
| CrowdedMod | andry08 & CrowdedMods — [CrowdedMod](https://github.com/CrowdedMods/CrowdedMod) | MIT | source, adapted in `Patches/Crowded.cs` |
| MiraAPI (UI sprites) | All-Of-Us-Mods — [MiraAPI](https://github.com/All-Of-Us-Mods/MiraAPI) | LGPL-2.1 | `Resources/Images/`: `ActiveNextButton.png`, `InactiveNextButton.png`, `Checkmark.png`, `CheckMarkBox.png` |
| TownOfHost-Pko | satokazoku et al. — [TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko) | GPL-3.0 | source, adapted in `Patches/NumericOptionInputPatch.cs` and `Modules/ConsecutiveJoinKick.cs` |
| Lotus (LotusContinued) | Lotus-AU — [LotusContinued](https://github.com/Lotus-AU/LotusContinued) | GPL-3.0 | source, adapted in `Modules/WhitelistManager.cs` |
| FusionCore | All-Of-Us-Mods — [FusionCore](https://github.com/All-Of-Us-Mods/FusionCore) | GPL-3.0 | source, `Android/Launcher/` (git subtree, rebranded Android launcher) |
| BepInEx Fusion | All-Of-Us-Mods — [BepInExFusion](https://github.com/All-Of-Us-Mods/BepInExFusion) | LGPL-2.1 | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`) |
| Il2CppInterop Fusion | All-Of-Us-Mods — [Il2CppInteropFusion](https://github.com/All-Of-Us-Mods/Il2CppInteropFusion) | LGPL-3.0 | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/BepInEx-arm64.zip`) |
| Pine (with AndroidELF) | canyie — [pine](https://github.com/canyie/pine); AndroidELF by Swift Gan | Anti 996 License 1.0 | `Android/Launcher/libs/canyie-pine.aar` (build shipped by FusionCore) |
| .NET runtime | Microsoft & .NET Foundation — [dotnet/runtime](https://github.com/dotnet/runtime) | MIT | bundled in the launcher APK (`Android/Launcher/fusionApp/src/main/assets/dotnet-arm64.zip`) |
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

## LGPL-2.1 components (BepInEx, Il2CppInterop, Unity Doorstop, MiraAPI)

These are redistributed unmodified (except `doorstop_config.ini`, which is a plain
configuration file). The full text of the GNU Lesser General Public License v2.1 is
available at <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>, and each
project's own repository carries its license file.

これらは無改変で再配布しています（`doorstop_config.ini` は設定ファイルのため当 fork 向けに調整）。
LGPL-2.1 の全文は <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html> を参照してください。

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

## Pine

[Pine](https://github.com/canyie/pine) by canyie (bundling [AndroidELF](https://github.com/ganyao114/AndroidELF)
by Swift Gan) is shipped as `Android/Launcher/libs/canyie-pine.aar`, a build provided by the FusionCore
project. Pine publishes no separate license file; its README declares the following license.

```
Copyright (c) canyie (Pine)
AndroidELF Copyright (c) Swift Gan

"Anti 996" License Version 1.0 (Draft)

Permission is hereby granted to any individual or legal entity
obtaining a copy of this licensed work (including the source code,
documentation and/or related items, hereinafter collectively referred
to as the "licensed work"), free of charge, to deal with the licensed
work for any purpose, including without limitation, the rights to use,
reproduce, modify, prepare derivative works of, distribute, publish
and sublicense the licensed work, subject to the following conditions:

1. The individual or the legal entity must conspicuously display,
without modification, this License and the notice on each redistributed
or derivative copy of the Licensed Work.

2. The individual or the legal entity must strictly comply with all
applicable laws, regulations, rules and standards of the jurisdiction
relating to labor and employment where the individual is physically
located or where the individual was born or naturalized; or where the
legal entity is registered or is operating (whichever is stricter). In
case that the jurisdiction has no such laws, regulations, rules and
standards or its laws, regulations, rules and standards are
unenforceable, the individual or the legal entity are required to
comply with Core International Labor Standards.

3. The individual or the legal entity shall not induce, suggest or force
its employee(s), whether full-time or part-time, or its independent
contractor(s), in any methods, to agree in oral or written form, to
directly or indirectly restrict, weaken or relinquish his or her
rights or remedies under such laws, regulations, rules and standards
relating to labor and employment as mentioned above, no matter whether
such written or oral agreements are enforceable under the laws of the
said jurisdiction, nor shall such individual or the legal entity
limit, in any methods, the rights of its employee(s) or independent
contractor(s) from reporting or complaining to the copyright holder or
relevant authorities monitoring the compliance of the license about
its violation(s) of the said license.

THE LICENSED WORK IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN ANY WAY CONNECTION WITH THE
LICENSED WORK OR THE USE OR OTHER DEALINGS IN THE LICENSED WORK.
```

## AndroidUtilities / AuthFix

[AndroidUtilities](https://github.com/All-Of-Us-Mods/AndroidUtilities) (All-Of-Us-Mods, including
the `AuthFix` plugin) was read for reference on how the Android build signs in. No code from it is
copied into End K not; the corresponding logic here was written independently. The repository
published no license file at the time of writing.

[AndroidUtilities](https://github.com/All-Of-Us-Mods/AndroidUtilities)（All-Of-Us-Mods、`AuthFix` を含む）は
Android 版のログイン手法の参考としてのみ参照しました。コードの複製はありません。
