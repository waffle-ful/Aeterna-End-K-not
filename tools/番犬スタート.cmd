@echo off
rem =====================================================================
rem  End K not 番犬ランチャー  ―  ダブルクリックで番犬(ウォッチドッグ)を起動
rem ---------------------------------------------------------------------
rem  このファイルを EndKnotWatchdog.ps1 と同じフォルダに置いてダブルクリック
rem  するだけで、Among Us がクラッシュ/ハングしたら自動で立て直す番犬が
rem  起動します。閉じるにはこのウィンドウを閉じるか Ctrl+C。
rem
rem  配布する時は「EndKnotWatchdog.ps1」と「番犬スタート.cmd」の2つを
rem  同じフォルダに入れて渡せばOKです（EndKnot 本体DLLは不要）。
rem =====================================================================
chcp 65001 >nul
cd /d "%~dp0"

if not exist "%~dp0EndKnotWatchdog.ps1" (
    echo.
    echo [エラー] EndKnotWatchdog.ps1 が同じフォルダに見つかりません。
    echo          この .cmd と .ps1 は同じフォルダに置いてください。
    echo.
    pause
    exit /b 1
)

echo End K not の番犬を起動します...（このウィンドウは開いたままにしてください）
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0EndKnotWatchdog.ps1"

echo.
echo 番犬が停止しました。ウィンドウを閉じてよいです。
pause
