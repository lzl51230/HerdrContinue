param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$repo=Join-Path $BuildDirectory 'repo'
$output=Join-Path $repo ('dist/qa/windows-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output | Out-Null
$portable=Join-Path $BuildDirectory ('qa/便携版 extracted-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
Expand-Archive -Path (Join-Path $repo 'dist/HerdrAutoContinue-win-x64.zip') -DestinationPath $portable
$taskName='HerdrContinue-QA-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$qaCommand='& "'+(Join-Path $repo 'scripts/qa-windows.ps1')+'" -AppPath "'+(Join-Path $BuildDirectory 'qa/app/HerdrAutoContinue.exe')+'" -ReleaseAppPath "'+(Join-Path $portable 'HerdrAutoContinue.exe')+'" -FakeAgentPath "'+(Join-Path $BuildDirectory 'qa/fake-agent/codex.exe')+'" -OutputDirectory "'+$output+'" *> "'+(Join-Path $output 'task.log')+'"'
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($qaCommand))
$arguments='-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand '+$encoded
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $output 'result.json'))){
        if($watch.Elapsed.TotalSeconds -gt 150){throw "QA task timed out; output: $output"}
        if($watch.Elapsed.TotalSeconds -gt 3 -and (Get-ScheduledTask -TaskName $taskName).State -ne 'Running'){
            if(Test-Path (Join-Path $output 'task.log')){Get-Content (Join-Path $output 'task.log') -Raw}
            throw "QA task exited without a result; output: $output"
        }
        Start-Sleep -Seconds 1
    }
    Get-Content (Join-Path $output 'result.json') -Raw -Encoding UTF8
    Write-Output "QA artifacts: $output"
    if(-not (Get-Content (Join-Path $output 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json).success){throw 'Windows GUI QA failed'}
} finally {Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue}
