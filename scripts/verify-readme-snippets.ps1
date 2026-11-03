#!/usr/bin/env pwsh
<#
.SYNOPSIS
    檢查兩份 README 的 C# 範例，每一行都真的出現在 samples/ReadmeSnippets/Program.cs 裡。

.DESCRIPTION
    samples/ReadmeSnippets 會在 CI 編譯並執行 README 教的每一個呼叫 —— 但它是手抄的一份。
    API 改名或改簽章時，ReadmeSnippets 編不過，開發者會把它修到編得過，README 卻沒有東西逼它
    跟著改：守門員綠了，文件照樣過期。這支腳本把那條「README 跟 ReadmeSnippets 一字不差」的慣例
    變成檢查，兩件事合起來才等於「README 的每一行 C# 都編譯並執行過」。

    比對規則（兩份 README 與 Program.cs 都先做同樣的正規化）：

      - 只看 ```csharp（或 ```cs、```c#）區塊；mermaid、指令列、表結構那些圍籬不看。
      - 拿掉 // 註解、把字串與字元常值的內容清空、連續空白收成一個、去掉空行。
        所以中文版把 "paper" 寫成 "論文"、註解翻成中文都沒關係；Program.cs 多寫的註解也沒關係。
      - 每個區塊的每一行都要在 Program.cs 找得到，而且照 README 的順序出現。
        中間可以夾 Program.cs 自己的鷹架（建資料、驗結果的那些行），README 不必照抄那些。

    另外兩項：

      - 兩份 README 的 C# 區塊數量要相同，對應的區塊正規化後要一字不差
        （中文版只能翻字串和註解，程式本身不能跟英文版各寫各的）。
      - README「Compile-time safety net」表格列的診斷代碼，要等於分析器原始碼實際宣告的那一組
        （加了 CHSU003 卻沒寫進 README、或 README 還列著已經拿掉的規則，都會紅燈）。

    沒有 -Update：兩邊都是人寫的，Program.cs 夾著鷹架、中文版翻了字串，
    沒有一份「實際輸出」可以拿來覆寫 README。對不上時，會印出 README 的檔名與行號、
    正規化後的那一行，以及 Program.cs 裡最像的一行（通常就是改名之後的新寫法），照著改即可。

    這支只讀文字、不建置也不執行任何東西，所以不會被 NuGet 警告之類的建置輸出干擾；
    「Program.cs 真的編得過、跑得過」由 CI 的 `dotnet run --project samples/ReadmeSnippets` 那一步負責。

.EXAMPLE
    pwsh scripts/verify-readme-snippets.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
# 正斜線：CI 在 Linux 上用 pwsh 跑，反斜線在那裡是檔名的一部分而不是分隔線
$snippetsPath = Join-Path $repo 'samples/ReadmeSnippets/Program.cs'
$analyzerDir = Join-Path $repo 'src/Cornhsu.Labeling.Analyzers'
$readmes = @(
    @{ File = 'README.md';         RulesHeading = '## Compile-time safety net (Roslyn analyzer)' }
    @{ File = 'README.zh-Hant.md'; RulesHeading = '## 編譯期防呆(Roslyn Analyzer)' }
)

# 明確指定 UTF-8：Windows PowerShell 的 Get-Content 預設用系統 ANSI 讀，中文版 README 會整份變亂碼
function Read-Lines([string]$path) {
    return [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
}

# 一行 C# 的正規化：去 // 註解、清空字串與字元常值的內容、收空白。
# 不是完整的 C# 詞法分析，只需要「README 與 Program.cs 兩邊用同一套規則」就夠：
# 認得一般字串、逐字字串（@""，"" 是跳脫）、插值字串（$""）與字元常值，
# 讓字串裡的 // 不被當成註解、"論文" 與 "paper" 收成同一個 ""。
function Normalize-Line([string]$line) {
    $sb = [System.Text.StringBuilder]::new()
    $i = 0
    $n = $line.Length
    while ($i -lt $n) {
        $c = $line[$i]
        if ($c -eq '/' -and $i + 1 -lt $n -and $line[$i + 1] -eq '/') { break }
        if ($c -eq '"' -or $c -eq "'") {
            $verbatim = $c -eq '"' -and $sb.Length -gt 0 -and
                        ($sb.ToString() -match '@\$?$|\$@$')
            [void]$sb.Append($c)
            $i++
            while ($i -lt $n) {
                $d = $line[$i]
                if (-not $verbatim -and $d -eq '\') { $i += 2; continue }
                if ($d -eq $c) {
                    if ($verbatim -and $i + 1 -lt $n -and $line[$i + 1] -eq '"') { $i += 2; continue }
                    break
                }
                $i++
            }
            [void]$sb.Append($c)
            $i++
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return ($sb.ToString() -replace '\s+', ' ').Trim()
}

# 把一串原始行正規化成 { Line = 原始行號(1 起算); Text = 正規化後 }，空行丟掉
function Normalize-Block([string[]]$lines, [int]$firstLineNumber) {
    $out = [System.Collections.Generic.List[object]]::new()
    for ($k = 0; $k -lt $lines.Count; $k++) {
        $t = Normalize-Line $lines[$k]
        if ($t.Length -gt 0) { $out.Add([pscustomobject]@{ Line = $firstLineNumber + $k; Text = $t }) }
    }
    return , $out
}

# 抽出 README 裡所有 C# 圍籬區塊
function Get-CSharpBlocks([string[]]$lines) {
    $blocks = [System.Collections.Generic.List[object]]::new()
    $inside = $false; $isCs = $false; $start = 0
    for ($k = 0; $k -lt $lines.Count; $k++) {
        $trim = $lines[$k].Trim()
        if (-not $inside -and $trim -match '^```(.*)$') {
            $inside = $true; $start = $k + 1
            $isCs = $Matches[1].Trim() -in @('csharp', 'cs', 'c#')
            continue
        }
        if ($inside -and $trim -eq '```') {
            $inside = $false
            if ($isCs) {
                $body = if ($k -gt $start) { $lines[$start..($k - 1)] } else { @() }
                $blocks.Add([pscustomobject]@{
                    FenceLine = $start                       # 圍籬那一行的行號(1 起算)
                    Lines     = Normalize-Block $body ($start + 1)
                })
            }
        }
    }
    return , $blocks
}

# 給「找不到」的那一行一個提示：Program.cs 裡識別字最相近的那行（多半就是改名後的新寫法）
function Find-Closest([string]$text, $candidates) {
    $mine = @([regex]::Matches($text, '\w+') | ForEach-Object Value | Sort-Object -Unique)
    if ($mine.Count -eq 0) { return $null }
    $best = $null; $bestScore = 0.0
    foreach ($cand in $candidates) {
        $theirs = @([regex]::Matches($cand.Text, '\w+') | ForEach-Object Value | Sort-Object -Unique)
        if ($theirs.Count -eq 0) { continue }
        $common = @($mine | Where-Object { $theirs -ccontains $_ }).Count
        $score = $common / ($mine.Count + $theirs.Count - $common)
        if ($score -gt $bestScore) { $bestScore = $score; $best = $cand }
    }
    if ($bestScore -ge 0.5) { return $best }
    return $null
}

$snippets = Normalize-Block (Read-Lines $snippetsPath) 1
$snippetsRel = 'samples/ReadmeSnippets/Program.cs'
$problems = [System.Collections.Generic.List[string]]::new()
$blocksByFile = @{}

# ── 1. 每個 README 區塊 ⊆ Program.cs（依序）──────────────────────────────
foreach ($r in $readmes) {
    $lines = Read-Lines (Join-Path $repo $r.File)
    $blocks = Get-CSharpBlocks $lines
    $blocksByFile[$r.File] = $blocks

    if ($blocks.Count -eq 0) {
        # 圍籬語言標籤被改掉的話，這支會安靜地變成「零個區塊、全部通過」—— 寧可紅燈
        $problems.Add("✗ $($r.File)：一個 ``````csharp 區塊都沒找到，這項檢查會空轉。圍籬的語言標籤被改過？")
        continue
    }

    $checkedLines = 0
    foreach ($b in $blocks) {
        $pos = 0
        foreach ($l in $b.Lines) {
            $checkedLines++
            $hit = -1
            for ($k = $pos; $k -lt $snippets.Count; $k++) {
                if ($snippets[$k].Text -ceq $l.Text) { $hit = $k; break }
            }
            if ($hit -ge 0) { $pos = $hit + 1; continue }

            $msg = "✗ $($r.File):$($l.Line)（第 $($b.FenceLine) 行開始的區塊）在 $snippetsRel 找不到：`n" +
                   "      $($l.Text)"
            $earlier = @($snippets | Select-Object -First $pos | Where-Object { $_.Text -ceq $l.Text })
            if ($earlier.Count -gt 0) {
                $msg += "`n      → 這行在 ${snippetsRel}:$($earlier[0].Line) 有，但順序跟 README 不一樣"
            } else {
                $near = Find-Closest $l.Text $snippets
                if ($near) { $msg += "`n      → 最像的是 ${snippetsRel}:$($near.Line)：$($near.Text)" }
            }
            $problems.Add($msg)
        }
    }
    Write-Host "$($r.File)：$($blocks.Count) 個 C# 區塊、$checkedLines 行程式已比對"
}

# ── 2. 兩份 README 的程式彼此一致 ─────────────────────────────────────────
$en = $readmes[0].File; $zh = $readmes[1].File
if ($blocksByFile.ContainsKey($en) -and $blocksByFile.ContainsKey($zh)) {
    $a = $blocksByFile[$en]; $b = $blocksByFile[$zh]
    if ($a.Count -ne $b.Count) {
        $problems.Add("✗ $en 有 $($a.Count) 個 C# 區塊，$zh 有 $($b.Count) 個 —— 有一份少了或多了一段範例")
    } else {
        for ($k = 0; $k -lt $a.Count; $k++) {
            $x = @($a[$k].Lines | ForEach-Object Text) -join "`n"
            $y = @($b[$k].Lines | ForEach-Object Text) -join "`n"
            if ($x -cne $y) {
                $problems.Add("✗ 第 $($k + 1) 個 C# 區塊（${en}:$($a[$k].FenceLine) ↔ ${zh}:$($b[$k].FenceLine)）" +
                              "除了字串與註解之外不一樣 —— 兩份 README 教的程式要相同")
            }
        }
    }
}

# ── 3. README 列的診斷代碼 = 分析器實際宣告的 ────────────────────────────
$declared = @(Get-ChildItem $analyzerDir -Filter *.cs -Recurse |
    ForEach-Object { [regex]::Matches([System.IO.File]::ReadAllText($_.FullName), '"(CHSU\d{3})"') } |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($declared.Count -eq 0) {
    $problems.Add("✗ 在 src/Cornhsu.Labeling.Analyzers 找不到任何 ""CHSU###"" 診斷代碼 —— 分析器的寫法變了，這項檢查需要跟著改")
}
foreach ($r in $readmes) {
    $lines = Read-Lines (Join-Path $repo $r.File)
    $h = [Array]::FindIndex($lines, [Predicate[string]] { param($x) $x.Trim() -eq $r.RulesHeading })
    if ($h -lt 0) {
        $problems.Add("✗ $($r.File)：找不到標題「$($r.RulesHeading)」—— 錨點被改掉了，這支腳本需要跟著更新")
        continue
    }
    # 標題之後、下一個 ## 之前，表格列第一格的診斷代碼
    $listed = [System.Collections.Generic.List[string]]::new()
    for ($k = $h + 1; $k -lt $lines.Count -and -not $lines[$k].StartsWith('## '); $k++) {
        if ($lines[$k] -match '^\|\s*`(CHSU\d{3})`\s*\|') { $listed.Add($Matches[1]) }
    }
    $listed = @($listed | Sort-Object -Unique)
    $missing = @($declared | Where-Object { $listed -notcontains $_ })
    $stale = @($listed | Where-Object { $declared -notcontains $_ })
    if ($missing.Count -gt 0) {
        $problems.Add("✗ $($r.File)「$($r.RulesHeading)」的表格少了分析器有宣告的規則：$($missing -join ', ')")
    }
    if ($stale.Count -gt 0) {
        $problems.Add("✗ $($r.File)「$($r.RulesHeading)」的表格列了分析器沒有的規則：$($stale -join ', ')")
    }
    if ($missing.Count -eq 0 -and $stale.Count -eq 0) {
        Write-Host "$($r.File)：診斷代碼 $($listed -join ', ') 與分析器一致"
    }
}

Write-Host ''
if ($problems.Count -eq 0) {
    Write-Host 'README 的 C# 範例都在 ReadmeSnippets 裡，兩份 README 的程式一致，診斷代碼與分析器一致。'
    exit 0
}

$problems | ForEach-Object { Write-Host $_ }
Write-Host ''
Write-Host '修法：API 改了 → 照 Program.cs 的新寫法改兩份 README（中文版的字串、註解照翻）；'
Write-Host '      README 新增或改了範例 → 在 samples/ReadmeSnippets/Program.cs 補上同樣的程式，讓它被編譯、執行；'
Write-Host '      診斷代碼對不上 → 更新兩份 README 的規則表（新規則也要寫進 docs/analyzer-rules.md）。'
exit 1
