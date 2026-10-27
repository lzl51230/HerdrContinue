param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$bytes=[IO.File]::ReadAllBytes((Join-Path (Split-Path $AppPath) 'HerdrAutoContinue.dll'))
$unicode=[Text.Encoding]::Unicode
if(-not ($unicode.GetString($bytes).Contains('WATCHDOG_QA_SESSION') -or $unicode.GetString($bytes,1,$bytes.Length-1).Contains('WATCHDOG_QA_SESSION'))){throw 'Debug build required'}
$scope='watchdog-qa-theme-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$state=Join-Path $OutputDirectory $scope
New-Item -ItemType Directory -Force $state|Out-Null
$env:WATCHDOG_QA_SESSION=$scope
$env:WATCHDOG_QA_SETTINGS_DIR=$state
$results=[ordered]@{}
if((Get-Process -Id $PID).SessionId -eq 0){throw 'Run on the logged-in desktop'}
foreach($stage in @('themes','restart','hidden')){
 $folder=Join-Path $OutputDirectory $stage
 New-Item -ItemType Directory -Force $folder|Out-Null
 $env:WATCHDOG_QA_SCENARIO=if($stage -eq 'themes'){'themes'}else{'theme-restart'}
 $env:WATCHDOG_QA_AUTOMATION_DIR=$folder
 $info=[Diagnostics.ProcessStartInfo]::new($AppPath)
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true
 if($stage -eq 'hidden'){$info.Arguments='--autostart'}
 $process=[Diagnostics.Process]::Start($info)
 try{
  if(-not $process.WaitForExit(60000)){throw "Timed out: $stage"}
  if($process.ExitCode -ne 0){throw "Application failed: $stage"}
  $result=Get-Content (Join-Path $folder 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
  $results[$stage]=$result
  if(-not $result.success){throw ($result|ConvertTo-Json -Depth 5)}
 }finally{if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}}
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($results|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
