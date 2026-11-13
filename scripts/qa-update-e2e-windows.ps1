param([string]$Case='all', [switch]$Prepare, [string]$Suite='update-e2e', [string]$Target='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$output=Join-Path $root "dist/qa/$Suite"
New-Item -ItemType Directory -Force $output | Out-Null
if($Prepare){
 & python scripts/qa-update-e2e.py --prepare --base-app dist/HerdrAutoContinue-win-x64 --output $output --dotnet "$env:USERPROFILE/.dotnet/dotnet.exe" *> "$output/prepare.log"
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
}
$cases=if($Case -eq 'all'){@('success-stopped','success-running','corrupt','startup-failure')}else{@($Case)}
foreach($scenario in $cases){
 $taskName='HerdrUpdateE2E-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
 $python=(Get-Command python).Source
 $command="& '$python' '$root/scripts/qa-update-e2e.py' --output '$output' --case '$scenario' --timeout 400"
 if($Target){$command+=" --target '$Target'"}
 $command+=" *> '$output/$scenario.log'"
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
 $action=New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -EncodedCommand $encoded"
 $principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
 try{
  Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
  Start-ScheduledTask -TaskName $taskName
  $deadline=[DateTime]::UtcNow.AddSeconds(420)
  while(!(Test-Path "$output/cases/$scenario/result.json")){
   if([DateTime]::UtcNow -gt $deadline){throw 'End-to-end timeout'}
   Start-Sleep -Milliseconds 500
  }
  $result=Get-Content "$output/cases/$scenario/result.json" -Raw
  Write-Output "$scenario : $result"
  if(!($result | ConvertFrom-Json).success){throw 'End-to-end failed'}
 }finally{Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue}
}
