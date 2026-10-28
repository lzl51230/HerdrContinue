param([Parameter(Mandatory=$true)][string]$AppPath, [Parameter(Mandatory=$true)][string]$OutputDirectory, [string]$ReleaseAppPath)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$result=[ordered]@{desktopSessionId=(Get-Process -Id $PID).SessionId}
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
function Wait-Until([scriptblock]$Condition,[int]$Seconds=15){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt $Seconds){try{if(& $Condition){return}}catch [Management.Automation.MethodInvocationException]{};Start-Sleep -Milliseconds 100}
    throw "Timed out: $Condition"
}
function Get-Window($Process,[string]$Title='Herdr Auto Continue'){
    if($Title -ne 'Herdr Auto Continue'){
        $id=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'region-settings-window')
        $own=[Windows.Automation.AndCondition]::new($id,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$Process.Id))
        $dialog=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$own)
        if($dialog){return $dialog}
        $main=Get-Window $Process
        if($main){return $main.FindFirst([Windows.Automation.TreeScope]::Descendants,$id)}
        return $null
    }
    $condition=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$Process.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Title))
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
}
function Find-Control($Window,[string]$Id){
    $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Invoke-Control($Window,[string]$Id){
    $control=Find-Control $Window $Id;if(-not $control){throw "Missing control: $Id"}
    try{$control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}catch [Management.Automation.MethodInvocationException]{}
}
function Set-Region($Window,[string]$Id,[bool]$Selected){
    $pattern=(Find-Control $Window ('region-check-'+$Id)).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $expected=if($Selected){[Windows.Automation.ToggleState]::On}else{[Windows.Automation.ToggleState]::Off}
    if($pattern.Current.ToggleState -ne $expected){try{$pattern.Toggle()}catch [Management.Automation.MethodInvocationException]{}}
    Wait-Until{(Find-Control $Window ('region-check-'+$Id)).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq $expected}
}
function Capture($Window,[string]$Name){
    Start-Sleep -Milliseconds 300;$r=$Window.Current.BoundingRectangle
    $b=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height);$g=[Drawing.Graphics]::FromImage($b)
    try{$g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$b.Size);$b.Save((Join-Path $OutputDirectory $Name),[Drawing.Imaging.ImageFormat]::Png)}finally{$g.Dispose();$b.Dispose()}
}
function Launch([string]$Path=$AppPath){
    $p=Start-Process -FilePath $Path -PassThru;$processes.Add($p)
    Wait-Until{(Get-Window $p) -ne $null}
    Wait-Until{(Find-Control (Get-Window $p) 'region-settings').Current.IsEnabled};return $p
}
function Open-Settings($Process){
    Invoke-Control (Get-Window $Process) 'region-settings'
    try{Wait-Until{(Get-Window $Process '地区设置') -ne $null}}catch{
        $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$Process.Id)
        [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)|ForEach-Object{$_.Current|Select-Object Name,AutomationId,NativeWindowHandle}|ConvertTo-Json|Set-Content (Join-Path $OutputDirectory 'window-diagnostic.json')
        throw
    }
    return Get-Window $Process '地区设置'
}
function Close-App($Process){Invoke-Control (Get-Window $Process) 'app-exit';if(-not $Process.WaitForExit(8000)){throw 'App did not stop after explicit quit'};if($Process.ExitCode -ne 0){throw "Exit code: $($Process.ExitCode)"}}
try{
    if($result.desktopSessionId -eq 0){throw 'QA must run on the interactive desktop'}
    $env:WATCHDOG_QA_SESSION='watchdog-qa-regions-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $env:WATCHDOG_QA_SETTINGS_DIR=$OutputDirectory;$env:WATCHDOG_QA_REGIONS='recovery'
    $app=Launch;$main=Get-Window $app
    Wait-Until{(Find-Control $main 'service-toggle').Current.Name -match '停止服务' -and (Find-Control $main 'service-toggle').Current.IsEnabled}
    $result.initialRunning=$true
    Invoke-Control $main 'service-toggle'
    Wait-Until{(Find-Control $main 'service-toggle').Current.Name -match '启动服务' -and (Find-Control $main 'service-toggle').Current.IsEnabled}
    $dialog=Open-Settings $app
    Wait-Until{(Find-Control $dialog 'region-catalogue-summary').Current.Name -match '4 个地区'}
    $result.fourRegionsDiscovered=$true
    Set-Region $dialog 'HK' $true;Set-Region $dialog 'SG' $true;Invoke-Control $dialog 'region-up-HK'
    Wait-Until{-not (Find-Control $dialog 'region-up-HK').Current.IsEnabled}
    Capture $dialog '01-region-draft.png';Invoke-Control $dialog 'region-cancel';Wait-Until{(Get-Window $app '地区设置') -eq $null}
    if(Test-Path (Join-Path $OutputDirectory 'settings.json')){throw 'Cancel persisted edits'}
    if((Find-Control $main 'region-order').Current.Name -ne '恢复优先级：日本'){throw 'Cancel changed active policy'}
    $result.cancelKeptOriginal=$true
    $dialog=Open-Settings $app;Set-Region $dialog 'HK' $true;Set-Region $dialog 'SG' $true;Invoke-Control $dialog 'region-up-HK'
    Wait-Until{-not (Find-Control $dialog 'region-up-HK').Current.IsEnabled};Invoke-Control $dialog 'region-apply'
    Wait-Until{(Get-Window $app '地区设置') -eq $null}
    $settings=Get-Content (Join-Path $OutputDirectory 'settings.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if(($settings.priorityRegionIds -join ',') -ne 'HK,JP,SG'){throw 'Unexpected saved order'}
    $result.savedOrder=$settings.priorityRegionIds
    Invoke-Control $main 'service-toggle'
    Wait-Until{(Find-Control $main 'proxy-status').Current.Name -eq '代理正常'} 25
    if((Find-Control $main 'selected-proxy-node').Current.Name -notmatch '验收香港01'){throw 'Wrong region/node selected'}
    $trace=Get-ChildItem $OutputDirectory -Filter 'clash-trace-*.json'|Sort-Object LastWriteTime -Descending|Select-Object -First 1|Get-Content -Raw -Encoding UTF8|ConvertFrom-Json
    if(@($trace.selected).Count -ne 1 -or $trace.selected[0] -ne '验收香港01'){throw 'Unexpected selections'}
    if(@($trace.tested).Count -ne 6 -or @($trace.tested|Where-Object {$_ -match '日本'}).Count -ne 4){throw 'Wrong region fallback sequence'}
    if(@($trace.tested|Select-Object -Skip 4|Where-Object {$_ -notmatch '香港'}).Count){throw 'Retested failed region or skipped priority'}
    $result.currentRegionThenTopPriority=$true;$result.fastestHongKongNodeSelected=$true
    Capture $main '02-regions-recovered.png';Close-App $app
    $app=Launch;$main=Get-Window $app;$dialog=Open-Settings $app
    foreach($id in @('HK','JP','SG')){
        if((Find-Control $dialog ('region-check-'+$id)).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::On){throw "Region checkbox was not restored: $id"}
    }
    if((Find-Control $dialog 'region-up-HK').Current.IsEnabled -or (Find-Control $dialog 'region-down-SG').Current.IsEnabled){throw 'Priority was not restored'}
    if((Find-Control $dialog 'region-check-US').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off){throw 'Unselected region became enabled'}
    $result.restartRestoredCheckboxesAndOrder=$true;Capture $dialog '03-regions-restored.png'
    Invoke-Control $dialog 'region-cancel';Wait-Until{(Get-Window $app '地区设置') -eq $null};Close-App $app
    if($ReleaseAppPath){
        $env:WATCHDOG_QA_SESSION=$null;$env:WATCHDOG_QA_SETTINGS_DIR=$null;$env:WATCHDOG_QA_REGIONS=$null
        $settingsPath=Join-Path $env:LOCALAPPDATA 'HerdrAutoContinue/settings.json'
        $before=if(Test-Path $settingsPath){(Get-FileHash $settingsPath -Algorithm SHA256).Hash}else{'missing'}
        $env:DOTNET_ROOT=Join-Path $OutputDirectory 'missing-runtime'
        $app=Launch $ReleaseAppPath;$main=Get-Window $app;$dialog=Open-Settings $app
        Wait-Until{(Find-Control $dialog 'region-catalogue-summary').Current.Name -match '发现 [1-9][0-9]* 个地区'}
        $result.realCatalogue=(Find-Control $dialog 'region-catalogue-summary').Current.Name
        Capture $dialog '04-release-real-regions.png';Invoke-Control $dialog 'region-cancel'
        Wait-Until{(Get-Window $app '地区设置') -eq $null};Close-App $app
        $after=if(Test-Path $settingsPath){(Get-FileHash $settingsPath -Algorithm SHA256).Hash}else{'missing'}
        if($before -ne $after){throw 'Read-only Release check modified user settings'}
        $result.portableReleaseAndReadOnlyCatalogue=$true
    }
    $result.success=$true
}catch{$result.success=$false;$result.error=$_.ToString();$result.stack=$_.ScriptStackTrace}
finally{
    foreach($p in $processes){try{if(-not $p.HasExited){$p.CloseMainWindow()|Out-Null;if(-not $p.WaitForExit(8000)){$p.Kill()}}}catch{}}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
