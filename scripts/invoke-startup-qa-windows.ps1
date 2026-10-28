param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$SkipSystemLists)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
if(Test-Path (Join-Path $OutputDirectory 'result.json')){throw 'Use a new output directory for each QA run'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$token=[Guid]::NewGuid().ToString('N').Substring(0,8)
$main='HerdrContinue-StartupQA-'+$token
$verifier='HerdrContinue-StartupListQA-'+$token
$user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
$mainPrincipal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$verifierPrincipal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'qa-startup-windows.ps1')+'" -AppPath "'+$AppPath+'" -OutputDirectory "'+$OutputDirectory+'"'
if(-not $SkipSystemLists){$arguments+=' -VerifySystemLists'}
$action=New-ScheduledTaskAction -Execute powershell.exe -Argument $arguments
try{
 Register-ScheduledTask -TaskName $main -Action $action -Principal $mainPrincipal|Out-Null
 Start-ScheduledTask $main
 $watch=[Diagnostics.Stopwatch]::StartNew();$handled=@{}
 while(-not (Test-Path (Join-Path $OutputDirectory 'result.json'))){
  if($watch.Elapsed.TotalSeconds -gt 300){throw 'Startup QA timed out'}
  if($watch.Elapsed.TotalSeconds -gt 5 -and (Get-ScheduledTask $main).State -ne 'Running' -and -not (Test-Path (Join-Path $OutputDirectory 'result.json'))){throw 'Startup QA exited without a result'}
  foreach($stage in @('enabled','removed')){
   $request=Join-Path $OutputDirectory ($stage+'-list-request.json')
   $response=Join-Path $OutputDirectory ($stage+'-list-result.json')
   if((Test-Path $request) -and -not $handled.ContainsKey($stage)){
    $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'qa-startup-list-windows.ps1')+'" -RequestFile "'+$request+'" -ResultFile "'+$response+'"'
    $verifyAction=New-ScheduledTaskAction -Execute powershell.exe -Argument $args
    Register-ScheduledTask -TaskName $verifier -Action $verifyAction -Principal $verifierPrincipal -Force|Out-Null
    Start-ScheduledTask $verifier;$handled[$stage]=$true
   }
  }
  Start-Sleep -Milliseconds 300
 }
 $result=Get-Content (Join-Path $OutputDirectory 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $result|ConvertTo-Json -Depth 9
 if(-not $result.success){throw 'Startup QA failed'}
}finally{
 foreach($task in @($main,$verifier)){Stop-ScheduledTask $task -ErrorAction SilentlyContinue;Unregister-ScheduledTask $task -Confirm:$false -ErrorAction SilentlyContinue}
}
