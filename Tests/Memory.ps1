param([string]$Executable, [string]$Label, [int]$Trials = 3)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MemoryNative {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder b, int size);
    public static string Titles(int pid) { var titles=new System.Collections.Generic.List<string>(); EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out var owner); if(owner==pid) {var b=new System.Text.StringBuilder(256); GetWindowText(h,b,256); titles.Add(b.ToString());} return true;},IntPtr.Zero); return string.Join(" | ", titles); }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
}
'@
function Sample($process, $stage, $trial) {
    $process.Refresh()
    if ($process.HasExited) { throw 'Application exited' }
    [pscustomobject]@{Label=$Label;Trial=$trial;Stage=$stage;WorkingSetMB=[math]::Round($process.WorkingSet64/1MB,2);PrivateMB=[math]::Round($process.PrivateMemorySize64/1MB,2);CPU=$process.TotalProcessorTime.TotalSeconds;Handles=$process.HandleCount}
}
$results=@()
for ($trial=1; $trial -le $Trials; $trial++) {
    $appProcess=Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru
    $fixture=$null
    try {
        for ($i=0; $i -lt 100; $i++) { $appProcess.Refresh(); if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }; Start-Sleep -Milliseconds 100 }
        if ($appProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'No main window' }
        Start-Sleep -Seconds 3
        $results+=Sample $appProcess 'main' $trial
        [void][MemoryNative]::PostMessage($appProcess.MainWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
        Start-Sleep -Seconds 2
        $results+=Sample $appProcess 'tray' $trial
        $fixture=Start-Process -FilePath "$PSScriptRoot\bin\Release\net8.0-windows\TermLens.Tests.exe" -ArgumentList '--fixture','churn' -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 14
        $fixture.Refresh()
        if ([MemoryNative]::GetForegroundWindow() -ne $fixture.MainWindowHandle) { throw 'Fixture lost focus; measurement invalid' }
        if (-not [MemoryNative]::Titles($appProcess.Id).Contains('点击解释选中术语')) { throw "Selection entry point not created: $([MemoryNative]::Titles($appProcess.Id))" }
        $results+=Sample $appProcess '20 selections without querying' $trial
        $results[-1] | ConvertTo-Json -Compress
    } finally {
        if ($fixture -and !$fixture.HasExited) { Stop-Process -Id $fixture.Id }
        if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    }
}
$results | ConvertTo-Json | Set-Content "$PSScriptRoot\..\artifacts\memory-$Label.json"
