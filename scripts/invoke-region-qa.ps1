param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$output=Join-Path $BuildDirectory ('repo/dist/qa/watchdog-qa-regions-windows-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output|Out-Null
$portable=Join-Path $BuildDirectory ('qa/地区 portable-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
Expand-Archive -Path (Join-Path $BuildDirectory 'repo/dist/HerdrAutoContinue-win-x64.zip') -DestinationPath $portable
$task='HerdrContinue-RegionQA-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$command='& "'+(Join-Path $BuildDirectory 'repo/scripts/qa-regions-windows.ps1')+'" -AppPath "'+(Join-Path $BuildDirectory 'qa/regions-app/HerdrAutoContinue.exe')+'" -OutputDirectory "'+$output+'" -ReleaseAppPath "'+(Join-Path $portable 'HerdrAutoContinue.exe')+'" *> "'+(Join-Path $output 'task.log')+'"'
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command)))
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
try{
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal|Out-Null;Start-ScheduledTask -TaskName $task
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path (Join-Path $output 'result.json'))){
        if($watch.Elapsed.TotalSeconds -gt 100){throw "Region QA timed out: $output"}
        if($watch.Elapsed.TotalSeconds -gt 3 -and (Get-ScheduledTask -TaskName $task).State -ne 'Running'){
            Get-Content (Join-Path $output 'task.log') -Raw;throw 'Region QA exited without results'
        }
        Start-Sleep -Seconds 1
    }
    Get-Content (Join-Path $output 'result.json') -Raw -Encoding UTF8;Write-Output "QA artifacts: $output"
    if(-not (Get-Content (Join-Path $output 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json).success){throw 'Region GUI QA failed'}
}finally{Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue}
