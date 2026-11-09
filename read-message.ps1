# read-message.ps1 —— 读取小橙看班本地 Jet 数据库（Data.idb）中「刚刚产生」的消息/播报记录，输出 JSON。
#
# 由 XiaoChengLink 插件在触发提醒时调用（32 位 Windows PowerShell，因为 Jet OLEDB 4.0 是 32 位提供程序）。
# 关键修复（2026-11-09）：
#   · 只接受最近 FreshSeconds 秒内的记录（默认 30 秒），避免把数据库里很久以前的旧消息（如别的窗口/别的测试）
#     误当成当前播报内容；
#   · 触发瞬间数据库可能还没落盘/消息行可能刚插入后被很快删除，因此脚本内部会重试：
#     如果传入的临时副本里没有新记录，会重新复制 LiveDbPath 指向的实时数据库再查（最多 4 次，每次 1 秒）；
#   · 全屏播报（窗口类 FMTfrmText2Voice）实测写入 WelcomeWord 表：Content=播报文字、Addtime=插入时间，
#     且该行在播报结束后会被删除，所以“新鲜度 + 重试”是必须的。
#
# 只读：Mode=Read 打开数据库副本；不写入、不修改任何数据。
#
# 用法：powershell.exe -File read-message.ps1 -DbPath <副本> -MaxAgeSeconds 3600 [-FreshSeconds 30] [-Tables "WelcomeWord,..."] [-LiveDbPath D:\inArtBoard\Data.idb]
# 输出：{"ok":true,"table":"WelcomeWord","title":"","content":"...","time":"..."} 或 {"ok":false,"error":"no fresh message"}
param(
  [Parameter(Mandatory=$true)][string]$DbPath,
  [int]$MaxAgeSeconds = 0,
  [int]$FreshSeconds = 30,
  [string]$Tables = '',
  [string]$LiveDbPath = 'D:\inArtBoard\Data.idb'
)
$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$table, [string]$title, [string]$content, [string]$time, [string]$errorText) {
  return [ordered]@{
    ok = $ok; table = $table; title = $title; content = $content; time = $time; error = $errorText
  }
}
function Write-Json($obj) { [Console]::Out.Write(($obj | ConvertTo-Json -Compress -Depth 3)) }

$sources = @(
  @{ Table = 'HomeWork';      Title = @('Title');    Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ClassNotice';   Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'SchoolNotice';  Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ParentMsg';     Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'WelcomeWord';   Title = @('AddUser');  Content = @('Content');  Time = @('Addtime','BeginTime') },
  @{ Table = 'ScheduledTask'; Title = @('TaskType'); Content = @('Notice');   Time = @('AddTime','StartTime','EndTime') },
  @{ Table = 'ExamClock';     Title = @('TitleTip'); Content = @('TitleTip'); Time = @('AddTime','BeginTime','ExamDate') },
  @{ Table = 'Album';         Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'Video';         Title = @('Title');    Content = @('VideoUrl'); Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'CountDown';     Title = @('Title');    Content = @();           Time = @('EndDay') }
)

# 可选：按窗口类名只查对应表（插件后续版本会传 -Tables；未传则查全部）
if ($Tables -and $Tables.Trim().Length -gt 0) {
  $wanted = @($Tables.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  if ($wanted.Count -gt 0) {
    $sources = @($sources | Where-Object { $wanted -contains $_.Table })
  }
}

function Query-LatestFresh([string]$path) {
  $conn = New-Object -ComObject ADODB.Connection
  $conn.Open("Provider=Microsoft.Jet.OLEDB.4.0;Data Source=$path;Mode=Read;")
  $best = $null
  $bestTime = [datetime]::MinValue
  $now = [datetime]::Now
  $minTime = $now.AddSeconds(-1 * [Math]::Max(1, $FreshSeconds))
  $errors = @()
  try {
    foreach ($src in $sources) {
      try {
        $rs = $conn.Execute("SELECT * FROM [" + $src.Table + "]")
        while (-not $rs.EOF) {
          $title = ''
          foreach ($f in $src.Title) {
            try { $v = [string]$rs.Fields.Item($f).Value } catch { $v = '' }
            if ($v -and $v.Trim().Length -gt 0) { $title = $v.Trim(); break }
          }
          $content = ''
          foreach ($f in $src.Content) {
            try { $v = [string]$rs.Fields.Item($f).Value } catch { $v = '' }
            if ($v -and $v.Trim().Length -gt 0) { $content = $v.Trim(); break }
          }
          $t = [datetime]::MinValue
          foreach ($f in $src.Time) {
            try {
              $v = $rs.Fields.Item($f).Value
              if ($null -ne $v) {
                $parsed = [datetime]::MinValue
                if ([datetime]::TryParse([string]$v, [ref]$parsed)) { $t = $parsed; break }
              }
            } catch { }
          }
          if ((($title.Length -gt 0) -or ($content.Length -gt 0)) -and $t -ne [datetime]::MinValue) {
            if ($t -ge $minTime -and $t -le $now.AddMinutes(5)) {
              if ($null -eq $best -or $t -gt $bestTime) {
                $best = [ordered]@{ Table = $src.Table; Title = $title; Content = $content; Time = $t }
                $bestTime = $t
              }
            }
          }
          $rs.MoveNext()
        }
        $rs.Close()
      } catch { $errors += ($src.Table + ': ' + $_.Exception.Message) }
    }
  } finally {
    $conn.Close()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($conn) | Out-Null
  }
  return @{ Best = $best; Errors = $errors }
}

try {
  $best = $null
  $lastError = ''
  for ($attempt = 1; $attempt -le 4; $attempt++) {
    $checkPath = $DbPath
    if ($attempt -gt 1 -and $LiveDbPath -and (Test-Path -LiteralPath $LiveDbPath)) {
      # 传入的副本是触发瞬间复制的；重试时重新从实时库复制，避免错过刚写入的行。
      try {
        $tmp = Join-Path $env:TEMP ('xc-fresh-' + [guid]::NewGuid().ToString('N') + '.idb')
        $fs = [System.IO.File]::Open($LiveDbPath, 'Open', 'Read', 'ReadWrite')
        try { $out = [System.IO.File]::Create($tmp); $fs.CopyTo($out); $out.Close() } finally { $fs.Close() }
        $checkPath = $tmp
      } catch { }
    }

    if (Test-Path -LiteralPath $checkPath) {
      $r = Query-LatestFresh $checkPath
      if ($null -ne $r.Best) { $best = $r.Best; break }
      if ($r.Errors.Count -gt 0) { $lastError = ($r.Errors -join '; ') }
    }

    if ($attempt -lt 4) { Start-Sleep -Seconds 1 }
  }

  if ($null -eq $best) {
    Write-Json (New-Result $false '' '' '' '' ('no fresh message within ' + $FreshSeconds + 's; ' + $lastError))
    exit 1
  }

  Write-Json (New-Result $true $best.Table $best.Title $best.Content $best.Time.ToString('yyyy-MM-dd HH:mm:ss') '')
  exit 0
} catch {
  Write-Json (New-Result $false '' '' '' '' $_.Exception.Message)
  exit 3
}
