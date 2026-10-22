using System;
using System.Windows;
using System.Windows.Media;

namespace TermLens;

public static class DisplayScale
{
    // A layout transform scales fonts, spacing and icons together while text still re-wraps to the window.
    public static void Apply(FrameworkElement root, double scale)
    {
        var current = root.LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1.0;
        if (Math.Abs(current - scale) < 0.005) return;
        var unscaled = Math.Abs(scale - 1) < 0.005;
        root.LayoutTransform = unscaled ? Transform.Identity : new ScaleTransform(scale, scale);
        // Display text formatting snaps glyphs to pixels and blurs once scaled.
        TextOptions.SetTextFormattingMode(root, unscaled ? TextFormattingMode.Display : TextFormattingMode.Ideal);
    }
}
