# read-message.ps1 —— 读取小橙看班本地 Jet 数据库（Data.idb）里“刚刚产生”的消息，输出 JSON。
#
# 由 XiaoChengLink 插件在触发提醒时调用（32 位 Windows PowerShell，因为 Jet OLEDB 4.0 只有 32 位提供程序）。
#
# 2026-11-11 修复：
#   · WelcomeWord 的 AddUser 只作为“发送人”，**必须 Content 非空才算一条消息**；
#     以前把 AddUser 当 title，行刚插入、Content 还没落盘时就会抓到“只有发送人”的半成品行。
#   · 输出字段改为 sender + content（不再把发送人塞进 title）。
#   · 只接受触发前 FreshSeconds 秒内的行；找不到就重试（重新复制实时库），最多 4 次。
#
# 只读：Mode=Read 打开数据库副本；不写入、不修改任何数据。
#
# 用法：powershell.exe -File read-message.ps1 -DbPath <副本> -FreshSeconds 30 -LiveDbPath D:\inArtBoard\Data.idb [-Tables "WelcomeWord,..."]
# 输出：{"ok":true,"table":"WelcomeWord","sender":"钱昶翰","content":"最终测试XYZ","time":"..."}
#       {"ok":false,"error":"no fresh message"}
param(
  [Parameter(Mandatory=$true)][string]$DbPath,
  [int]$MaxAgeSeconds = 0,
  [int]$FreshSeconds = 30,
  [string]$Tables = '',
  [string]$LiveDbPath = 'D:\inArtBoard\Data.idb'
)
$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$table, [string]$sender, [string]$content, [string]$time, [string]$errorText) {
  return [ordered]@{
    ok = $ok; table = $table; sender = $sender; content = $content; time = $time; error = $errorText
  }
}
function Write-Json($obj) { [Console]::Out.Write(($obj | ConvertTo-Json -Compress -Depth 3)) }

# 表名/字段名来自对 D:\inArtBoard\Data.idb 的实测（Jet/Access 格式，24 张表）。
# 注意：只有 Content（或等价正文字段）非空才算一条完整消息；AddUser 只是发送人，不能单独构成消息。
$sources = @(
  @{ Table = 'HomeWork';      Sender = @('AddUserName'); Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ClassNotice';   Sender = @('AddUserName'); Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'SchoolNotice';  Sender = @();             Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ParentMsg';     Sender = @();             Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'WelcomeWord';   Sender = @('AddUser');    Content = @('Content');  Time = @('Addtime','BeginTime') },
  @{ Table = 'ScheduledTask'; Sender = @('AddUser');    Content = @('Notice');   Time = @('AddTime','StartTime','EndTime') },
  @{ Table = 'ExamClock';     Sender = @('AddUser');    Content = @('TitleTip'); Time = @('AddTime','BeginTime','ExamDate') },
  @{ Table = 'Album';         Sender = @();             Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'Video';         Sender = @();             Content = @('Title');    Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'CountDown';     Sender = @('AddUser');    Content = @('Title');    Time = @('EndDay') }
)

if ($Tables -and $Tables.Trim().Length -gt 0) {
  $wanted = @($Tables.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  if ($wanted.Count -gt 0) {
    $sources = @($sources | Where-Object { $wanted -contains $_.Table })
  }
}

function Read-Text($rs, $fields) {
  foreach ($f in $fields) {
    try {
      $v = [string]$rs.Fields.Item($f).Value
      if ($v -and $v.Trim().Length -gt 0) { return $v.Trim() }
    } catch { }
  }
  return ''
}

function Read-Time($rs, $fields) {
  foreach ($f in $fields) {
    try {
      $v = $rs.Fields.Item($f).Value
      if ($null -ne $v) {
        $parsed = [datetime]::MinValue
        if ([datetime]::TryParse([string]$v, [ref]$parsed)) { return $parsed }
      }
    } catch { }
  }
  return [datetime]::MinValue
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
          $content = Read-Text $rs $src.Content
          # 关键：Content 为空的行直接跳过（避免抓到只有 AddUser 的半成品行）
          if ($content.Length -eq 0) { $rs.MoveNext(); continue }

          $sender = Read-Text $rs $src.Sender
          $t = Read-Time $rs $src.Time
          if ($t -ne [datetime]::MinValue -and $t -ge $minTime -and $t -le $now.AddMinutes(5)) {
            if ($null -eq $best -or $t -gt $bestTime) {
              $best = [ordered]@{ Table = $src.Table; Sender = $sender; Content = $content; Time = $t }
              $bestTime = $t
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
      # 触发瞬间的副本可能还没落盘：重试时重新复制实时库。
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

  Write-Json (New-Result $true $best.Table $best.Sender $best.Content $best.Time.ToString('yyyy-MM-dd HH:mm:ss') '')
  exit 0
} catch {
  Write-Json (New-Result $false '' '' '' '' $_.Exception.Message)
  exit 3
}
