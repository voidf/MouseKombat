param([Parameter(Mandatory=$true)][string]$Actions)
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class KritaMouse {
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
}
'@
Add-Type -AssemblyName System.Windows.Forms
$items = $Actions | ConvertFrom-Json
foreach($a in $items){
 switch($a.kind){
 'click' { [KritaMouse]::SetCursorPos($a.x,$a.y)|Out-Null; Start-Sleep -Milliseconds 50; [KritaMouse]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [KritaMouse]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
 'right' { [KritaMouse]::SetCursorPos($a.x,$a.y)|Out-Null; [KritaMouse]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [KritaMouse]::mouse_event(16,0,0,0,[UIntPtr]::Zero) }
 'drag' { $p=$a.points; [KritaMouse]::SetCursorPos($p[0][0],$p[0][1])|Out-Null; [KritaMouse]::mouse_event(2,0,0,0,[UIntPtr]::Zero); foreach($v in $p){[KritaMouse]::SetCursorPos($v[0],$v[1])|Out-Null; Start-Sleep -Milliseconds 35}; [KritaMouse]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
 'key' { [System.Windows.Forms.SendKeys]::SendWait($a.text) }
 }
 Start-Sleep -Milliseconds 180
}
Write-Output "Executed $($items.Count) GUI actions."
