<#
.SYNOPSIS
    公開ファイルの「内部事情コメント」チェッカー (End K not)

.DESCRIPTION
    このリポジトリは公開されている。一方で docs/ ツリー・.claude メモリ・
    内部バグ台帳は gitignore 済みで、クローンした第三者からは一切見えない。
    そのためコード内のコメントに

      - レビュー帰属 (「監査指摘」「advisor 指摘」「ご主人様裁定」…)
      - 内部メモリ参照 (memory: <slug> / MEMORY.md)
      - gitignore 済みドキュメントへのパス (docs/foo.md)
      - 内部バグ台帳 ID (BUG-YYYYMMDD-NN)
      - 内部設計案ラベル (A案 / B案)

    が残っていると、(a) 開発体制そのものが露出し、(b) 第三者には永久に
    解決できない宙ぶらりんの参照になる。

    コメントに書いてよいのは「コードが何をするか / なぜそうでなければ
    ならないか」だけで、「誰が見つけたか・いつレビューされたか・何が
    却下されたか」は書かない。

    日付そのものは禁止ではない。技術的事象を指す日付 (実機確認 / 実測 / 修正) は
    残してよく、レビュー事象を指す日付だけを消す。

    このスクリプトは違反を検出した時点で exit 1 → ビルド失敗にする。

.NOTES
    ⚠️ このファイルは UTF-8 **BOM 付き** で保存すること。
       csproj のターゲットは Windows PowerShell 5.1 で実行され、BOM が無いと
       ANSI として読まれて日本語文字列でパースエラーになる。
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$selfPath = $MyInvocation.MyCommand.Path

# ── 検査対象 ────────────────────────────────────────────────────────────────
# 出荷物 / 公開されるソースのみ。ゲーム内テキスト (Resources/Lang) は
# 役職名などに正当な語 (Auditor = 監査) が出るため対象外。
$includeGlobs = @(
    '*.cs', '*.ts', '*.tsx', '*.svelte', '*.html', '*.ps1', '*.csproj',
    '*.yml', '*.yaml', '*.props', '*.json', '*.jsonc'
)
$excludeDirRegex = '(^|[\\/])(\.git|\.claude|\.vs|\.idea|node_modules|vendor|obj|bin|build|dist|packages|docs|Resources|TestResults|src-tauri[\\/]target)([\\/]|$)'

# ── 禁止パターン ────────────────────────────────────────────────────────────
# Pattern は .NET 正規表現。Hint は違反時に表示する直し方。
$rules = @()
function Add-Rule($name, $pattern, $hint) {
    $script:rules += [pscustomobject]@{ Name = $name; Pattern = $pattern; Hint = $hint }
}

Add-Rule 'review-attribution' `
    '監査|指摘|裁定|兄弟スイープ' `
    '「誰がいつレビューしたか」は書かない。帰属の括弧だけ落とし、技術的理由は残す'

Add-Rule 'dev-loop-actor' `
    '(?i)\badvisor\b|\bcoordinator\b|ご主人様|ユーザー(要望|選択|証言|提案|仮説|報告|裁定)|user (要望|request)' `
    '開発体制 (AI 補助・単独運用) が露出する。行為者を消して事実だけ書く'

Add-Rule 'internal-memory-ref' `
    'memory:\s*[A-Za-z][A-Za-z0-9_.-]{5,}|memory (参照|罠)|蓄積 memory|MEMORY\.md' `
    '.claude メモリは gitignore 済み。スラッグを平文の技術説明に置き換える'

# メモリファイル名は `memory:` 接頭辞なしの裸のスラッグや [[wikilink]] でも書かれる。
# 命名は project_ / reference_ / feedback_ + snake_case (MEMORY.md の索引と同じ形)。
# ⚠️ `[[...]]` 自体は禁止しない — `[[LocalPlayerFeet]]` のようにリポジトリ内の実在シンボル/
#    ファイル行への参照にも使われており、そちらは第三者が解決できるので正当。
Add-Rule 'internal-memory-slug' `
    '(?<![A-Za-z0-9_])(project|reference|feedback)[_-][a-z0-9]+[_-][a-z0-9_-]{4,}' `
    '.claude メモリのファイル名。参照ごと消し、必要なら要点を平文で書き下す'

Add-Rule 'gitignored-docs-ref' `
    'docs[\\/][A-Za-z0-9._-]+\.md' `
    'docs/ は gitignore 済みで第三者には開けない。パスを消し、必要なら要点を平文で書く'

Add-Rule 'bug-ledger-id' `
    'BUG-\d{8}-\d+' `
    '内部バグ台帳 ID は公開リポジトリでは解決できない。ID を消して症状を平文で書く'

Add-Rule 'internal-design-label' `
    '(?<![A-Za-z0-9])[A-Z]案|案[A-Z](?![A-Za-z0-9])' `
    '内部設計案ラベル (A案/B案) は書かない。採用した実装の事実だけ書く'

Add-Rule 'assistant-workspace-ref' `
    'CLAUDE\.md|(?<![A-Za-z0-9._-])\.claude[\\/]' `
    'Claude 用の運用ノートは gitignore 済み。公開ファイルから参照しない'

# ── 既知の正当な例外 ────────────────────────────────────────────────────────
# "<相対パス>:<行番号>" 完全一致、またはパスだけの指定でファイル丸ごと除外。
# 追加するときは「なぜ正当か」を必ずコメントで残すこと。
$allowList = @(
    # .gitignore を書くための参照ではなく、gitignore 規則そのもの
    '.gitignore',
    # BepInEx 設定の注記: release.ps1 が false を保証している旨の運用メモ
    'packaging/BepInEx/config/BepInEx.cfg'
)

# ── 走査対象の確定 ──────────────────────────────────────────────────────────
# 「公開されているか」の正典は git の管理下にあるかどうか。gitignore 済みのファイル
# (tools/ の非公開スクリプト、docs/、mockups/ 等) は第三者から見えないので対象外。
# ファイルシステム走査だと gitignore 済みの私物まで拾って誤検知するため、必ず
# git ls-files を使う (-c: 追跡済み, -o --exclude-standard: 未追跡だが ignore されていない
# = これから追加されうる新規ファイル)。
# core.quotepath=false: これが無いと日本語ファイル名が "\346\227\245..." のように
# エスケープされて返り、パスとして扱えない (番犬スタート.cmd 等が実在する)。
$prevOutEnc = [Console]::OutputEncoding
Push-Location $root
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $tracked = & git -c core.quotepath=false ls-files -c -o --exclude-standard 2>$null
    $gitOk = ($LASTEXITCODE -eq 0)
}
catch { $gitOk = $false }
finally {
    [Console]::OutputEncoding = $prevOutEnc
    Pop-Location
}

if (-not $gitOk -or -not $tracked) {
    Write-Host "[check-forbidden-comments] SKIP - git の管理情報を取得できませんでした (git 不在 / リポジトリ外)。"
    exit 0
}

# 拡張子照合は IO.Path を使わない (git が返す文字列にパスとして不正な文字が混じりうる)。
$extPattern = '(' + (($includeGlobs | ForEach-Object { [regex]::Escape($_.TrimStart('*')) }) -join '|') + ')$'

$files = foreach ($rel in ($tracked | Sort-Object -Unique)) {
    if ([string]::IsNullOrWhiteSpace($rel)) { continue }
    if ($rel -match $excludeDirRegex) { continue }
    if ($rel -notmatch $extPattern) { continue }

    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    if ((Get-Item -LiteralPath $full).FullName -eq $selfPath) { continue }

    Get-Item -LiteralPath $full
}

$violations = @()

foreach ($file in $files) {
    $rel = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
    if ($allowList -contains $rel) { continue }

    $lines = [IO.File]::ReadAllLines($file.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        foreach ($rule in $rules) {
            $m = [regex]::Match($line, $rule.Pattern)
            if (-not $m.Success) { continue }
            if ($allowList -contains ("{0}:{1}" -f $rel, ($i + 1))) { continue }

            $violations += [pscustomobject]@{
                File  = $rel
                Line  = $i + 1
                Rule  = $rule.Name
                Hint  = $rule.Hint
                Match = $m.Value
                Text  = $line.Trim()
            }
        }
    }
}

# ── 報告 ────────────────────────────────────────────────────────────────────
if ($violations.Count -eq 0) {
    Write-Host ("[check-forbidden-comments] OK - {0} ファイルを検査し、内部事情コメントは 0 件でした。" -f $files.Count)
    exit 0
}

Write-Host ""
Write-Host ("[check-forbidden-comments] FAIL - 内部向けの覚え書きを {0} 件検出しました。" -f $violations.Count)
Write-Host "  公開リポジトリのコメントは第三者がそのまま読みます。"
Write-Host ""

foreach ($group in ($violations | Group-Object Rule | Sort-Object Name)) {
    Write-Host ("── {0} ({1} 件)" -f $group.Name, $group.Count)
    Write-Host ("   直し方: {0}" -f $group.Group[0].Hint)
    foreach ($v in $group.Group) {
        $text = $v.Text
        if ($text.Length -gt 110) { $text = $text.Substring(0, 110) + '…' }
        Write-Host ("   {0}:{1}  [{2}]  {3}" -f $v.File, $v.Line, $v.Match, $text)
    }
    Write-Host ""
}

Write-Host "日付そのものは禁止ではありません。技術的事象 (実機確認/実測/修正) を指す日付は残し、"
Write-Host "レビュー事象を指す日付だけ消してください。"
exit 1
