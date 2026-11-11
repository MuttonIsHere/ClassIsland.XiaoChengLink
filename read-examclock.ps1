param([Parameter(Mandatory=$true)][string]$DbPath)
$ErrorActionPreference='Stop'
try {
  $c=New-Object System.Data.OleDb.OleDbConnection("Provider=Microsoft.Jet.OLEDB.4.0;Data Source=$DbPath;Mode=Read;");$c.Open()
  $cmd=$c.CreateCommand();$cmd.CommandText='SELECT * FROM [ExamClock]';$rd=$cmd.ExecuteReader()
  $list=@()
  while($rd.Read()){
    $o=[ordered]@{}
    for($i=0;$i -lt $rd.FieldCount;$i++){
      $n=$rd.GetName($i)
      $o[$n]=if($rd.IsDBNull($i)){$null}else{[string]$rd.GetValue($i)}
    }
    $list+=$o
  }
  $rd.Close();$c.Close()
  @{ok=$true;rows=$list}|ConvertTo-Json -Compress -Depth 4
} catch {
  @{ok=$false;error=$_.Exception.Message}|ConvertTo-Json -Compress
}
