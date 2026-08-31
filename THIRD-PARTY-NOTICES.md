# Third-Party Notices / 同梱している第三者ソフトウェア

End K not itself is licensed under the GNU General Public License v3.0 (see [`LICENSE`](./LICENSE)).
The components listed below are the work of other authors and keep their own licenses.
They are either embedded into `EndKnot.dll` or shipped inside the release packages.

End K not 本体は GPL-3.0 です（[`LICENSE`](./LICENSE)）。以下は別の作者による著作物で、
それぞれ独自のライセンスに従います。`EndKnot.dll` に埋め込まれているもの、
配布パッケージに同梱されているものの両方を含みます。

| Component | Author / Project | License | Where |
|---|---|---|---|
| NVorbis 0.10.5 | Andrew Ward — [NVorbis](https://github.com/NVorbis/NVorbis) | MIT | embedded in `EndKnot.dll` (Ogg Vorbis decoding) |
| NLayer 1.16.0 | Mark Heath, Andrew Ward & Contributors (port of JavaLayer) — [NLayer](https://github.com/naudio/NLayer) | MIT | embedded in `EndKnot.dll` (MP3 decoding) |
| Unity Doorstop 4.5.1 (`winhttp.dll`, `doorstop_config.ini`) | NeighTools — [UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) | LGPL-2.1 | release packages |
| BepInEx (IL2CPP) | BepInEx team — [BepInEx](https://github.com/BepInEx/BepInEx) | LGPL-2.1 | release packages |
| Il2CppInterop | BepInEx team — [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) | LGPL-2.1 | release packages (`BepInEx/core`) |
| Mini.RegionInstall | miniduikboot — [Mini.RegionInstall](https://github.com/miniduikboot/Mini.RegionInstall) | GPL-3.0 | release packages (`BepInEx/plugins`) |
| CrowdedMod | andry08 & CrowdedMods — [CrowdedMod](https://github.com/CrowdedMods/CrowdedMod) | MIT | source, adapted in `Patches/Crowded.cs` |
| MiraAPI (UI sprites) | All-Of-Us-Mods — [MiraAPI](https://github.com/All-Of-Us-Mods/MiraAPI) | LGPL-2.1 | `Resources/Images/`: `ActiveNextButton.png`, `InactiveNextButton.png`, `Checkmark.png`, `CheckMarkBox.png` |

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
