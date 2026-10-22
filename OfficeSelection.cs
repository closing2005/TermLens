using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TermLens;

internal static class OfficeSelection
{
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object application);

    public static Selection? TryRead(IntPtr origin, Native.Point point, Action<string>? diagnostic = null)
    {
        object? application = null, window = null, selected = null;
        try
        {
            Native.GetWindowThreadProcessId(origin, out var pid);
            using var process = Process.GetProcessById((int)pid);
            var progId = process.ProcessName.ToLowerInvariant() switch { "wps" => "kwps.Application", "winword" => "Word.Application", _ => null };
            if (progId == null) return null;
            diagnostic?.Invoke("Editor recognized");
            var type = Type.GetTypeFromProgID(progId);
            if (type == null) return null;
            var clsid = type.GUID;
            // Attach only to an already running editor. Never create an application,
            // open a document, change selection, or read an entire document here.
            GetActiveObject(ref clsid, IntPtr.Zero, out application);
            diagnostic?.Invoke("Running automation object found");
            window = ((dynamic)application).ActiveWindow;
            diagnostic?.Invoke("Active window found");
            var hwnd = new IntPtr((long)((dynamic)window).Hwnd);
            if (Native.GetAncestor(hwnd, 2) != Native.GetAncestor(origin, 2)) { diagnostic?.Invoke("Window identity differs"); return null; }
            selected = ((dynamic)window).Selection;
            diagnostic?.Invoke("Selection object found");
            dynamic selection = selected;
            diagnostic?.Invoke("Selection type: " + (int)selection.Type);
            if ((int)selection.Type != 2) return null; // wdSelectionNormal, excludes insertion point.
            var length = (int)selection.End - (int)selection.Start;
            if (length <= 0 || length > QueryRules.MaxTerm) return null;
            var text = ((string)selection.Text).Trim();
            return text.Length is > 0 and <= QueryRules.MaxTerm ? new Selection(text, point) : null;
        }
        catch (Exception ex) { diagnostic?.Invoke(ex.GetType().Name + " HRESULT=" + ex.HResult); return null; }
        finally
        {
            foreach (var item in new[] { selected, window, application })
                if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }
}
