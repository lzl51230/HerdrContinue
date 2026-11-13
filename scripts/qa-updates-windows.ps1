param([string]$DotnetPath = "$env:USERPROFILE\.dotnet\dotnet.exe")
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$env:DOTNET_CLI_UI_LANGUAGE='en'
$env:DOTNET_CLI_CONTEXT_ANSI_PASS_THRU='false'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$output=Join-Path $root 'dist/qa/update-windows'
New-Item -ItemType Directory -Force $output | Out-Null
$ErrorActionPreference='Continue'
& $DotnetPath test HerdrContinue.slnx -c Release --nologo --tl:off *> "$output/tests.log"
$ErrorActionPreference='Stop'
if($LASTEXITCODE -ne 0){throw 'Tests failed'}
& "$root/scripts/build-windows.ps1" -DotnetPath $DotnetPath *> "$output/build.log"
& $DotnetPath publish src/HerdrAutoContinue -c Debug -r win-x64 --self-contained true -o "$output/debug" --tl:off *> "$output/debug.log"
if($LASTEXITCODE -ne 0){throw 'Debug build failed'}
& python "$root/scripts/qa-update-worker.py" --app "$root/dist/HerdrAutoContinue-win-x64" --output "$output/worker" *> "$output/worker.log"
if($LASTEXITCODE -ne 0){throw 'Worker QA failed'}
$taskName='HerdrContinue-Update-QA-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$qaSettings=Join-Path $output 'watchdog-qa-updates'
$command="`$env:WATCHDOG_QA_SESSION='watchdog-qa-updates'; `$env:WATCHDOG_QA_SETTINGS_DIR='$qaSettings'; `$env:WATCHDOG_QA_SCENARIO='updates'; `$env:WATCHDOG_QA_AUTOMATION_DIR='$output/ui'; & '$output/debug/HerdrAutoContinue.exe'"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoLogo -NoProfile -EncodedCommand $encoded"
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
try {
 Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
 Start-ScheduledTask -TaskName $taskName
 $deadline=[DateTime]::UtcNow.AddSeconds(90)
 while(!(Test-Path "$output/ui/result.json")) {if([DateTime]::UtcNow -gt $deadline){throw 'GUI QA timed out'}; Start-Sleep -Milliseconds 250}
 if(!(Get-Content "$output/ui/result.json" -Raw | ConvertFrom-Json).success){throw 'GUI QA failed'}
 Get-Content "$output/ui/result.json"
 Get-Content "$output/worker/result.json"
 Get-Content "$output/tests.log" -Tail 4
} finally {Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue}
