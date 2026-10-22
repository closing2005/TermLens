using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TermLens;

internal static partial class Program
{
    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object app);
    private static int WpsProbe()
    {
        object? application = null, document = null;
        try
        {
            var type = Type.GetTypeFromProgID("kwps.Application") ?? throw new Exception("WPS automation not installed");
            var clsid = type.GUID;
            GetActiveObject(ref clsid, IntPtr.Zero, out application);
            dynamic app = application;
            document = app.Documents.Add();
            dynamic doc = document;
            doc.Content.Text = "随机森林 gradient descent";
            app.Visible = true;
            doc.Activate();
            doc.Range(0, 4).Select();
            var window = new IntPtr((long)app.ActiveWindow.Hwnd);
            Native.GetWindowRect(window, out var rect);
            Thread.Sleep(600);
            var selected = Task.Run(() => DesktopSelection.ReadAt(new Native.Point(rect.Left + 300, rect.Top + 300), window)).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (selected == null)
            {
                var method = typeof(DesktopSelection).Assembly.GetType("TermLens.OfficeSelection")!.GetMethod("TryRead")!;
                _ = Task.Run(() => method.Invoke(null, new object[] { window, new Native.Point(rect.Left + 300, rect.Top + 300), new Action<string>(Console.WriteLine) })).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            }
            var result = selected == "随机森林" ? "PASS WPS document selection" : "UNSUPPORTED WPS selection via current reader";
            File.WriteAllText("artifacts/wps-compatibility.txt", result);
            Console.WriteLine(result);
            return selected == "随机森林" ? 0 : 2;
        }
        catch (Exception ex) { Console.WriteLine("WPS probe: " + ex.GetType().Name + ": " + ex.Message); return 1; }
        finally
        {
            // Close only the temporary document created above, never the user's other documents.
            if (document != null) { try { ((dynamic)document).Close(0); } catch { } Marshal.ReleaseComObject(document); }
            if (application != null) Marshal.ReleaseComObject(application);
        }
    }
}
