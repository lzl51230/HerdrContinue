param([Parameter(Mandatory=$true)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$output=Join-Path $BuildDirectory ('results-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output | Out-Null
$task='HerdrContinue-UI-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$body=@'
param($Root,$Output)
$ErrorActionPreference='Stop'
$app=Join-Path $Root 'windows-debug/HerdrAutoContinue.exe'
$capture=Start-Job -ArgumentList $Output -ScriptBlock {
 param($Folder)
 Add-Type -AssemblyName System.Drawing
 Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DesktopCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w,out uint id);
}
"@
 [DesktopCapture]::SetProcessDPIAware()|Out-Null
 for($i=0;$i -lt 60;$i++) {
  Start-Sleep -Seconds 2
  $w=[DesktopCapture]::GetForegroundWindow();$ownerId=[uint32]0
  [DesktopCapture]::GetWindowThreadProcessId($w,[ref]$ownerId)|Out-Null
  if((Get-Process -Id $ownerId -ErrorAction SilentlyContinue).ProcessName -ne 'HerdrAutoContinue'){continue}
  $r=New-Object DesktopCapture+Rect
  [DesktopCapture]::GetWindowRect($w,[ref]$r)|Out-Null
  if($r.Right -le $r.Left -or $r.Bottom -le $r.Top){continue}
  $bmp=[Drawing.Bitmap]::new($r.Right-$r.Left,$r.Bottom-$r.Top)
  $g=[Drawing.Graphics]::FromImage($bmp)
  try {$g.CopyFromScreen($r.Left,$r.Top,0,0,$bmp.Size);$bmp.Save((Join-Path $Folder "desktop-$i.png"))}
  finally {$g.Dispose();$bmp.Dispose()}
 }
}
try {
foreach($suite in @('themes','features','cooldown','regions')) {
 $suiteOutput=Join-Path $Output "watchdog-qa-$suite"
 $extra=@(); if($suite -eq 'regions'){$extra=@('-UiOnly')}
 $ErrorActionPreference='Continue'
 & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root "scripts/qa-$suite-windows.ps1") -AppPath $app -OutputDirectory $suiteOutput @extra *> (Join-Path $Output "$suite.log")
 $ErrorActionPreference='Stop'
 if($LASTEXITCODE -ne 0){throw "Suite failed: $suite"}
 $report=Get-Content (Join-Path $suiteOutput 'result.json') -Raw -Encoding UTF8
 if($report -match '"success"\s*:\s*false'){throw "Suite failed: $suite - $report"}
}
} catch { $_ | Out-String | Set-Content (Join-Path $Output "failure.txt"); throw } finally { Stop-Job $capture; Remove-Job $capture }
'@
$runner=Join-Path $output 'run.ps1'
[IO.File]::WriteAllText($runner,$body,[Text.UTF8Encoding]::new($true))
$command="& '$runner' -Root '$BuildDirectory' -Output '$output' *> '$output/task.log'"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
$principal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
try {
 Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
 Start-ScheduledTask -TaskName $task
 Start-Sleep -Seconds 3
 $timer=[Diagnostics.Stopwatch]::StartNew()
 while((Get-ScheduledTask $task).State -eq 'Running') {
  if($timer.Elapsed.TotalSeconds -gt 300){Stop-ScheduledTask $task;throw 'UI QA timed out'}
  Start-Sleep -Seconds 2
 }
 Get-Content "$output/task.log" -Encoding UTF8
 if((Get-ScheduledTaskInfo $task).LastTaskResult -ne 0){throw "UI QA failed: $output"}
 Write-Output "QA artifacts: $output"
} finally { Unregister-ScheduledTask $task -Confirm:$false -ErrorAction SilentlyContinue }
