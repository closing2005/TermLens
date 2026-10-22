using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLens;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    private static string Event(string text, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = finish } } }) + "\r\n\r\n";
    private static HttpResponseMessage Events(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
    private static async Task UpgradeApiTests()
    {
        var settings = new ApiSettings("https://example.com/v1", "test-only-key", "test-model");
        Test("流式解析：未闭合的字符串也能显示", () => Equal(ExplanationClient.ParsePartial("{\"definition\":\"已经生成的中文", "RF").Definition, "已经生成的中文"));
        Test("流式解析：转义字符、换行和半个 Unicode", () =>
        {
            Equal(ExplanationClient.ParsePartial("{\"definition\":\"引号\\\"和\\n换行\\u4E", "RF").Definition, "引号\"和\n换行");
            Equal(ExplanationClient.ParsePartial("{\"definition\":\"\\u4E2D\\u6587", "RF").Definition, "中文");
        });
        Test("流式解析：字段名出现在字符串中不会误识别", () => Equal(ExplanationClient.ParsePartial("{\"definition\":\"他说\\\"usage\\\":\\\"例子\\\"", "RF").Usage, ""));
        Test("解释：含义、歧义说明包含在复制结果", () =>
        {
            var answer = Answer[..^1] + ",\"meaning\":\"计算机领域\",\"ambiguity\":\"请补充原句确认\"}";
            var parsed = ExplanationClient.Parse(answer, "RF");
            Assert(parsed.CopyText.Contains("计算机领域") && parsed.CopyText.Contains("请补充原句确认"));
        });
        await TestAsync("流式传输：结束前显示部分内容，支持碎片化 UTF-8", async () =>
        {
            var split = Answer.IndexOf("分类或预测", StringComparison.Ordinal);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var source = new GatedStream(Event(Answer[..split]), Event(Answer[split..], "stop") + "data: [DONE]\n\n", release.Task);
            using var client = new ExplanationClient(new FakeHandler(async (request, token) =>
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert(doc.RootElement.GetProperty("stream").GetBoolean());
                return Events(source);
            }));
            var task = client.ExplainAsync(settings, "RF", "", CancellationToken.None, new InlineProgress(x => { if (x.Definition.Length > 0) received.TrySetResult(); }));
            try
            {
                await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(!task.IsCompleted);
            }
            finally { release.TrySetResult(); }
            Equal((await task).English, "Random Forest");
        });
        await TestAsync("流式传输：正文等待时可以取消", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(Events(new GatedStream(Event("{\"chinese\":\"随机"), "", new TaskCompletionSource().Task)))));
            using var cancel = new CancellationTokenSource(80);
            await ThrowsAsync<OperationCanceledException>(() => client.ExplainAsync(settings, "RF", "", cancel.Token));
        });
        await TestAsync("流式传输：超时包含响应正文阶段", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(Events(new GatedStream(Event("{\"chinese\":\"随机"), "", new TaskCompletionSource().Task)))), TimeSpan.FromMilliseconds(80));
            await ThrowsAsync<UserError>(() => client.ExplainAsync(settings, "RF", "", CancellationToken.None));
        });
        foreach (var suffix in new[] { "", Event("", "length"), "data: {\"error\":{\"message\":\"private-secret\"}}\n\n" })
            await TestAsync("流式传输：提前中断、截断或错误不能当作成功", async () =>
            {
                using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(Events(new MemoryStream(Encoding.UTF8.GetBytes(Event(Answer) + suffix))))));
                await ThrowsAsync<UserError>(() => client.ExplainAsync(settings, "RF", "", CancellationToken.None));
            });
        await TestAsync("流式传输：注释、空 choices、终止事件无空行", async () =>
        {
            var sse = ": heartbeat\n\ndata: {\"choices\":[]}\n\n" + Event(Answer) + "data: [DONE]";
            using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(Events(new MemoryStream(Encoding.UTF8.GetBytes(sse))))));
            Equal((await client.ExplainAsync(settings, "RF", "", CancellationToken.None)).Chinese, "随机森林");
        });
        await TestAsync("流式传输：密集分片限制刷新次数且保留完整结果", async () =>
        {
            var content = string.Concat(Answer.Select(c => Event(c.ToString()))) + "data: [DONE]\n\n";
            var updates = 0;
            using var client = new ExplanationClient(new FakeHandler((_, _) => Task.FromResult(Events(new MemoryStream(Encoding.UTF8.GetBytes(content))))));
            var result = await client.ExplainAsync(settings, "RF", "", CancellationToken.None, new InlineProgress(_ => updates++));
            Equal(result.Chinese, "随机森林");
            Assert(updates > 0 && updates < Answer.Length / 2);
        });
        await TestAsync("上下文重查：发送新的原句，支持普通查询", async () =>
        {
            using var client = new ExplanationClient(new FakeHandler(async (request, token) =>
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert(!doc.RootElement.GetProperty("stream").GetBoolean());
                var messages = doc.RootElement.GetProperty("messages");
                Assert(messages[0].GetProperty("content").GetString()!.Contains("不要把推断当成确定事实"));
                using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Equal(data.RootElement.GetProperty("term").GetString(), "Transformer");
                Equal(data.RootElement.GetProperty("context").GetString(), "The transformer reduces voltage.");
                return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Answer } } } }));
            }));
            await client.ExplainAsync(settings, "Transformer", "The transformer reduces voltage.", CancellationToken.None, stream: false);
        });
    }
    private sealed class InlineProgress(Action<Explanation> update) : IProgress<Explanation>
    { public void Report(Explanation value) => update(value); }
    private sealed class GatedStream(string first, string last, Task gate, Action? onCancelled = null) : Stream
    {
        private readonly byte[] head = Encoding.UTF8.GetBytes(first), tail = Encoding.UTF8.GetBytes(last);
        private int position;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { if (position >= head.Length) await gate.WaitAsync(token); }
            catch (OperationCanceledException) { onCancelled?.Invoke(); throw; }
            var source = position < head.Length ? head : tail;
            var offset = position < head.Length ? position : position - head.Length;
            var count = Math.Min(3, Math.Min(buffer.Length, source.Length - offset));
            if (count <= 0) return 0;
            source.AsMemory(offset, count).CopyTo(buffer); position += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static string FindBrowser(string name)
    {
        var custom = Environment.GetEnvironmentVariable(name == "chrome" ? "TERMLENS_TEST_CHROME_PATH" : "TERMLENS_TEST_EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        var relative = name == "chrome" ? Path.Combine("Google", "Chrome", "Application", "chrome.exe") : Path.Combine("Microsoft", "Edge", "Application", "msedge.exe");
        foreach (var location in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            var path = Path.Combine(Environment.GetFolderPath(location), relative);
            if (File.Exists(path)) return path;
        }
        throw new Exception("Test browser is not installed; set TERMLENS_TEST_CHROME_PATH or TERMLENS_TEST_EDGE_PATH.");
    }
    private static void BrowserTest(string executable, string name, bool pdf = false)
    {
        if (!File.Exists(executable)) throw new Exception("Browser is not installed");
        Directory.CreateDirectory("artifacts");
        if (pdf) File.Copy(Path.Combine("Tests", "Fixtures", "selection-fixture.pdf"), Path.Combine("artifacts", "selection-fixture.pdf"), overwrite: true);
        var html = Path.GetFullPath("artifacts/selection-fixture.html");
        File.WriteAllText(html, """
            <!doctype html><meta charset="utf-8"><title>TermLens compatibility fixture</title>
            <body style="font:32px sans-serif;padding:60px"><p>TermLens compatibility fixture</p>
            <span id="term">随机森林</span><p>Only this test page is used.</p>
            <script>setTimeout(()=>{const r=document.createRange();r.selectNodeContents(document.querySelector('#term'));const s=getSelection();s.removeAllRanges();s.addRange(r);},800);</script>
            """);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--user-data-dir=" + Path.GetFullPath("artifacts/" + name + "-test-profile-" + Guid.NewGuid().ToString("N")[..6]));
        start.ArgumentList.Add("--no-first-run"); start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--guest");
        // Exercise the reader against an explicitly exposed accessibility tree.
        // This changes only the isolated fixture process, never the user's browser.
        if (Environment.GetEnvironmentVariable("TERMLENS_TEST_EXPOSE_ACCESSIBILITY") == "1")
        {
            start.ArgumentList.Add("--force-renderer-accessibility");
            start.ArgumentList.Add("--enable-features=UiaProvider");
        }
        start.ArgumentList.Add("--disable-background-networking"); start.ArgumentList.Add("--disable-extensions");
        start.ArgumentList.Add("--new-window"); start.ArgumentList.Add(new Uri(pdf ? Path.GetFullPath("artifacts/selection-fixture.pdf") : html).AbsoluteUri);
        using var process = Process.Start(start)!;
        try
        {
            for (var i = 0; i < 100; i++)
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                if (process.HasExited) throw new Exception("Isolated browser process exited");
                Thread.Sleep(100);
            }
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero) throw new Exception("Browser window not created");
            ShowWindow(window, 9);
            Native.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
            SetForegroundWindow(window);
            Thread.Sleep(1800);
            Native.GetWindowRect(window, out var rect);
            var point = new Native.Point(rect.Left + 100, rect.Top + 270);
            if (pdf)
            {
                var targetBounds = new System.Windows.Rect();
                point = Task.Run(() =>
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(window);
                    for (var wait = 0; wait < 12; wait++)
                    {
                    var nodes = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsTextPatternAvailableProperty, true));
                    foreach (System.Windows.Automation.AutomationElement node in nodes)
                    {
                        var pattern = (System.Windows.Automation.TextPattern)node.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern);
                        var range = pattern.DocumentRange.FindText("Random forest", false, true);
                        if (range == null) continue;
                        range.Select();
                        var bounds = range.GetBoundingRectangles();
                        if (bounds.Length > 0) { targetBounds = bounds[0]; return new Native.Point((int)(bounds[0].X + 20), (int)(bounds[0].Y + 5)); }
                    }
                    Thread.Sleep(400);
                    }
                    throw new Exception("PDF provider does not expose a selectable text range");
                }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                // This PDF provider supports range geometry but ignores Range.Select.
                // Make a real selection, confined to our isolated fixture window.
                SetForegroundWindow(window);
                Native.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
                Thread.Sleep(250);
                var startPoint = new Native.Point((int)targetBounds.Left - 4, (int)(targetBounds.Top + targetBounds.Height / 2));
                if (Native.GetAncestor(Native.WindowFromPoint(startPoint), 2) != window) throw new Exception("Test PDF window is obscured");
                Native.GetCursorPos(out var previous);
                try
                {
                    SetCursorPos(startPoint.X, startPoint.Y);
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(100);
                    for (var step = 1; step <= 12; step++)
                    {
                        SetCursorPos(startPoint.X + (int)((targetBounds.Width + 20) * step / 12), startPoint.Y);
                        Thread.Sleep(20);
                    }
                    Thread.Sleep(100);
                }
                finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(100); SetCursorPos(previous.X, previous.Y); }
                Thread.Sleep(300);
                if (Environment.GetEnvironmentVariable("TERMLENS_TEST_PDF_SELECT_ALL") == "1")
                {
                    if (Native.GetForegroundWindow() != window) throw new Exception("Fixture PDF is not foreground");
                    System.Windows.Forms.SendKeys.SendWait("^a");
                    Thread.Sleep(200);
                }
            }
            string? text = null;
            var expectedSelection = pdf ? "Random forest" : "随机森林";
            if (Environment.GetEnvironmentVariable("TERMLENS_TEST_CAPTURE_COPY") == "1")
            {
                if (pdf)
                {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(window);
                    foreach (System.Windows.Automation.AutomationElement node in root.FindAll(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsTextPatternAvailableProperty, true)))
                    {
                        var p = (System.Windows.Automation.TextPattern)node.GetCurrentPattern(System.Windows.Automation.TextPattern.Pattern);
                        foreach (var r in p.GetSelection()) { var selectedText = r.GetText(200); if (selectedText.Length > 0 && "Random forest".Contains(selectedText)) expectedSelection = selectedText; }
                    }
                }
                var saved = (System.Windows.DataObject)typeof(SelectionCapture).GetMethod("Snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
                var previousContext = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                try
                {
                    var sentinel = new System.Windows.DataObject();
                    sentinel.SetText("TermLens clipboard preservation fixture");
                    sentinel.SetData(System.Windows.DataFormats.Html, "<b>fixture</b>");
                    System.Windows.Clipboard.SetDataObject(sentinel, true);
                    var capture = Environment.GetEnvironmentVariable("TERMLENS_TEST_RESOLVE") == "1" ? DesktopSelection.ResolveAsync(new Selection("", point, window)) : SelectionCapture.ReadAsync(window);
                    PumpUntil(() => capture.IsCompleted);
                    text = capture.GetAwaiter().GetResult();
                    Equal(System.Windows.Clipboard.GetText(), "TermLens clipboard preservation fixture");
                    Equal(System.Windows.Clipboard.GetData(System.Windows.DataFormats.Html)?.ToString(), "<b>fixture</b>");
                    Console.WriteLine("PASS compatibility capture restores text and HTML clipboard formats");
                }
                finally { System.Windows.Clipboard.SetDataObject(saved, true); SynchronizationContext.SetSynchronizationContext(previousContext); }
                Equal(text, expectedSelection);
                return;
            }
            for (var attempt = 0; attempt < 5 && text == null; attempt++)
            {
                text = Task.Run(() => DesktopSelection.ReadAt(point, window)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (text == null) Thread.Sleep(500);
            }
            if (text == null)
            {
                var details = Task.Run(() => {
                    var root = System.Windows.Automation.AutomationElement.FromHandle(window);
                    var nodes = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    var lines = new List<string> { "Window: " + root.Current.Name };
                    foreach (System.Windows.Automation.AutomationElement node in nodes)
                    {
                        if (lines.Count >= 70) break;
                        var entry = node.Current.ControlType.ProgrammaticName + ": " + node.Current.Name + " offscreen=" + node.Current.IsOffscreen;
                        if (node.Current.ControlType == System.Windows.Automation.ControlType.Document)
                        {
                            var parent = node; var depth = 0;
                            while (parent != null && !parent.Equals(root) && depth < 30) { parent = System.Windows.Automation.TreeWalker.ControlViewWalker.GetParent(parent); depth++; }
                            entry += " depth=" + depth;
                        }
                        if (node.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var p))
                            entry += " SELECTED=" + string.Join("|", ((System.Windows.Automation.TextPattern)p).GetSelection().Select(r => r.GetText(100)));
                        lines.Add(entry);
                    }
                    return string.Join(Environment.NewLine, lines);
                }).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                File.WriteAllText("artifacts/" + name + "-diagnostic.txt", details);
            }
            Equal(text, pdf ? "Random forest" : "随机森林");
        }
        finally
        {
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true); }
        }
    }
}
