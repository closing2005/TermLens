using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TermLens;

// Invoked only by a click on the non-activating selection badge, never on drag.
public static class SelectionCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputData { [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    private static bool busy;

    private static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
    private static bool SameWindow(IntPtr origin) => origin != IntPtr.Zero && !Native.IsOurWindow(origin) && Native.GetForegroundWindow() == origin;
    private static DataObject Snapshot()
    {
        var snapshot = new DataObject();
        var original = Clipboard.GetDataObject();
        if (original == null) return snapshot;
        foreach (var format in original.GetFormats(false))
        {
            var value = original.GetData(format, false);
            // Materialize data before Ctrl+C invalidates the original OLE object.
            object? clone = value switch
            {
                null => null,
                string text => text,
                byte[] bytes => bytes.Clone(),
                string[] paths => paths.Clone(),
                MemoryStream stream => new MemoryStream(stream.ToArray()),
                BitmapSource bitmap => bitmap.Clone(),
                _ => throw new UserError("剪贴板包含暂不支持备份的格式，请手动复制选中文字后按 Ctrl+Alt+D 查询。")
            };
            if (clone != null) snapshot.SetData(format, clone);
        }
        return snapshot;
    }
    public static async Task<string> ReadAsync(IntPtr origin)
    {
        if (busy) throw new UserError("正在读取选区，请稍候再试。");
        if (!SameWindow(origin)) throw new UserError("原窗口已切换，请重新选中文字后点击图标。");
        busy = true;
        uint copiedSequence = 0;
        DataObject? saved = null;
        try
        {
            // Do not combine our chord with a held Alt/Shift/Windows key.
            foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) throw new UserError("请松开键盘快捷键后，再点击选词图标。");
            var before = GetClipboardSequenceNumber();
            saved = Snapshot();
            if (!SameWindow(origin) || before != GetClipboardSequenceNumber()) throw new UserError("窗口或剪贴板已变化，请重新点击查询。");
            var keys = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
            if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>()) != keys.Length)
            {
                _ = SendInput(2, new[] { Key(0x43, true), Key(0x11, true) }, Marshal.SizeOf<Input>());
                throw new UserError("当前窗口未接受复制操作，请手动复制后按 Ctrl+Alt+D 查询。");
            }
            Native.GetWindowThreadProcessId(origin, out var sourcePid);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(40);
                if (!SameWindow(origin)) throw new UserError("窗口已切换，本次读取已取消。");
                var sequence = GetClipboardSequenceNumber();
                if (sequence == before) continue;
                Native.GetWindowThreadProcessId(GetClipboardOwner(), out var ownerPid);
                if (ownerPid != sourcePid) throw new UserError("剪贴板被其他程序更新，本次读取已取消。");
                copiedSequence = sequence;
                try
                {
                    if (!Clipboard.ContainsText()) continue;
                    var text = Clipboard.GetText();
                    if (sequence != GetClipboardSequenceNumber()) { copiedSequence = 0; throw new UserError("剪贴板已变化，请重新查询。"); }
                    return QueryRules.Validate(text, "");
                }
                catch (COMException) { /* A PDF viewer may still own the clipboard briefly. */ }
            }
            throw new UserError("没有读到可复制的选中文字。请重新划选，或复制后按 Ctrl+Alt+D；扫描图片暂不支持。");
        }
        finally
        {
            try
            {
                if (saved != null && copiedSequence != 0 && copiedSequence == GetClipboardSequenceNumber())
                {
                    for (var retry = 0; retry < 3; retry++)
                    {
                        if (copiedSequence != GetClipboardSequenceNumber()) break;
                        try { if (saved.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); break; }
                        catch (COMException) { await Task.Delay(30); }
                    }
                }
            }
            finally { busy = false; }
        }
    }
}
