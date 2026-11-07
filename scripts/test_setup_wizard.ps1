$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class WizardSnapshot {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string name);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w,uint msg,IntPtr p,IntPtr l);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int show);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr w,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int cx,int cy,uint flags);
}
"@
$dir=(Resolve-Path 'installer/verification').Path
$target=Join-Path $dir ('fixture-install-'+[guid]::NewGuid().ToString('N'))
$process=Start-Process -FilePath (Join-Path $dir 'wizard-fixture.exe') -ArgumentList ('/DIR="'+$target+'" /LOG="'+(Join-Path $dir 'wizard-interactive.log')+'"') -WindowStyle Hidden -PassThru
try {
 $scope=[Windows.Automation.TreeScope]::Children
 $root=[Windows.Automation.AutomationElement]::RootElement
 $pidCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
 function Get-Wizard {
  $windows=$root.FindAll($scope,$pidCondition)
  foreach($w in $windows) { if ($w.Current.Name -match 'Setup') { return $w } }
  $h=[WizardSnapshot]::FindWindow("TWizardForm","Setup - IDMLite Wizard Test")
  if($h -ne [IntPtr]::Zero){ [void][WizardSnapshot]::SetWindowPos($h,[IntPtr]::Zero,-10000,-10000,0,0,0x15); [void][WizardSnapshot]::ShowWindow($h,4); return [Windows.Automation.AutomationElement]::FromHandle($h) }
 }
 function Find-Button($window,$name){
  $names=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)
  $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$names)
 }
 function Save-Wizard($w,$name){
  $r=$w.Current.BoundingRectangle
  if ($r.Width -lt 100 -or $r.Height -lt 100) { throw 'Wizard has no visible-sized surface' }
  $bitmap=[Drawing.Bitmap]::new([int]$r.Width,[int]$r.Height)
  $g=[Drawing.Graphics]::FromImage($bitmap)
  $hdc=$g.GetHdc()
  try { if(-not [WizardSnapshot]::PrintWindow([IntPtr]$w.Current.NativeWindowHandle,$hdc,2)){ throw 'PrintWindow failed' } } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
  try { $bitmap.Save((Join-Path $dir $name)) } finally { $bitmap.Dispose() }
 }
 $w=$null
 for($i=0;$i -lt 50 -and !$w;$i++){ Start-Sleep -Milliseconds 120; $w=Get-Wizard }
 if(!$w){ throw 'Wizard not found in isolated fixture' }
 [void][WizardSnapshot]::SetWindowPos([IntPtr]$w.Current.NativeWindowHandle,[IntPtr]::Zero,-10000,-10000,0,0,0x15)
 Start-Sleep -Milliseconds 250
 Save-Wizard $w 'setup-welcome.png'
 $done=$false
 for($i=0;$i -lt 55 -and !$done;$i++){
  $w=Get-Wizard
  if(!$w){ Start-Sleep -Milliseconds 150; continue }
  foreach($name in @('Finish','Install','Next')){
   $b=Find-Button $w $name
   if($b -and $b.Current.IsEnabled){
    if($name -eq 'Finish'){ Save-Wizard $w 'setup-finished.png'; $done=$true }
    [void][WizardSnapshot]::PostMessage([IntPtr]$b.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    if($name -eq "Install"){ Start-Sleep -Milliseconds 300; Save-Wizard $w "setup-installing.png" }
    break
   }
  }
  Start-Sleep -Milliseconds 130
 }
 if(!$done){ throw 'Wizard did not complete through native navigation' }
 if(!(Test-Path -LiteralPath (Join-Path $target 'app_icon.ico'))){ throw 'Fixture file was not installed' }
 'PASS: isolated Inno wizard navigated to finish and installed its fixture'
} finally {
 Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "wizard-fixture.tmp" -and $_.CommandLine.Contains($target) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
 if(!$process.HasExited){ Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
}




