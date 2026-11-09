# read-message.ps1 —— 读取小橙看班本地 Jet 数据库（Data.idb）中最新的通知/消息记录，输出 JSON。
#
# 由 XiaoChengLink 插件在触发提醒时调用（32 位 Windows PowerShell，因为 Jet OLEDB 4.0 是 32 位提供程序）。
# 只读：Mode=Read 打开数据库副本；不写入、不修改任何数据。
#
# 用法：powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File read-message.ps1 -DbPath <path> -MaxAgeSeconds 3600
# 输出：{"ok":true,"table":"HomeWork","title":"...","content":"...","time":"yyyy-MM-dd HH:mm:ss"}
#       {"ok":false,"error":"..."}
param(
  [Parameter(Mandatory=$true)][string]$DbPath,
  [int]$MaxAgeSeconds = 0
)
$ErrorActionPreference = 'Stop'

function New-Result([bool]$ok, [string]$table, [string]$title, [string]$content, [string]$time, [string]$errorText) {
  return [ordered]@{
    ok = $ok
    table = $table
    title = $title
    content = $content
    time = $time
    error = $errorText
  }
}

function Write-Json($obj) {
  $json = $obj | ConvertTo-Json -Compress -Depth 3
  [Console]::Out.Write($json)
}

# 表名/字段名来自对 D:\inArtBoard\Data.idb 的实测（Jet/Access 格式，24 张表）。
$sources = @(
  @{ Table = 'HomeWork';      Title = @('Title');    Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ClassNotice';   Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'SchoolNotice';  Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ParentMsg';     Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'WelcomeWord';   Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'ScheduledTask'; Title = @('TaskType'); Content = @('Notice');   Time = @('StartTime','AddTime','EndTime') },
  @{ Table = 'ExamClock';     Title = @('TitleTip'); Content = @('TitleTip'); Time = @('BeginTime','ExamDate','AddTime') },
  @{ Table = 'Album';         Title = @();           Content = @('Content');  Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'Video';         Title = @('Title');    Content = @('VideoUrl'); Time = @('BeginTime','Addtime','AddTime') },
  @{ Table = 'CountDown';     Title = @('Title');    Content = @();           Time = @('EndDay') }
)

try {
  if (-not (Test-Path -LiteralPath $DbPath)) {
    Write-Json (New-Result $false '' '' '' '' 'db file not found')
    exit 2
  }

  $conn = New-Object -ComObject ADODB.Connection
  $conn.Open("Provider=Microsoft.Jet.OLEDB.4.0;Data Source=$DbPath;Mode=Read;")

  $best = $null
  $bestTime = [datetime]::MinValue
  $errors = @()

  foreach ($src in $sources) {
    try {
      $sql = "SELECT * FROM [" + $src.Table + "]"
      $rs = $conn.Execute($sql)
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

        if (($title.Length -gt 0) -or ($content.Length -gt 0)) {
          $candidate = [ordered]@{ Table = $src.Table; Title = $title; Content = $content; Time = $t }
          if ($null -eq $best -or $t -gt $bestTime) { $best = $candidate; $bestTime = $t }
        }

        $rs.MoveNext()
      }
      $rs.Close()
    } catch {
      $errors += ($src.Table + ': ' + $_.Exception.Message)
    }
  }

  $conn.Close()
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($conn) | Out-Null

  if ($null -eq $best) {
    Write-Json (New-Result $false '' '' '' '' ('no message found; ' + ($errors -join '; ')))
    exit 1
  }

  if ($MaxAgeSeconds -gt 0 -and $bestTime -ne [datetime]::MinValue) {
    $age = ([datetime]::Now - $bestTime).TotalSeconds
    if ($age -gt $MaxAgeSeconds) {
      Write-Json (New-Result $false '' '' '' $bestTime.ToString('yyyy-MM-dd HH:mm:ss') ('message too old: ' + $best.Time))
      exit 1
    }
  }

  Write-Json (New-Result $true $best.Table $best.Title $best.Content $bestTime.ToString('yyyy-MM-dd HH:mm:ss') '')
  exit 0
} catch {
  Write-Json (New-Result $false '' '' '' '' $_.Exception.Message)
  exit 3
}
