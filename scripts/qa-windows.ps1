param(
    [Parameter(Mandatory=$true)][string]$AppPath,
    [Parameter(Mandatory=$true)][string]$ReleaseAppPath,
    [Parameter(Mandatory=$true)][string]$FakeAgentPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class QaNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr window,int x,int y,int width,int height,bool repaint);
}
'@
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$session = 'watchdog-qa-' + [Guid]::NewGuid().ToString('N').Substring(0,12)
$receipt = Join-Path $OutputDirectory 'receipt.jsonl'
$result = [ordered]@{ session=$session; desktopSessionId=(Get-Process -Id $PID).SessionId }
$started = [Collections.Generic.List[Diagnostics.Process]]::new()
$server = $null
$created = $false
function Wait-Until([scriptblock]$Condition, [int]$Seconds=15) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        try { if (& $Condition) { return } } catch [System.Management.Automation.MethodInvocationException] { }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $Condition"
}
function Get-Window($Process) {
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$Process.Id)
    [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
}
function Find-Control($Window,[string]$Id) {
    $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)
    $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
}
function Invoke-Control($Window,[string]$Id) {
    $control=Find-Control $Window $Id
    if (-not $control) {throw "Missing control: $Id"}
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Capture($Window,[string]$Name) {
    [QaNative]::SetForegroundWindow([IntPtr]$Window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 350
    $r=$Window.Current.BoundingRectangle
    $bitmap=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try { $graphics.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bitmap.Size);$bitmap.Save((Join-Path $OutputDirectory $Name),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose();$bitmap.Dispose() }
}
function Launch([string]$Path) {
    $p=Start-Process -FilePath $Path -PassThru
    $started.Add($p)
    Wait-Until { (Get-Window $p) -ne $null }
    return $p
}
function Close-Window($Window,$Process) {
    Invoke-Control $Window 'app-exit'
    if(-not $Process.WaitForExit(8000)){throw 'Explicit quit did not terminate application'}
    if($Process.ExitCode -ne 0){throw "Application exited with $($Process.ExitCode)"}
}
try {
    if($result.desktopSessionId -eq 0){throw 'GUI QA must run in the logged-in desktop session'}
    foreach($name in @('HERDR_SESSION','HERDR_SOCKET_PATH','HERDR_PANE_ID','HERDR_WORKSPACE_ID','HERDR_TAB_ID')){[Environment]::SetEnvironmentVariable($name,$null,'Process')}
    $herdr=(Get-Command herdr.exe).Source
    $server=Start-Process -FilePath $herdr -ArgumentList @('--session',$session,'server') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputDirectory 'server.stdout') -RedirectStandardError (Join-Path $OutputDirectory 'server.stderr')
    $created=$true
    Wait-Until { ((& $herdr session list --json | ConvertFrom-Json).sessions | Where-Object {$_.name -eq $session -and $_.running}) -ne $null }
    $workspace=& $herdr --session $session workspace create --label 'Watchdog QA' --cwd $OutputDirectory --no-focus | ConvertFrom-Json
    if($LASTEXITCODE -ne 0){throw 'QA workspace creation failed'}
    $pane=$workspace.result.root_pane.pane_id
    Start-Sleep -Milliseconds 500
    # Works in either PowerShell or cmd; paths are absolute and are not supplied by a shell user.
    $command='"' + $FakeAgentPath + '" "' + $receipt + '"'
    $shell=(& $herdr --session $session pane process-info --pane $pane | ConvertFrom-Json).result.process_info
    if(($shell.foreground_processes | Where-Object {$_.name -match 'powershell|pwsh'})){$command='& '+$command}
    & $herdr --session $session pane run $pane $command | Out-Null
    if($LASTEXITCODE -ne 0){throw 'QA agent launch failed'}
    Wait-Until { ((& $herdr --session $session agent list | ConvertFrom-Json).result.agents | Where-Object {$_.agent -eq 'codex' -and $_.agent_status -eq 'idle'}) -ne $null }
    $env:WATCHDOG_QA_SESSION=$session
    $app=Launch $AppPath;$window=Get-Window $app
    Wait-Until {(Find-Control $window 'service-toggle').Current.IsEnabled}
    Wait-Until { (Find-Control $window 'service-toggle').Current.Name -match '启动服务' -and (Find-Control $window 'service-toggle').Current.IsEnabled }
    $result.initialStopped=$true;Capture $window '01-stopped.png'
    Invoke-Control $window 'service-toggle'
    Wait-Until { (Find-Control $window 'service-state').Current.Name -match '1 个冷却中' }
    $result.cooldownVisible=$true;Capture $window '02-cooldown.png'
    [QaNative]::MoveWindow([IntPtr]$window.Current.NativeWindowHandle,100,100,900,720,$true)|Out-Null
    Capture $window '03-minimum-size.png'
    Close-Window $window $app
    Start-Sleep -Seconds 11
    if(Test-Path $receipt){throw 'Explicit quit failed to cancel pending submission'}
    $result.exitCancelledPendingInput=$true
    $app=Launch $AppPath;$window=Get-Window $app
    Wait-Until {(Find-Control $window 'service-toggle').Current.IsEnabled}
    [QaNative]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)|Out-Null
    (Find-Control $window 'service-toggle').SetFocus()
    $watch=[Diagnostics.Stopwatch]::StartNew();[Windows.Forms.SendKeys]::SendWait('^{ENTER}')
    Wait-Until {(Find-Control $window 'service-toggle').Current.Name -match '停止服务'}
    $result.keyboardShortcutWorked=$true
    Wait-Until {Test-Path $receipt} 25
    $result.secondsFromStart=$watch.Elapsed.TotalSeconds
    $received=Get-Content $receipt -First 1 -Encoding UTF8 | ConvertFrom-Json
    if(-not $received.receivedContinueAndNewline -or $result.secondsFromStart -lt 10){throw 'Invalid continuation or cooldown'}
    $logDirectory=Join-Path ([IO.Path]::GetTempPath()) $session
    Wait-Until { (Get-Content (Join-Path $logDirectory 'events.jsonl') -Encoding UTF8 | ForEach-Object {$_ | ConvertFrom-Json} | Where-Object {$_.message -match 'Herdr 已确认提交'}) -ne $null }
    Start-Sleep -Seconds 3
    if(@(Get-Content $receipt).Count -ne 1){throw 'Duplicate input received'}
    $result.continueAndEnterOnce=$true;Capture $window '04-resumed.png'
    Invoke-Control $window 'open-log';Start-Sleep -Milliseconds 500
    $logDirectory=Join-Path ([IO.Path]::GetTempPath()) $session
    $shell=New-Object -ComObject Shell.Application
    $logWindows=@($shell.Windows() | Where-Object {try{$_.Document.Folder.Self.Path -eq $logDirectory}catch{$false}})
    if($logWindows.Count -eq 0){throw 'Open log did not reveal the QA log directory'}
    $result.openLogRevealedFile=$true
    foreach($logWindow in $logWindows){$logWindow.Quit()}
    Invoke-Control $window 'service-toggle'
    Wait-Until {(Find-Control $window 'service-toggle').Current.Name -match '启动服务'}
    $result.stopButtonWorked=$true
    Invoke-Control $window 'service-toggle';Start-Sleep -Milliseconds 500;Close-Window $window $app
    $logPath=Join-Path $logDirectory 'events.jsonl'
    $last=Get-Content $logPath -Tail 1 -Encoding UTF8 | ConvertFrom-Json
    if($last.message -notmatch '取消全部待发送任务'){throw 'Quit did not stop monitor'}
    $result.exitStoppedService=$true
    $env:WATCHDOG_QA_SESSION=$null
    $env:DOTNET_ROOT=Join-Path $OutputDirectory 'missing-runtime'
    $release=Launch $ReleaseAppPath;$window=Get-Window $release
    Wait-Until {(Find-Control $window 'service-toggle').Current.Name -match '启动服务'}
    Capture $window '05-release-portable.png';Close-Window $window $release
    $result.selfContainedReleaseLaunched=$true
    $result.success=$true
} catch { $result.success=$false;$result.error=$_.ToString();$result.stack=$_.ScriptStackTrace }
finally {
    foreach($process in $started){try{if(-not $process.HasExited){$process.CloseMainWindow()|Out-Null;if(-not $process.WaitForExit(5000)){$process.Kill()}}}catch{}}
    if($created){try{& $herdr session stop $session --json | Out-Null;& $herdr session delete $session --json | Out-Null}catch{}}
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
