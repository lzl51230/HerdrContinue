param([Parameter(Mandatory=$true)][string]$AppPath,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$SettingsDirectory=$OutputDirectory,[switch]$Reload)
$ErrorActionPreference='Stop'
$bytes=[IO.File]::ReadAllBytes((Join-Path (Split-Path $AppPath) 'HerdrAutoContinue.dll'))
$unicode=[Text.Encoding]::Unicode
if(-not ($unicode.GetString($bytes).Contains('WATCHDOG_QA_SESSION') -or $unicode.GetString($bytes,1,$bytes.Length-1).Contains('WATCHDOG_QA_SESSION'))){throw 'Use an isolated Debug QA build'}
if((Split-Path $OutputDirectory -Leaf) -notlike 'watchdog-qa-*'){throw 'Use an isolated watchdog-qa output directory'}
if(Test-Path (Join-Path $OutputDirectory 'result.json')){throw 'Use a fresh output directory'}
$env:WATCHDOG_QA_SESSION='watchdog-qa-sources-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
if((Split-Path $SettingsDirectory -Leaf) -notlike 'watchdog-qa-*'){throw 'Use isolated QA settings'}
$env:WATCHDOG_QA_SETTINGS_DIR=$SettingsDirectory
$env:WATCHDOG_QA_AUTOMATION_DIR=$OutputDirectory
$env:WATCHDOG_QA_SCENARIO=if($Reload){'sources-reload'}else{'sources'}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$p=Start-Process $AppPath -PassThru
try {
 if(-not $p.WaitForExit(60000)){throw 'Source GUI QA timed out'}
 $r=Get-Content (Join-Path $OutputDirectory 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
 $r|ConvertTo-Json
 if(-not $r.success){throw 'Source GUI QA failed'}
} finally {if(-not $p.HasExited){$p.Kill();$p.WaitForExit()}}
