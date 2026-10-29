param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if((Get-Process -Id $PID).SessionId -eq 0){throw 'Run on the logged-in desktop'}
$scope='watchdog-qa-features-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$state=Join-Path $OutputDirectory $scope
New-Item -ItemType Directory -Force $state|Out-Null
$env:WATCHDOG_QA_SESSION=$scope
$env:WATCHDOG_QA_SETTINGS_DIR=$state
$results=[ordered]@{}
foreach($stage in @('features','features-restart')){
 $folder=Join-Path $OutputDirectory $stage
 $env:WATCHDOG_QA_SCENARIO=$stage
 $env:WATCHDOG_QA_AUTOMATION_DIR=$folder
 $process=Start-Process -FilePath $AppPath -PassThru
 try{
  if(-not $process.WaitForExit(45000)){throw "Timed out: $stage"}
  if($process.ExitCode -ne 0){throw "Application failed: $stage"}
  $result=Get-Content (Join-Path $folder 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
  $results[$stage]=$result
  if(-not $result.success){throw ($result|ConvertTo-Json -Depth 5)}
 }finally{if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}}
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($results|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
