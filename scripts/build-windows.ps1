param([string]$DotnetPath = 'dotnet', [ValidateSet('Release','Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $output = Join-Path $root 'dist/HerdrAutoContinue-win-x64'
    if (Test-Path $output) { Remove-Item -Recurse -Force $output }
    foreach ($project in @('src/HerdrAutoContinue', 'src/Watchdog.Probe')) {
        & $DotnetPath publish $project -c $Configuration -r win-x64 --self-contained true -p:PublishTrimmed=false -p:DebugType=None -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
    }
    $zip = Join-Path $root 'dist/HerdrAutoContinue-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$output/*" -DestinationPath $zip
    Get-FileHash $zip -Algorithm SHA256 | Format-List
    Write-Output "Built: $zip"
} finally { Pop-Location }
