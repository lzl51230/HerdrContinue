param([Parameter(Mandatory=$true)][string]$RequestFile,[Parameter(Mandatory=$true)][string]$ResultFile)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StartupListNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window,int index);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$request=Get-Content $RequestFile -Raw -Encoding UTF8|ConvertFrom-Json
$out=Split-Path $ResultFile
$report=[ordered]@{stage=$request.stage;appPath=$request.appPath;runValue=$request.name}
function Wait-For([scriptblock]$Check){
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while($watch.Elapsed.TotalSeconds -lt 25){try{$value=& $Check;if($value){return $value}}catch [System.Management.Automation.MethodInvocationException]{};Start-Sleep -Milliseconds 150}
 throw "UI condition timed out: $Check"
}
function Find-Id($Root,[string]$Id){$Root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))}
function Window-For([string]$Process){
 if($Process -eq 'SystemSettings'){
  foreach($candidate in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)){
   if($candidate.Current.Name -in @('设置','Settings') -and (Find-Id $candidate 'SystemSettings_Apps_StartupAppList_Description_DescriptionTextBlock')){return $candidate}
  }
  return
 }
 foreach($p in @(Get-Process $Process -ErrorAction SilentlyContinue)){
  $w=[Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$p.Id))
  if($w){return $w}
 }
}
function Capture($Window,[string]$Name){
 $handle=[IntPtr]$Window.Current.NativeWindowHandle
 $wasTopmost=([StartupListNative]::GetWindowLong($handle,-20) -band 8) -ne 0
 [StartupListNative]::SetWindowPos($handle,[IntPtr](-1),0,0,0,0,0x13)|Out-Null
 Start-Sleep -Milliseconds 250
 $r=$Window.Current.BoundingRectangle;$bitmap=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height);$g=[Drawing.Graphics]::FromImage($bitmap)
 try{$g.CopyFromScreen([int]$r.X,[int]$r.Y,0,0,$bitmap.Size);$bitmap.Save((Join-Path $out $Name),[Drawing.Imaging.ImageFormat]::Png)}finally{
  $g.Dispose();$bitmap.Dispose()
  if(-not $wasTopmost){[StartupListNative]::SetWindowPos($handle,[IntPtr](-2),0,0,0,0,0x13)|Out-Null}
 }
}
function Settings-Toggle($Window){
 $items=$Window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
 $matches=@($items|Where-Object{$_.Current.AutomationId -like '*_ToggleSwitch' -and $_.Current.Name -match '^Herdr'})
 if($matches.Count -gt 1){throw 'Ambiguous Herdr startup items; refusing to toggle another app'}
 if($matches.Count -eq 1){return $matches[0]}
}
function Task-Row($Window){
 $items=$Window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
 $matches=@($items|Where-Object{$_.Current.ControlType -in @([Windows.Automation.ControlType]::TreeItem,[Windows.Automation.ControlType]::DataItem) -and $_.Current.Name -match '^Herdr'})
 if($matches.Count -gt 1){throw 'Ambiguous Herdr rows in Task Manager'}
 if($matches.Count -eq 1){return $matches[0]}
}
function Row-Text($Row){
 @($Row.Current.Name)+@($Row.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name})
}
function Refresh-TaskManager($Window){
 # Task Manager keeps startup metadata for the lifetime of its process on this Windows build.
 $Window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
 Start-Sleep -Milliseconds 500
 Start-Process taskmgr.exe
 $script:manager=Wait-For {Window-For 'Taskmgr'}
 (Wait-For {Find-Id $script:manager 'Startup AppsItem'}).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Start-Sleep -Milliseconds 500
}
try{
 if($request.name -notlike 'HerdrAutoContinue-QA-*' -or $request.appPath -notmatch 'watchdog-qa-' -or -not (Test-Path $request.appPath)){throw 'Only isolated QA startup entries may be checked'}
 $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
 if($request.stage -eq 'enabled'){
  $command=Get-ItemPropertyValue $run -Name $request.name
  if(-not $command.StartsWith('"'+$request.appPath+'" --autostart')){throw 'Startup command does not point at the QA app'}
  $report.command=$command
 }
 # Settings caches its startup list while that page remains open.
 Start-Process 'ms-settings:appsfeatures'
 Start-Sleep -Seconds 2
 Start-Process 'ms-settings:startupapps'
 $settings=Wait-For {Window-For 'SystemSettings'}
 Wait-For {Find-Id $settings 'SystemSettings_Apps_StartupAppList_Description_DescriptionTextBlock'}|Out-Null
 Start-Process taskmgr.exe
 $manager=Wait-For {Window-For 'Taskmgr'}
 $navigation=Wait-For {Find-Id $manager 'Startup AppsItem'}
 $navigation.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Refresh-TaskManager $manager
 if($request.stage -eq 'enabled'){
  $toggle=Wait-For {Settings-Toggle $settings}
  $pattern=$toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  if($pattern.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){throw 'Startup app is listed but disabled'}
  $report.settingsName=$toggle.Current.Name
  Capture $settings 'settings-enabled.png'
  $row=Wait-For {Task-Row $manager};$text=Row-Text $row
  $report.taskManagerEnabled=$text
  if(($text -join ' ') -notmatch '已启用|Enabled'){throw 'Task Manager did not show enabled state'}
  Capture $manager 'task-manager-enabled.png'
  $pattern.Toggle()
  Wait-For {$pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off}|Out-Null
  Refresh-TaskManager $manager
  Wait-For {((Row-Text (Task-Row $manager)) -join ' ') -match '已禁用|Disabled'}|Out-Null
  Capture $settings 'settings-disabled.png';Capture $manager 'task-manager-disabled.png'
  if((Get-ItemPropertyValue $run -Name $request.name) -ne $command){throw 'System disabling unexpectedly changed the registered command'}
  $report.systemDisableVerified=$true
  $pattern.Toggle()
  Wait-For {$pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On}|Out-Null
  Refresh-TaskManager $manager
  Wait-For {((Row-Text (Task-Row $manager)) -join ' ') -match '已启用|Enabled'}|Out-Null
  $report.systemReenableVerified=$true
 }else{
  Start-Sleep -Seconds 2
  if(Get-ItemProperty $run -Name $request.name -ErrorAction SilentlyContinue){throw 'Run item remains after disabling'}
  if((Settings-Toggle $settings) -or (Task-Row $manager)){throw 'Removed startup app remains visible in a system list'}
  Capture $settings 'settings-removed.png';Capture $manager 'task-manager-removed.png'
  $report.removedFromBothLists=$true
 }
 $report.success=$true
}catch{
 $report.success=$false;$report.error=$_.ToString();$report.stack=$_.ScriptStackTrace
 if($settings){
  $report.settingsItems=@($settings.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.AutomationId -like '*ToggleSwitch' -or $_.Current.Name -match 'Herdr|Program'}|ForEach-Object{@{name=$_.Current.Name;id=$_.Current.AutomationId}})
  Capture $settings 'settings-failed.png'
 }
}
finally{[IO.File]::WriteAllText($ResultFile,($report|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))}
