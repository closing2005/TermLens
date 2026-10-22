using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TermLens;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MouseData { public Point Point; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    public static bool IsOurWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId;
    }
    public static bool Contains(Window window, Point point)
    {
        if (!window.IsVisible) return false;
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
    }
    public static void Place(Window window, Point anchor)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).WorkingArea;
        // Move first so WPF receives the target monitor DPI before measuring.
        SetWindowPos(handle, new IntPtr(-1), anchor.X, anchor.Y, 0, 0, 0x0011);
        window.UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        window.MaxHeight = Math.Max(100, (area.Height - 24) / dpi.DpiScaleY);
        window.MaxWidth = Math.Max(100, (area.Width - 24) / dpi.DpiScaleX);
        window.UpdateLayout();
        GetWindowRect(handle, out var size);
        var width = size.Right - size.Left;
        var height = size.Bottom - size.Top;
        var x = Math.Clamp(anchor.X + 12, area.Left + 8, Math.Max(area.Left + 8, area.Right - width - 8));
        var y = anchor.Y + 18;
        if (y + height > area.Bottom - 8) y = anchor.Y - height - 12;
        y = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - height - 8));
        SetWindowPos(handle, new IntPtr(-1), x, y, 0, 0, 0x0011);
    }
}

public sealed record Selection(string Text, Native.Point Anchor, IntPtr Source = default);

public sealed class DesktopSelection : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Native.HookProc callback;
    private readonly IntPtr hook;
    private readonly Native.WinEventProc selectionCallback;
    private readonly IntPtr selectionHook;
    private bool mouseHeld;
    private bool disposed;
    private Native.Point downPoint;
    private long downTime;
    private bool doubleClick;
    private readonly DispatcherTimer timer;
    private Native.Point lastPoint;
    private IntPtr source;
    private static int reading;
    private bool paused;
    public event Action<Selection>? Selected;
    public event Action<Native.Point>? MouseDown;
    public bool Paused
    {
        get => paused;
        set { paused = value; timer.Stop(); }
    }
    public DesktopSelection(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        callback = OnMouse;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background, ReadSelection, dispatcher);
        timer.Stop();
        hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new UserError("无法启用划词监听。可以继续使用手动查询，或重新启动程序。");
        selectionCallback = (_, _, window, _, _, _, _) =>
        {
            if (window == IntPtr.Zero || Native.IsOurWindow(window)) return;
            dispatcher.BeginInvoke(() =>
            {
                if (disposed || paused || mouseHeld || Native.GetAncestor(window, 2) != Native.GetForegroundWindow()) return;
                Native.GetCursorPos(out var point);
                Schedule(point);
            });
        };
        // EVENT_OBJECT_TEXTSELECTIONCHANGED; no keyboard text is captured.
        selectionHook = Native.SetWinEventHook(0x8014, 0x8014, IntPtr.Zero, selectionCallback, 0, 0, 2);
    }
    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var msg = message.ToInt32();
            if (msg is 0x0201 or 0x0204 or 0x0207 or 0x0202)
            {
                var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                if ((mouse.Flags & 1) == 0)
                    dispatcher.BeginInvoke(() => HandleMouse(msg, mouse.Point));
            }
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }
    private void HandleMouse(int message, Native.Point point)
    {
        if (message != 0x0202)
        {
            if (message == 0x0201)
            {
                doubleClick = Environment.TickCount64 - downTime <= System.Windows.Forms.SystemInformation.DoubleClickTime &&
                    Math.Abs(point.X - downPoint.X) <= 4 && Math.Abs(point.Y - downPoint.Y) <= 4;
                downPoint = point;
                downTime = Environment.TickCount64;
            }
            mouseHeld = message == 0x0201;
            timer.Stop();
            MouseDown?.Invoke(point);
            return;
        }
        mouseHeld = false;
        if (disposed || paused || Native.IsOurWindow(Native.WindowFromPoint(point))) return;
        if (!doubleClick && Math.Abs(point.X - downPoint.X) < 3 && Math.Abs(point.Y - downPoint.Y) < 3 && (Native.GetAsyncKeyState(0x10) & 0x8000) == 0) return;
        Schedule(point);
    }
    private void Schedule(Native.Point point)
    {
        timer.Interval = TimeSpan.FromMilliseconds(180);
        lastPoint = point;
        source = Native.GetForegroundWindow();
        timer.Stop();
        timer.Start();
    }
    private void ReadSelection(object? sender, EventArgs args)
    {
        timer.Stop();
        if (disposed || paused) return;
        // Selection gestures only show the entry point. Traverse external controls
        // when the user actually requests an explanation, not while reading.
        if (source == Native.GetForegroundWindow() && !Native.IsOurWindow(source))
            Selected?.Invoke(new Selection("", lastPoint, source));
    }
    public static async Task<string> ResolveAsync(Selection selected)
    {
        if (selected.Text.Length > 0) return selected.Text;
        var origin = selected.Source;
        if (origin == IntPtr.Zero || origin != Native.GetForegroundWindow() || Native.IsOurWindow(origin))
            throw new UserError("原窗口已切换，请重新选中文字后点击图标。");
        Selection? result = null;
        if (Interlocked.CompareExchange(ref reading, 1, 0) == 0)
        {
            // A hung provider must not block copy fallback or accumulate workers.
            var task = Task.Run(() =>
            {
                try { return ReadDetailed(selected.Anchor, origin); }
                catch { return null; }
                finally { Interlocked.Exchange(ref reading, 0); }
            });
            try { result = await task.WaitAsync(TimeSpan.FromMilliseconds(800)); }
            catch (TimeoutException) { }
        }
        if (origin != Native.GetForegroundWindow())
            throw new UserError("窗口已切换，本次读取已取消。");
        return result?.Text ?? await SelectionCapture.ReadAsync(origin);
    }
    public static string? ReadAt(Native.Point point, IntPtr origin) => ReadDetailed(point, origin)?.Text;
    public static Selection? ReadDetailed(Native.Point point, IntPtr origin)
    {
        Native.GetWindowThreadProcessId(origin, out var pid);
        if (pid == 0 || pid == Environment.ProcessId) return null;
        var budget = Stopwatch.StartNew();
        var focus = Safe(() => AutomationElement.FocusedElement);
        if (focus != null && Safe(() => focus.Current.ProcessId == pid && focus.Current.IsPassword)) return null;
        var hit = Safe(() => AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y)));
        budget.Restart();
        var roots = new List<AutomationElement>();
        var visited = new HashSet<string>();
        bool Belongs(AutomationElement element) => Safe(() => element.Current.ProcessId == pid && !element.Current.IsPassword);
        Selection? Read(AutomationElement element, bool withinSourceWindow = false)
        {
            if (withinSourceWindow ? !Safe(() => !element.Current.IsPassword) : !Belongs(element)) return null;
            try
            {
                var id = string.Join(".", element.GetRuntimeId());
                if (!visited.Add(id)) return null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
                var ranges = ((TextPattern)pattern).GetSelection();
                if (ranges.Length != 1) return null;
                var text = ranges[0].GetText(QueryRules.MaxTerm + 1).Trim();
                if (text.Length == 0 || text.Length > QueryRules.MaxTerm) return null;
                var anchor = point;
                try
                {
                    var rect = ranges[0].GetBoundingRectangles().Where(r => !r.IsEmpty && r.Width > 0 && r.Height > 0 && double.IsFinite(r.X) && double.IsFinite(r.Y))
                        .OrderBy(r => Math.Abs(r.X + r.Width / 2 - point.X) + Math.Abs(r.Y + r.Height / 2 - point.Y)).FirstOrDefault();
                    if (rect.Width > 0 && rect.Height > 0)
                        anchor = new Native.Point((int)Math.Clamp(point.X, rect.Left, rect.Right), (int)rect.Bottom);
                }
                catch { /* Selection text can still be used without geometry. */ }
                return new Selection(text, anchor);
            }
            catch { return null; }
        }
        foreach (var initial in new[] { focus, hit, Safe(() => AutomationElement.FromHandle(origin)) })
        {
            var element = initial;
            for (var i = 0; element != null && i < 10 && budget.ElapsedMilliseconds < 700; i++)
            {
                if (!Belongs(element)) break;
                var found = Read(element);
                if (found != null) return found;
                roots.Add(element);
                element = Safe(() => TreeWalker.ControlViewWalker.GetParent(element));
            }
        }
        // Browser/PDF selections may live in a nested Document rather than the
        // focused container. Traverse a bounded visible subtree, never DocumentRange.
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        var sourceRoot = Safe(() => AutomationElement.FromHandle(origin));
        if (sourceRoot != null) queue.Enqueue((sourceRoot, 0));
        foreach (var root in roots.AsEnumerable().Reverse().Take(3)) queue.Enqueue((root, 0));
        var expanded = new HashSet<string>();
        for (var count = 0; queue.Count > 0 && count < 180 && budget.ElapsedMilliseconds < 700; count++)
        {
            var (element, depth) = queue.Dequeue();
            // Chromium PDF documents can be hosted in a different renderer process.
            // Traversal remains rooted in the explicitly selected native window.
            var found = Read(element, true);
            if (found != null) return found;
            if (depth >= 16) continue;
            var id = Safe(() => string.Join(".", element.GetRuntimeId()));
            if (id == null || !expanded.Add(id)) continue;
            var child = Safe(() => TreeWalker.ControlViewWalker.GetFirstChild(element));
            for (var n = 0; child != null && n < 24 && budget.ElapsedMilliseconds < 700; n++)
            {
                if (Safe(() => !child.Current.IsPassword && !child.Current.IsOffscreen)) queue.Enqueue((child, depth + 1));
                child = Safe(() => TreeWalker.ControlViewWalker.GetNextSibling(child));
            }
        }
        // WPS/Word may expose selection via their running automation object only.
        // An edit box (e.g. Find or a ribbon field) must not reuse document selection.
        if (focus != null && Safe(() => focus.Current.ProcessId == pid && focus.Current.ControlType == ControlType.Edit)) return null;
        return OfficeSelection.TryRead(origin, point);
    }
    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
    public void Dispose()
    {
        disposed = true;
        Paused = true;
        Native.UnhookWindowsHookEx(hook);
        if (selectionHook != IntPtr.Zero) Native.UnhookWinEvent(selectionHook);
        GC.KeepAlive(selectionCallback);
        GC.KeepAlive(callback);
    }
}
