param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$FakeAgentPath,[switch]$VerifySystemLists)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$bytes=[IO.File]::ReadAllBytes((Join-Path (Split-Path $AppPath) 'HerdrAutoContinue.dll'))
$unicode=[Text.Encoding]::Unicode
if(-not ($unicode.GetString($bytes).Contains('WATCHDOG_QA_SESSION') -or $unicode.GetString($bytes,1,$bytes.Length-1).Contains('WATCHDOG_QA_SESSION'))){throw 'Use an isolated Debug QA build'}
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$token=[Guid]::NewGuid().ToString('N').Substring(0,6)
$scope='watchdog-qa-s'+$token
$appDirectory=Join-Path $env:LOCALAPPDATA ('Temp\watchdog-qa-a'+$token)
$state=Join-Path $env:LOCALAPPDATA ('Temp\watchdog-qa-s'+$token)
New-Item -ItemType Directory -Force $appDirectory,$state,$OutputDirectory|Out-Null
Copy-Item (Join-Path (Split-Path $AppPath) '*') $appDirectory -Recurse
$app=Join-Path $appDirectory 'HerdrAutoContinue.exe'
$hash=[Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($scope))
$name='HerdrAutoContinue-QA-'+([BitConverter]::ToString($hash).Replace('-','').Substring(0,12))
$key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$result=[ordered]@{session=$scope;desktopSessionId=(Get-Process -Id $PID).SessionId;stages=@{}}
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
$server=$null;$herdr=$null
function Verify-Lists([string]$Stage){
 if(-not $VerifySystemLists){return}
 $request=Join-Path $OutputDirectory ($Stage+'-list-request.json')
 $response=Join-Path $OutputDirectory ($Stage+'-list-result.json')
 [IO.File]::WriteAllText($request,(@{stage=$Stage;name=$name;appPath=$app}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(-not (Test-Path $response)){if($watch.Elapsed.TotalSeconds -gt 120){throw 'System list verifier did not respond'};Start-Sleep -Milliseconds 200}
 $verification=Get-Content $response -Raw -Encoding UTF8|ConvertFrom-Json
 if(-not $verification.success){throw ($verification|ConvertTo-Json -Depth 6)}
 $result[$Stage+'SystemLists']=$verification
}
function Wait-Condition([scriptblock]$Condition){
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while($watch.Elapsed.TotalSeconds -lt 15){if(& $Condition){return};Start-Sleep -Milliseconds 100}
 throw 'Herdr fixture did not become ready'
}
function Run-Stage([string]$Scenario,[bool]$Hidden=$false,[bool]$Registered=$false) {
 $folder=Join-Path $state $Scenario
 $env:WATCHDOG_QA_SESSION=$scope
 $env:WATCHDOG_QA_SETTINGS_DIR=$state
 $env:WATCHDOG_QA_SCENARIO=$Scenario
 $env:WATCHDOG_QA_AUTOMATION_DIR=$folder
 $info=[Diagnostics.ProcessStartInfo]::new($app)
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true
 if($Hidden){$info.Arguments='--autostart'}
 if($Registered) {
  $line=Get-ItemPropertyValue -Path $key -Name $name
  $prefix='"'+$app+'"'
  if(-not $line.StartsWith($prefix+' --autostart')){throw 'Unexpected registered startup command'}
  if($line.Length -gt 260){throw 'Registered command exceeds Windows limit'}
  $info.Arguments=$line.Substring($prefix.Length).TrimStart()
  $folder=Join-Path $state 'system-login'
  $env:WATCHDOG_QA_SCENARIO=$null;$env:WATCHDOG_QA_AUTOMATION_DIR=$null
 }
 $p=[Diagnostics.Process]::Start($info);$processes.Add($p)
 $watch=[Diagnostics.Stopwatch]::StartNew();$nativeChecked=$false
 while(-not $p.HasExited) {
  if($watch.Elapsed.TotalSeconds -gt 40){throw "Stage timed out: $Scenario"}
  $ready=Join-Path $folder 'background-ready.json'
  if($Hidden -and -not $nativeChecked -and (Test-Path $ready)) {
   $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$p.Id)
   $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
   if($windows.Count -ne 0){throw 'Silent startup created a desktop window'}
   $nativeChecked=$true
  }
  Start-Sleep -Milliseconds 100
 }
 if($p.ExitCode -ne 0){throw "Stage exit code $($p.ExitCode): $Scenario"}
 $report=Get-Content (Join-Path $folder 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
 if(-not $report.success){throw ($report|ConvertTo-Json -Depth 6)}
 if($Hidden -and -not $nativeChecked){throw 'Missing native silent-start observation'}
 $result.stages[$Scenario]=$report
}
try {
 if($result.desktopSessionId -eq 0){throw 'Run on the logged-in desktop'}
 if($FakeAgentPath) {
  if(Get-Process HerdrAutoContinue -ErrorAction SilentlyContinue|Where-Object {$_.Path -notmatch 'watchdog-qa-'}){throw 'An existing production monitor could interfere with the agent fixture'}
  foreach($variable in @('HERDR_SESSION','HERDR_SOCKET_PATH','HERDR_PANE_ID','HERDR_WORKSPACE_ID','HERDR_TAB_ID')){[Environment]::SetEnvironmentVariable($variable,$null,'Process')}
  $herdr=(Get-Command herdr.exe).Source
  $server=Start-Process $herdr -ArgumentList @('--session',$scope,'server') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $state 'server.stdout') -RedirectStandardError (Join-Path $state 'server.stderr')
  Wait-Condition { ((& $herdr session list --json|ConvertFrom-Json).sessions|Where-Object {$_.name -eq $scope -and $_.running}) -ne $null }
  $workspace=& $herdr --session $scope workspace create --label 'Startup QA' --cwd $state --no-focus|ConvertFrom-Json
  $pane=$workspace.result.root_pane.pane_id
  Start-Sleep -Milliseconds 500
  $actorDirectory=Join-Path $appDirectory 'actor';New-Item -ItemType Directory -Force $actorDirectory|Out-Null
  Copy-Item (Join-Path (Split-Path $FakeAgentPath) '*') $actorDirectory -Recurse
  $actor=Join-Path $actorDirectory 'codex.exe'
  $line='"'+$actor+'" "'+(Join-Path $state 'receipt.jsonl')+'"'
  $shell=(& $herdr --session $scope pane process-info --pane $pane|ConvertFrom-Json).result.process_info
  if($shell.foreground_processes|Where-Object {$_.name -match 'powershell|pwsh'}){$line='& '+$line}
  & $herdr --session $scope pane run $pane $line|Out-Null
  Wait-Condition { ((& $herdr --session $scope agent list|ConvertFrom-Json).result.agents|Where-Object {$_.agent -eq 'codex' -and $_.agent_status -eq 'idle'}) -ne $null }
  [IO.File]::WriteAllText((Join-Path $state 'expect-agent'),'')
 }
 Run-Stage 'startup-enable'
 Verify-Lists 'enabled'
 Run-Stage 'startup-system' $true $true
 if($FakeAgentPath){
  $receipt=@(Get-Content (Join-Path $state 'receipt.jsonl') -Encoding UTF8)
  $record=$receipt[0]|ConvertFrom-Json
  if($receipt.Count -ne 1 -or -not $record.receivedContinueAndNewline -or $record.elapsed -lt 10){throw 'Invalid automatic continuation/cooldown'}
  $result.singleAutomaticContinuation=$true;$result.actorElapsedSeconds=$record.elapsed
 }
 Run-Stage 'startup-manual'
 Run-Stage 'startup-os-shutdown'
 Run-Stage 'startup-disable'
 if(Get-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue){throw 'Startup entry still exists after disabling'}
 Verify-Lists 'removed'
 Run-Stage 'startup-off'
 Run-Stage 'startup-off-hidden' $true
 $result.success=$true
} catch {$result.success=$false;$result.error=$_.ToString();$result.stack=$_.ScriptStackTrace}
finally {
 foreach($p in $processes){if(-not $p.HasExited){$p.Kill();$p.WaitForExit()}}
 if($server){try{& $herdr session stop $scope --json|Out-Null;& $herdr session delete $scope --json|Out-Null}catch{};if(-not $server.HasExited){$server.Kill()}}
 Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue
 Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run' -Name $name -ErrorAction SilentlyContinue
 if(Test-Path $state){Copy-Item $state (Join-Path $OutputDirectory 'state') -Recurse -Force}
 [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($result|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
 Remove-Item $appDirectory,$state -Recurse -Force -ErrorAction SilentlyContinue
}
