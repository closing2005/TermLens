using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TermLens;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static void ActivateFixture(IntPtr window)
    {
        var current = GetCurrentThreadId();
        var foreground = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var attached = current != foreground && AttachThreadInput(current, foreground, true);
        try { ShowWindow(window, 9); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    private static void CaptureTests()
    {
        System.Threading.Tasks.Task<string> Capture(IntPtr window) => Environment.GetEnvironmentVariable("TERMLENS_TEST_RESOLVE") == "1"
            ? DesktopSelection.ResolveAsync(new Selection("", new Native.Point(100, 100), window)) : SelectionCapture.ReadAsync(window);
        var saved = (DataObject)typeof(SelectionCapture).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            foreach (var mode in new[] { "sentence", "empty" })
                Test("跨进程兼容读取：" + mode, () =>
                {
                    using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture " + mode) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
                    try
                    {
                        for (var i = 0; i < 60; i++) { process.Refresh(); if (process.MainWindowHandle != IntPtr.Zero) break; Thread.Sleep(100); }
                        ShowWindow(process.MainWindowHandle, 9);
                        Native.SetWindowPos(process.MainWindowHandle, new IntPtr(-1), 0, 0, 0, 0, 0x43);
                        ActivateFixture(process.MainWindowHandle);
                        Thread.Sleep(300);
                        Assert(Native.GetForegroundWindow() == process.MainWindowHandle);
                        Clipboard.SetText("previous clipboard must never become a query");
                        var capture = Capture(process.MainWindowHandle);
                        PumpUntil(() => capture.IsCompleted);
                        if (mode == "sentence") Equal(capture.GetAwaiter().GetResult(), "随机森林 gradient descent");
                        else Throws<UserError>(() => capture.GetAwaiter().GetResult());
                        Equal(Clipboard.GetText(), "previous clipboard must never become a query");
                        if (mode == "sentence")
                        {
                            Clipboard.Clear();
                            capture = Capture(process.MainWindowHandle);
                            PumpUntil(() => capture.IsCompleted);
                            Equal(capture.GetAwaiter().GetResult(), "随机森林 gradient descent");
                            Assert(!Clipboard.ContainsText());
                        }
                        var invalid = Capture(IntPtr.Zero);
                        Throws<UserError>(() => invalid.GetAwaiter().GetResult());
                    }
                    finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(true); } }
                });
        }
        finally { if (saved.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); SynchronizationContext.SetSynchronizationContext(context); }
    }
}
