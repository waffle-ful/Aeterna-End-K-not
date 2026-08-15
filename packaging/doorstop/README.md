# Unity Doorstop 4.5.1 (同梱バイナリの出所)

このディレクトリの `winhttp.dll` は第三者製バイナリ **Unity Doorstop 4.5.1** です。

- 原作: [NeighTools/UnityDoorstop](https://github.com/NeighTools/UnityDoorstop) (LGPL-2.1)
- 入手元: 上流 [Gurge44/EndlessHostRoles](https://github.com/Gurge44/EndlessHostRoles) commit `7af710d4` の
  `packaging-steam/` / `packaging-epic/` から無改変で取り込み (winhttp.dll / .doorstop_version とも同一ハッシュ)
- ストア別にビット数が異なるため**混用禁止**: `steam/` = x86 (22,528 bytes) / `epic/` = x64 (27,136 bytes)
- `doorstop_config.ini` のみ当 fork 向けに調整済み (target_assembly / coreclr パス)。
  `[Environment] GC_DISABLE_INCREMENTAL=1` は上流由来の設定をそのまま維持
