param([string]$Executable = "$PSScriptRoot\..\release\TermLens\TermLens.exe")
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);
}
'@
$appProcess = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable) -WindowStyle Hidden -PassThru -RedirectStandardError "$PSScriptRoot\..\artifacts\startup-error.txt" -RedirectStandardOutput "$PSScriptRoot\..\artifacts\startup-output.txt"
try {
    $window = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 400; $attempt++) {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw "Application exited during startup: $($appProcess.ExitCode)" }
        $window = $appProcess.MainWindowHandle
        [uint32]$windowProcess = 0
        [void][SmokeNative]::GetWindowThreadProcessId($window, [ref]$windowProcess)
        if ($window -ne [IntPtr]::Zero -and $windowProcess -eq $appProcess.Id) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($window -eq [IntPtr]::Zero -or $windowProcess -ne $appProcess.Id) {
        Get-Content "$PSScriptRoot\..\artifacts\startup-error.txt"
        throw "Main window not created; process title: $($appProcess.MainWindowTitle)"
    }
    Start-Sleep -Milliseconds 500
    $appProcess.Refresh()
    if ($appProcess.HasExited) { throw 'Application exited after initialization' }
    Write-Output 'PASS Published self-contained executable starts and creates its main window'
    [void][SmokeNative]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    $appProcess.Refresh()
    if ($appProcess.HasExited -or [SmokeNative]::IsWindowVisible($window)) { throw 'Close-to-tray behavior failed' }
    Write-Output 'PASS Closing main window leaves application running in tray'
}
finally {
    if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id -Force }
}
