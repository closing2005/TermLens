using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TermLens;

internal static partial class Program
{
    private sealed class UiHandler : HttpMessageHandler
    {
        public TaskCompletionSource ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastContext = "";
        public bool Cancelled;
        public bool Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var term = data.RootElement.GetProperty("term").GetString();
            LastContext = data.RootElement.GetProperty("context").GetString()!;
            if (Block || term == "旧查询")
                return Events(new GatedStream(Event("{\"definition\":\"这是未完成的解释"), Event("\"}" , "stop") + "data: [DONE]\n\n", ReleaseOld.Task, () => Cancelled = true));
            var result = Answer.Replace("随机森林", "新查询结果");
            return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = result } } } }));
        }
    }
    private static void PumpUntil(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression("condition")] string expression = "")
    {
        var frame = new DispatcherFrame();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || clock.ElapsedMilliseconds > 3000) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new Exception("Timed out: " + expression);
    }
    private static void UiReplacement(App app, LookupWindow popup, UiHandler handler)
    {
        app.Settings = new("https://example.com/v1", "test-only-key", "test-model");
        popup.Query("旧查询", "", new Native.Point(100, 100));
        try { PumpUntil(() => ((TextBlock)popup.FindName("DefinitionText")).Text.Contains("未完成")); }
        catch (Exception ex) { throw new Exception(ex.Message + $"; visible={popup.IsVisible}; message={((TextBlock)popup.FindName("MessageText")).Text}; title={((TextBlock)popup.FindName("TitleText")).Text}"); }
        Assert(!((Button)popup.FindName("CopyButton")).IsEnabled);
        popup.Query("新查询", "", new Native.Point(100, 100));
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        handler.ReleaseOld.TrySetResult();
        PumpUntil(() => handler.Cancelled);
        Equal(((TextBlock)popup.FindName("TitleText")).Text, "新查询结果");
        popup.Dismiss();
    }
    private static void UiContext(App app, LookupWindow popup, UiHandler handler)
    {
        popup.Query("Transformer", "", new Native.Point(100, 100));
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        ((TextBox)popup.FindName("ContextInput")).Text = "The transformer reduces voltage.";
        var click = typeof(LookupWindow).GetMethod("Context_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        click.Invoke(popup, new object[] { popup, new RoutedEventArgs() });
        PumpUntil(() => ((Button)popup.FindName("CopyButton")).IsEnabled);
        Equal(handler.LastContext, "The transformer reduces voltage.");
        handler.Block = true; handler.Cancelled = false;
        handler.ReleaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        popup.Query("等待取消", "", new Native.Point(100, 100));
        PumpUntil(() => ((TextBlock)popup.FindName("DefinitionText")).Text.Contains("未完成"));
        var cancel = typeof(LookupWindow).GetMethod("Cancel_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        cancel.Invoke(popup, new object[] { popup, new RoutedEventArgs() });
        PumpUntil(() => handler.Cancelled);
        Equal(((TextBlock)popup.FindName("MessageText")).Text, "查询已取消。");
        Assert(((Button)popup.FindName("RetryButton")).IsEnabled);
        Assert(!((Button)popup.FindName("CopyButton")).IsEnabled);
        popup.Dismiss();
    }
}
