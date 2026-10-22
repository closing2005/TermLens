using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TermLens;

public sealed class SelectionBadge : Window
{
    private readonly DispatcherTimer expiry;
    private Selection? current;
    private readonly Button button;
    public event Action<Selection>? Chosen;
    public SelectionBadge()
    {
        Width = 40; Height = 40;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Title = "点击解释选中术语";
        button = new Button
        {
            Content = new Image { Source = (ImageSource)Application.Current.FindResource("BrandMark"), Width = 32, Height = 32 },
            Padding = new Thickness(0), Margin = new Thickness(2),
            Background = Brushes.Transparent,
            Foreground = Brushes.White, ToolTip = "解释选中术语", Focusable = false
        };
        Content = button;
        button.Click += (_, _) => { var selection = current; Dismiss(); if (selection != null) Chosen?.Invoke(selection); };
        expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        expiry.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowLongPtr(handle, -20, new IntPtr(Native.GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x0021) { handled = true; return new IntPtr(3); }
                return IntPtr.Zero;
            });
        };
    }
    public void Present(Selection selection)
    {
        current = selection;
        button.ToolTip = selection.Source != IntPtr.Zero ? "读取选中的词句（临时复制，并尝试恢复剪贴板）" : "解释选中的词句";
        Show();
        Native.Place(this, selection.Anchor);
        expiry.Stop();
        expiry.Start();
    }
    public void Dismiss() { expiry.Stop(); current = null; Hide(); }
}
