using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TermLens;

internal static partial class Program
{
    private static void HistoryTests()
    {
        Directory.CreateDirectory("artifacts");
        var path = Path.GetFullPath(Path.Combine("artifacts", "history-tests-" + Guid.NewGuid().ToString("N") + ".dat"));
        var answer = ExplanationClient.Parse(Answer, "RF") with { Principle = "先采样，再训练多棵树。", Example = "三棵树中两棵预测是，投票结果为是。", Pitfalls = "更多树不保证消除所有偏差。" };
        try
        {
            Test("历史：加密保存，重建后恢复词句、上下文及详细解释", () =>
            {
                var store = new HistoryStore(path);
                store.Remember("随机森林", "测试原句", answer);
                Equal(store.Warning, "");
                var content = File.ReadAllText(path);
                Assert(!content.Contains("随机森林") && !content.Contains("测试原句") && !content.Contains("三棵树"));
                var restored = new HistoryStore(path);
                Equal(restored.Entries.Count, 1);
                Equal(restored.Entries[0].Term, "随机森林");
                Equal(restored.Entries[0].Context, "测试原句");
                Equal(restored.Entries[0].Answer, answer);
            });
            Test("历史：相同词与上下文去重并置顶，不同上下文保留", () =>
            {
                var store = new HistoryStore(null);
                store.Remember("RF", "A", answer);
                store.Remember("RF", "B", answer);
                store.Remember(" rf ", " A ", answer with { Example = "更新后的例子" });
                Equal(store.Entries.Count, 2);
                Equal(store.Entries[0].Context, "A");
                Equal(store.Entries[0].Answer.Example, "更新后的例子");
            });
            Test("历史：只保留最近100条，过长回答不增加内存记录", () =>
            {
                var store = new HistoryStore(null);
                for (var i = 0; i < 105; i++) store.Remember("term-" + i, "", answer);
                Equal(store.Entries.Count, 100);
                Equal(store.Entries[0].Term, "term-104");
                Equal(store.Entries[^1].Term, "term-5");
                store.Remember("过长", "", answer with { Plain = new string('x', 24001) });
                Equal(store.Entries.Count, 100);
                Assert(store.Warning.Length > 0);
            });
            Test("历史：清空删除本地文件，重建后无旧记录", () =>
            {
                var store = new HistoryStore(path);
                store.Clear();
                Equal(store.Entries.Count, 0);
                Assert(!File.Exists(path));
                Equal(new HistoryStore(path).Entries.Count, 0);
            });
            Test("历史：损坏文件保留，清空后可重新保存", () =>
            {
                File.WriteAllText(path, "not encrypted history");
                var store = new HistoryStore(path);
                Assert(store.Warning.Length > 0);
                store.Remember("RF", "", answer);
                Equal(File.ReadAllText(path), "not encrypted history");
                store.Clear(); store.Remember("RF", "", answer);
                Equal(new HistoryStore(path).Entries[0].Term, "RF");
            });
            Test("历史：超大文件与无效字段不使程序崩溃", () =>
            {
                File.WriteAllText(path, new string('x', 4_000_001));
                var store = new HistoryStore(path);
                Equal(store.Entries.Count, 0); Assert(store.Warning.Length > 0);
                var invalid = new[] { new HistoryEntry("RF", "", answer with { Meaning = null! }, DateTimeOffset.UtcNow) };
                File.WriteAllText(path, SecretProtection.Protect(JsonSerializer.Serialize(invalid)));
                store = new HistoryStore(path);
                Equal(store.Entries.Count, 0); Assert(store.Warning.Length > 0);
            });
            Test("历史：保存失败仍保留本次结果并提示", () =>
            {
                File.Delete(path); Directory.CreateDirectory(path);
                try
                {
                    var store = new HistoryStore(path);
                    store.Remember("RF", "", answer);
                    Equal(store.Entries.Count, 1); Assert(store.Warning.Length > 0);
                }
                finally { Directory.Delete(path); }
            });
            Test("详细解释：完整与流式解析、复制均保留新增内容", () =>
            {
                var detailed = Answer[..^1] + ",\"principle\":\"工作原理\",\"example\":\"具体实例\",\"pitfalls\":\"常见误区\"}";
                var parsed = ExplanationClient.Parse(detailed, "RF");
                Equal(parsed.Principle, "工作原理"); Equal(parsed.Example, "具体实例"); Equal(parsed.Pitfalls, "常见误区");
                Assert(parsed.CopyText.Contains("工作原理") && parsed.CopyText.Contains("具体实例") && parsed.CopyText.Contains("常见误区"));
                Equal(ExplanationClient.ParsePartial(detailed[..^2], "RF").Pitfalls, "常见误区");
            });
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
    }
}
